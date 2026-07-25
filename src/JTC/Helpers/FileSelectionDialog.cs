using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JTC.Services;

namespace JTC.Helpers;

/// <summary>
/// File-picker for multi-file torrents shown at add time. User checks the files they want,
/// unchecked files are added with Priority.DoNotDownload so MonoTorrent skips their pieces
/// (except the ones shared across piece boundaries with kept files).
///
/// Layout is a compact numbered grid ("[ ] 1  [ ] 2  [ ] 3 …") — the usage pattern here
/// is almost always TV-show torrents where users pick by episode number and don't care
/// about the identical repeated title text. Full path + size still live in the hover
/// tooltip for cases where the number alone is ambiguous.
/// </summary>
public static class FileSelectionDialog
{
    public sealed record Entry(int Index, string Path, long Size);

    private const int GridColumns = 10;

    // Leading integer with a common separator (dot / space / underscore / dash) so we
    // catch both "19. Название.mkv" and "S01E19 - Name.mkv" via the E19 group below.
    private static readonly Regex LeadingNumber = new(@"^\s*(\d+)\s*[.\-_\s]", RegexOptions.Compiled);
    // Fallback: SxxEnn pattern, take the episode number.
    private static readonly Regex SeasonEpisode = new(@"[Ss]\d+[Ee](\d+)", RegexOptions.Compiled);

    // Returns null if the user cancelled; otherwise the set of file indices to SKIP
    // (i.e. mark as DoNotDownload). An empty set means "download everything".
    public static async Task<HashSet<int>?> ShowAsync(
        XamlRoot xamlRoot, string torrentName, IReadOnlyList<Entry> files)
    {
        // Extract episode number for each file — leading integer wins, then SxxEnn,
        // finally Entry.Index+1 so files without any parseable number still get a
        // stable label. Then sort by the extracted number so the grid reads 1,2,3…
        // regardless of the torrent's internal file order.
        var rows = files
            .Select(f => new Row(f, ExtractEpisodeNumber(f)))
            .OrderBy(r => r.Number)
            .ThenBy(r => r.Entry.Index)
            .ToList();

        var titleBlock = new TextBlock
        {
            Text = torrentName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
        };
        var totalSize = files.Sum(f => f.Size);
        var summaryBlock = new TextBlock
        {
            Text = $"{files.Count} файлов · {Formatting.BytesToHuman(totalSize)}",
            Opacity = 0.75,
            FontSize = 12,
        };

        var allBtn = new Button { Content = "Всё" };
        var noneBtn = new Button { Content = "Ничего" };
        var separator = new TextBlock
        {
            Text = "·",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.4,
        };
        var counter = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.85,
            FontSize = 12,
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 8),
        };
        toolbar.Children.Add(allBtn);
        toolbar.Children.Add(noneBtn);
        toolbar.Children.Add(separator);
        toolbar.Children.Add(counter);

        // Compact numbered grid. GridColumns fixed so wide dialogs don't stretch a
        // 10-episode row into gargantuan spacing; rows overflow into a ScrollViewer.
        var grid = new Grid
        {
            RowSpacing = 4,
            ColumnSpacing = 4,
        };
        for (int c = 0; c < GridColumns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rowCount = (rows.Count + GridColumns - 1) / GridColumns;
        for (int r = 0; r < rowCount; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var checkboxes = new List<CheckBox>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var cb = new CheckBox
            {
                Content = row.Number.ToString(),
                IsChecked = true,
                MinWidth = 0,
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // Full name + size in the hover tooltip — number alone can be ambiguous when
            // a torrent has both S01E01.mkv and 01.Subs.srt.
            ToolTipService.SetToolTip(cb, $"{row.Entry.Path}  ·  {Formatting.BytesToHuman(row.Entry.Size)}");

            Grid.SetRow(cb, i / GridColumns);
            Grid.SetColumn(cb, i % GridColumns);
            grid.Children.Add(cb);
            checkboxes.Add(cb);
        }

        var scroll = new ScrollViewer
        {
            Content = grid,
            MinHeight = 120,
            MaxHeight = 460,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var content = new StackPanel { MinWidth = 520, MaxWidth = 720, Spacing = 4 };
        content.Children.Add(titleBlock);
        content.Children.Add(summaryBlock);
        content.Children.Add(toolbar);
        content.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = "Выберите файлы для скачивания",
            Content = content,
            PrimaryButtonText = "Скачать выделенные",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            FullSizeDesired = true,
        };

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var cb = checkboxes[i];
            cb.Checked   += (_, _) => { row.IsSelected = true;  RefreshCounter(dialog, rows, counter); };
            cb.Unchecked += (_, _) => { row.IsSelected = false; RefreshCounter(dialog, rows, counter); };
        }
        allBtn.Click  += (_, _) => { foreach (var cb in checkboxes) cb.IsChecked = true;  };
        noneBtn.Click += (_, _) => { foreach (var cb in checkboxes) cb.IsChecked = false; };
        RefreshCounter(dialog, rows, counter);

        ThemeHelper.ApplyToDialog(dialog, ThemeHelper.CurrentTheme);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null;

        var skip = new HashSet<int>();
        foreach (var row in rows)
            if (!row.IsSelected)
                skip.Add(row.Entry.Index);
        return skip;
    }

    private static int ExtractEpisodeNumber(Entry entry)
    {
        var name = System.IO.Path.GetFileName(entry.Path);
        var m = LeadingNumber.Match(name);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n1)) return n1;
        m = SeasonEpisode.Match(name);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n2)) return n2;
        return entry.Index + 1;
    }

    private static void RefreshCounter(ContentDialog dialog, List<Row> rows, TextBlock counter)
    {
        var selected = rows.Where(r => r.IsSelected).ToList();
        var selSize = selected.Sum(r => r.Entry.Size);
        counter.Text = $"Выбрано: {selected.Count} / {rows.Count} · {Formatting.BytesToHuman(selSize)}";
        dialog.IsPrimaryButtonEnabled = selected.Count > 0;
    }

    private sealed class Row
    {
        public Entry Entry  { get; }
        public int   Number { get; }
        public bool  IsSelected { get; set; } = true;
        public Row(Entry entry, int number) { Entry = entry; Number = number; }
    }
}
