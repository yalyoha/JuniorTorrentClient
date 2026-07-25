using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JTC.Services;

namespace JTC.Helpers;

/// <summary>
/// File-picker for multi-file torrents shown at add time. User checks the files they want,
/// unchecked files are added with Priority.DoNotDownload so MonoTorrent skips their pieces
/// (except the ones shared across piece boundaries with kept files).
/// </summary>
public static class FileSelectionDialog
{
    public sealed record Entry(int Index, string Path, long Size);

    // Returns null if the user cancelled; otherwise the set of file indices to SKIP
    // (i.e. mark as DoNotDownload). An empty set means "download everything".
    public static async Task<HashSet<int>?> ShowAsync(
        XamlRoot xamlRoot, string torrentName, IReadOnlyList<Entry> files)
    {
        var rows = files.Select(f => new Row(f)).ToList();

        // Header — torrent name + aggregate summary.
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

        // Toolbar — "Всё" / "Ничего" + live counter. Counter uses TextTrimming so a very
        // long torrent summary can't extend past the dialog's right edge.
        var allBtn = new Button { Content = "Всё" };
        var noneBtn = new Button { Content = "Ничего" };
        // Buttons + live counter on one horizontal line, all left-aligned. Prior attempts
        // used a Grid with Auto/Star/Auto columns to push the counter to the right edge, but
        // ContentDialog's Content presenter clips a few px on the right regardless of the
        // stated content width — the trailing portion of the counter was always shaved. Left
        // alignment sidesteps the issue entirely and reads naturally as "action | status".
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

        // File list — StackPanel of Grids inside a ScrollViewer. Each row has three columns:
        // checkbox, file-name text (star, trimmed), size (fixed). Splitting the CheckBox
        // content out into its own TextBlock lets us apply TextTrimming so long paths get
        // ellipsized inside the star column instead of extending the row past the viewport.
        var listPanel = new StackPanel { Spacing = 2 };
        var checkboxes = new List<CheckBox>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = new Grid { Padding = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            var cb = new CheckBox
            {
                IsChecked = true,
                MinWidth = 32,
                MinHeight = 24,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cb, 0);
            line.Children.Add(cb);
            checkboxes.Add(cb);

            var nameText = new TextBlock
            {
                Text = row.Entry.Path,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 12, 0),
            };
            Grid.SetColumn(nameText, 1);
            line.Children.Add(nameText);

            var sizeText = new TextBlock
            {
                Text = Formatting.BytesToHuman(row.Entry.Size),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75,
                FontSize = 12,
            };
            Grid.SetColumn(sizeText, 2);
            line.Children.Add(sizeText);

            listPanel.Children.Add(line);
        }

        // Always-visible vertical scrollbar so a partially-visible last row can't look like a
        // rendering bug. Auto is fluent-idiomatic but hides the bar until hover, which reads
        // as "there's a half-row here for some reason" on first sight.
        var scroll = new ScrollViewer
        {
            Content = listPanel,
            MinHeight = 200,
            MaxHeight = 460,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };

        // MaxWidth caps the dialog so it can't grow past the point where the toolbar counter
        // would clip against the dialog border. MinWidth stays comfortable for short lists.
        var content = new StackPanel { MinWidth = 560, MaxWidth = 720, Spacing = 4 };
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
            // FullSizeDesired stretches the dialog to the parent window's full available
            // area — without it, on a non-maximised window (default 1000×640) the dialog's
            // content region collapses to ~400 px tall and the ScrollViewer below its
            // MaxHeight, so long file lists look clipped. The scrollbar still works, but
            // users read the shortened list as "some files are missing". FullSizeDesired
            // is the WinUI-idiomatic switch for long content dialogs.
            FullSizeDesired = true,
        };

        // Wire event handlers AFTER dialog exists so the closures can reference it safely.
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

    private static void RefreshCounter(ContentDialog dialog, List<Row> rows, TextBlock counter)
    {
        var selected = rows.Where(r => r.IsSelected).ToList();
        var selSize = selected.Sum(r => r.Entry.Size);
        // Compact format — "Выбрано: 24 / 24 (14.73 GB)" ran past the dialog's right border
        // on small dialogs. Middle dot separator + no parens shaves ~4 characters.
        counter.Text = $"Выбрано: {selected.Count} / {rows.Count} · {Formatting.BytesToHuman(selSize)}";
        dialog.IsPrimaryButtonEnabled = selected.Count > 0;
    }

    // Plain mutable wrapper — CheckBox handlers write IsSelected, RefreshCounter reads it.
    private sealed class Row
    {
        public Entry Entry { get; }
        public bool IsSelected { get; set; } = true;
        public Row(Entry entry) => Entry = entry;
    }
}
