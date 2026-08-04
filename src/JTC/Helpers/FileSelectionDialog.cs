using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using JTC.Services;

namespace JTC.Helpers;

/// <summary>
/// File-picker for multi-file torrents shown at add time. User checks the files they want,
/// unchecked files are added with Priority.DoNotDownload so MonoTorrent skips their pieces
/// (except the ones shared across piece boundaries with kept files).
///
/// Layout: one row per file — [checkbox] [episode number badge] [file name] [size].
/// The number badge is a courtesy for TV-show torrents (files sort by extracted episode
/// number when a series pattern matches); the filename is always visible so game/software
/// torrents (where "01" / "02" mean nothing) are readable too. Full path lives in the
/// hover tooltip for cases where a truncated name is ambiguous.
///
/// v0.7.1–v0.7.5 shipped a compact numbers-only grid — turned out to be unusable for
/// non-series torrents (only numbers, no context) — so v0.7.6 brings the name column
/// back while keeping the episode-sort and the counter/toolbar.
/// </summary>
public static class FileSelectionDialog
{
    public sealed record Entry(int Index, string Path, long Size);

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
        // stable label. Then sort by the extracted number so series torrents read
        // 1,2,3… regardless of the .torrent's internal file order; for non-series
        // torrents the fallback = index+1 keeps the order matching the .torrent.
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

        // Per-file row. Grid: [checkbox] [number badge] [filename, ellipsis] [size, right].
        // Kept flat (no ListView / ItemsRepeater) — the list is short (dozens, not
        // thousands), and a StackPanel of Grids gives us pixel-precise column alignment
        // without ListViewItem's chrome, focus rectangles, and reveal-brush overhead.
        var list = new StackPanel { Spacing = 2 };
        var checkboxes = new List<CheckBox>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var cb = new CheckBox
            {
                IsChecked = true,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var numText = new TextBlock
            {
                Text = row.Number.ToString(),
                Width = 32,
                TextAlignment = TextAlignment.Right,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
            };
            var nameText = new TextBlock
            {
                Text = System.IO.Path.GetFileName(row.Entry.Path),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var sizeText = new TextBlock
            {
                Text = Formatting.BytesToHuman(row.Entry.Size),
                Opacity = 0.7,
                TextAlignment = TextAlignment.Right,
                MinWidth = 80,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };

            var rowGrid = new Grid { ColumnSpacing = 8 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(cb,       0); rowGrid.Children.Add(cb);
            Grid.SetColumn(numText,  1); rowGrid.Children.Add(numText);
            Grid.SetColumn(nameText, 2); rowGrid.Children.Add(nameText);
            Grid.SetColumn(sizeText, 3); rowGrid.Children.Add(sizeText);

            // Full path (relative to torrent root) + size in the hover tooltip — the
            // visible name may be trimmed and won't reveal the containing folder.
            ToolTipService.SetToolTip(rowGrid, $"{row.Entry.Path}  ·  {Formatting.BytesToHuman(row.Entry.Size)}");

            list.Children.Add(rowGrid);
            checkboxes.Add(cb);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            MinHeight = 120,
            MaxHeight = 460,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var content = new StackPanel { MinWidth = 640, MaxWidth = 900, Spacing = 4 };
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
            // Auto-size — long file lists scroll internally via the ScrollViewer's
            // MaxHeight cap; short lists don't waste the whole window height.
            FullSizeDesired = false,
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
