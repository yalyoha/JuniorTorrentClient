using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using JTC.Services;

namespace JTC.Helpers;

public static class ThemeHelper
{
    private static readonly Color DarkSolid  = Color.FromArgb(0xFF, 0x21, 0x21, 0x21);
    private static readonly Color LightSolid = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2);

    // Default Colored-theme gradient for fresh installs: the top built-in preset
    // (blue-navy → lime, swapped from pink-orange in v0.5.6 to match the reordered
    // BuiltInColorPresets.All). Used when AppSettings.ColoredTopHex / ColoredBottomHex
    // are null (no settings.json yet) or unparseable.
    private static readonly Color DefaultTop    = MustParseHex(BuiltInColorPresets.BlueTopHex);
    private static readonly Color DefaultBottom = MustParseHex(BuiltInColorPresets.BlueBottomHex);

    /// <summary>
    /// The last-applied theme. Exposed for ContentDialog / other pop-up UI that lives on
    /// the XamlRoot popup surface and doesn't inherit the RootGrid's element-scoped theme.
    /// </summary>
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Colored;

    /// <summary>Top gradient stop for the Colored theme.</summary>
    public static Color CurrentTop { get; private set; } = DefaultTop;

    /// <summary>Bottom gradient stop for the Colored theme.</summary>
    public static Color CurrentBottom { get; private set; } = DefaultBottom;

    /// <summary>Plashka (row) background colour for the Colored theme.</summary>
    public static Color CurrentPlashkaBg { get; private set; } = MustParseHex(DefaultColors.WhitePlashkaBgHex);

    /// <summary>Plashka (row) text colour for the Colored theme.</summary>
    public static Color CurrentPlashkaFg { get; private set; } = MustParseHex(DefaultColors.WhitePlashkaFgHex);

    /// <summary>Toolbar-button corner radius. New TorrentViewModels read this at construction.</summary>
    public static int CurrentButtonCornerRadius { get; private set; } = 15;

    /// <summary>Row (plashka) corner radius. New TorrentViewModels read this at construction.</summary>
    public static int CurrentPlashkaCornerRadius { get; private set; } = 20;

    /// <summary>Update the cached corner-radius values so newly-created row VMs pick them up.</summary>
    public static void SetCornerRadii(int buttonRadius, int plashkaRadius)
    {
        CurrentButtonCornerRadius  = buttonRadius;
        CurrentPlashkaCornerRadius = plashkaRadius;
    }

    /// <summary>Which status indicator variant to show — Circle (dot) or Stripe (bar).</summary>
    public static StatusIndicatorStyle CurrentStatusIndicatorStyle { get; private set; } = StatusIndicatorStyle.Circle;

    public static void SetStatusIndicatorStyle(StatusIndicatorStyle style)
    {
        CurrentStatusIndicatorStyle = style;
    }

    public static bool IsColored(AppTheme t) => t == AppTheme.Colored;

    /// <summary>
    /// Parse a hex color string in one of the forms "#RRGGBB", "#AARRGGBB" (leading '#'
    /// optional). Returns null on any parse failure — callers fall back to sensible defaults.
    /// </summary>
    public static Color? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return null;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return null;
        return Color.FromArgb(
            (byte)((raw >> 24) & 0xFF),
            (byte)((raw >> 16) & 0xFF),
            (byte)((raw >> 8)  & 0xFF),
            (byte)(raw         & 0xFF));
    }

    public static string ToHex(Color c) =>
        $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color MustParseHex(string hex) =>
        TryParseHex(hex) ?? throw new ArgumentException($"invalid hex color: {hex}", nameof(hex));

    /// <summary>
    /// Update the cached Colored-theme colors without rebuilding the whole window. Used by
    /// the settings dialog's live-preview flow — the caller repaints RootGrid / TitleBar /
    /// row VMs after this returns.
    /// </summary>
    public static void SetColoredColors(Color top, Color bottom)
    {
        CurrentTop = top;
        CurrentBottom = bottom;
    }

    /// <summary>Update cached Coloured-theme plashka colours (row background + text).</summary>
    public static void SetColoredPlashka(Color bg, Color fg)
    {
        CurrentPlashkaBg = bg;
        CurrentPlashkaFg = fg;
    }

    /// <summary>Apply user-picked status colours to RowBrushes' static state.</summary>
    public static void SetStatusColors(Color idle, Color downloading, Color seeding, Color hashing, Color error)
    {
        RowBrushes.SetStatusColors(idle, downloading, seeding, hashing, error);
    }

    /// <summary>Resolve a hex string to Color, falling back to a hardcoded default.</summary>
    public static Color HexOrDefault(string? hex, string defaultHex) =>
        TryParseHex(hex) ?? MustParseHex(defaultHex);

    /// <summary>
    /// Full status-colour snapshot pulled from an AppSettings — used by MainWindow at
    /// startup and by the settings dialog's live-preview to apply status colours in
    /// one call. Each null hex falls back to its DefaultColors constant.
    /// </summary>
    public static (Color Idle, Color Downloading, Color Seeding, Color Hashing, Color Error) StatusColorsFrom(AppSettings s) =>
        (HexOrDefault(s.StatusIdleHex,        DefaultColors.StatusIdleHex),
         HexOrDefault(s.StatusDownloadingHex, DefaultColors.StatusDownloadingHex),
         HexOrDefault(s.StatusSeedingHex,     DefaultColors.StatusSeedingHex),
         HexOrDefault(s.StatusHashingHex,     DefaultColors.StatusHashingHex),
         HexOrDefault(s.StatusErrorHex,       DefaultColors.StatusErrorHex));

    /// <summary>
    /// Plashka bg/fg snapshot pulled from AppSettings, with legacy fallback to the
    /// ColoredPlashkaStyle enum for pre-v0.5 records that never wrote the hex fields.
    /// </summary>
    public static (Color Bg, Color Fg) PlashkaColorsFrom(AppSettings s)
    {
        // Newer records: explicit hex wins.
        if (!string.IsNullOrEmpty(s.PlashkaBgHex) && !string.IsNullOrEmpty(s.PlashkaFgHex))
        {
            var bg = TryParseHex(s.PlashkaBgHex);
            var fg = TryParseHex(s.PlashkaFgHex);
            if (bg is not null && fg is not null) return (bg.Value, fg.Value);
        }
        // Legacy fallback: derive from PlashkaStyle enum.
        return s.ColoredPlashkaStyle == PlashkaStyle.Dark
            ? (MustParseHex(DefaultColors.DarkPlashkaBgHex),  MustParseHex(DefaultColors.DarkPlashkaFgHex))
            : (MustParseHex(DefaultColors.WhitePlashkaBgHex), MustParseHex(DefaultColors.WhitePlashkaFgHex));
    }

    private static LinearGradientBrush ColoredGradient() =>
        MakeColoredGradient(CurrentTop, CurrentBottom);

    /// <summary>
    /// Public factory for a top-to-bottom gradient brush — used by the settings dialog's
    /// live-preview code so it can hand a matching gradient to an open ContentDialog.
    /// </summary>
    public static LinearGradientBrush MakeColoredGradient(Color top, Color bottom)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5, 0),
            EndPoint   = new Windows.Foundation.Point(0.5, 1),
        };
        g.GradientStops.Add(new GradientStop { Color = top,    Offset = 0 });
        g.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return g;
    }

    // Primary-accent trio for AccentButton in ContentDialogs. Rule: the top gradient color
    // is the accent (matches the "main brand color" a user picks). Hover and pressed are
    // derived by blending toward white / black — good-enough for arbitrary user colors
    // without a full HSL round-trip.
    private static (Color Base, Color Hover, Color Pressed) AccentColorsForColored()
    {
        var baseColor = CurrentTop;
        return (baseColor, MixTowards(baseColor, White, 0.30), MixTowards(baseColor, Black, 0.30));
    }

    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Black = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

    private static byte Lerp(byte a, byte b, double t) =>
        (byte)Math.Round(a + (b - a) * t);

    private static Color MixTowards(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(from.A, Lerp(from.R, to.R, t), Lerp(from.G, to.G, t), Lerp(from.B, to.B, t));
    }

    // ContentDialog's default template binds BOTH the outer BackgroundElement Border
    // AND the inner CommandSpace Grid to {TemplateBinding Background}. A gradient brush
    // paints per-element (RelativeToBoundingBox), so both regions render their own
    // independent 0..1 gradient — the button strip visibly "restarts" the top color at
    // its own top, creating a discontinuity. We can't decouple them through resources
    // (both go through the same Background property), so we walk the visual tree after
    // Loaded fires, find CommandSpace by x:Name, and clear its Background — then the
    // outer element's continuous gradient shows through.
    //
    // Also overrides AccentButton brushes so the primary "Сохранить" button uses the
    // user's chosen top color instead of the stock lavender WinUI accent.
    public static void ApplyToDialog(ContentDialog dialog, AppTheme theme)
    {
        // Idempotent: strip any prior Colored-scope overrides first so a live-preview
        // theme switch (Colored → Dark → back to Colored, etc.) starts from a clean
        // slate. Without this, resources set on the previous ApplyToDialog call linger
        // and the new theme inherits stale gradient / accent brushes.
        ClearColoredDialogOverrides(dialog);

        dialog.RequestedTheme = theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

        // Dialogs live on the XamlRoot popup surface, which does not inherit FontFamily
        // from RootGrid. Setting it directly guarantees the app font regardless.
        if (Application.Current.Resources["AppFontFamily"] is FontFamily appFont)
            dialog.FontFamily = appFont;

        // Enable scrolling inside the dialog's content area. The default WinUI
        // ContentDialog template wraps its Content in a ContentScrollViewer with BOTH
        // scroll bars disabled — so a dialog with tall content on a small window (our
        // MinWindowHeight is 360 DIP) just clips the bottom and hides the Save/Cancel
        // buttons. Turning both scroll bars to Auto lets the user reach every control;
        // Save/Cancel live in the separate CommandSpace strip and stay pinned regardless.
        RunNowOrOnLoad(dialog, () =>
        {
            if (FindDescendantByName(dialog, "ContentScrollViewer") is Microsoft.UI.Xaml.Controls.ScrollViewer csv)
            {
                csv.VerticalScrollBarVisibility   = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
                csv.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
            }
        });

        // Force AccentButton text to white for EVERY theme. On some Windows 10 systems
        // the OS-chosen accent colour is light enough that WinUI picks a dark foreground
        // brush for contrast, and the "Сохранить" text on the primary button ends up
        // nearly invisible against our styled backgrounds (screenshots/windows10.jpg).
        // White reads reliably against Colored's user-picked accent, Dark's system blue,
        // and Light's system blue alike.
        var whiteBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        dialog.Resources["AccentButtonForeground"]            = whiteBrush;
        dialog.Resources["AccentButtonForegroundPointerOver"] = whiteBrush;
        dialog.Resources["AccentButtonForegroundPressed"]     = whiteBrush;
        // Belt-and-braces: some template snapshots the ThemeResource lookup at
        // ApplyTemplate time and doesn't repaint on Resources changes — reach in
        // once the visual tree is materialized and set Foreground directly too.
        // RunNowOrOnLoad handles both the "dialog not yet loaded" (initial show) case
        // AND the "dialog already loaded" case (live-preview theme change through
        // the settings ComboBox) — plain dialog.Loaded += only fires once and would
        // silently no-op on the second path.
        RunNowOrOnLoad(dialog, () =>
        {
            if (FindDescendantByName(dialog, "PrimaryButton") is Control pb)
                pb.Foreground = whiteBrush;
        });

        if (!IsColored(theme))
            return;

        var transparent   = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        var white         = new SolidColorBrush(White);
        var (accBase, accHover, accPress) = AccentColorsForColored();
        var accentBrush   = new SolidColorBrush(accBase);
        var accentBrushH  = new SolidColorBrush(accHover);
        var accentBrushP  = new SolidColorBrush(accPress);

        dialog.Background = ColoredGradient();
        dialog.Foreground = white;
        dialog.Resources["ContentDialogForeground"]  = white;
        dialog.Resources["ContentDialogBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        // Primary button ("Сохранить" / "Удалить" / "Добавить") — top-color accent.
        dialog.Resources["AccentButtonBackground"]              = accentBrush;
        dialog.Resources["AccentButtonBackgroundPointerOver"]   = accentBrushH;
        dialog.Resources["AccentButtonBackgroundPressed"]       = accentBrushP;
        dialog.Resources["AccentButtonForeground"]              = white;
        dialog.Resources["AccentButtonForegroundPointerOver"]   = white;
        dialog.Resources["AccentButtonForegroundPressed"]       = white;
        dialog.Resources["AccentButtonBorderBrush"]             = transparent;

        // Input surfaces — a uniform translucent-black overlay tints whatever gradient
        // color is underneath, so TextBox / NumberBox / ComboBox read as one visual family
        // regardless of where they sit on the gradient. Border stays constant across
        // states so the three fields behave identically.
        var inputBg      = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
        var inputBgHover = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
        var inputBgFocus = new SolidColorBrush(Color.FromArgb(0x77, 0, 0, 0));
        var inputBorder  = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        // TextBox + NumberBox (inner input) — TextControl* keys.
        dialog.Resources["TextControlBackground"]              = inputBg;
        dialog.Resources["TextControlBackgroundPointerOver"]   = inputBgHover;
        dialog.Resources["TextControlBackgroundFocused"]       = inputBgFocus;
        dialog.Resources["TextControlBackgroundDisabled"]      = inputBg;
        dialog.Resources["TextControlForeground"]              = white;
        dialog.Resources["TextControlForegroundPointerOver"]   = white;
        dialog.Resources["TextControlForegroundFocused"]       = white;
        dialog.Resources["TextControlForegroundDisabled"]      = white;
        dialog.Resources["TextControlBorderBrush"]             = inputBorder;
        dialog.Resources["TextControlBorderBrushPointerOver"]  = inputBorder;
        dialog.Resources["TextControlBorderBrushFocused"]      = inputBorder;
        dialog.Resources["TextControlBorderBrushDisabled"]     = inputBorder;

        // ComboBox — separate resource family from TextBox.
        dialog.Resources["ComboBoxBackground"]                 = inputBg;
        dialog.Resources["ComboBoxBackgroundPointerOver"]      = inputBgHover;
        dialog.Resources["ComboBoxBackgroundPressed"]          = inputBgFocus;
        dialog.Resources["ComboBoxBackgroundFocused"]          = inputBgFocus;
        dialog.Resources["ComboBoxBackgroundUnfocused"]        = inputBg;
        dialog.Resources["ComboBoxBackgroundDisabled"]         = inputBg;
        dialog.Resources["ComboBoxForeground"]                 = white;
        dialog.Resources["ComboBoxForegroundPointerOver"]      = white;
        dialog.Resources["ComboBoxForegroundPressed"]          = white;
        dialog.Resources["ComboBoxForegroundFocused"]          = white;
        dialog.Resources["ComboBoxForegroundDisabled"]         = white;
        dialog.Resources["ComboBoxBorderBrush"]                = inputBorder;
        dialog.Resources["ComboBoxBorderBrushPointerOver"]     = inputBorder;
        dialog.Resources["ComboBoxBorderBrushPressed"]         = inputBorder;
        dialog.Resources["ComboBoxBorderBrushFocused"]         = inputBorder;
        dialog.Resources["ComboBoxBorderBrushUnfocused"]       = inputBorder;
        dialog.Resources["ComboBoxBorderBrushDisabled"]        = inputBorder;

        // ComboBox open dropdown — same brand gradient as the dialog so the flyout
        // reads as an extension of the surface, not a stray dark WinUI panel.
        var itemHover    = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var itemPressed  = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        var itemSelected = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        dialog.Resources["ComboBoxDropDownBackground"]                   = ColoredGradient();
        dialog.Resources["ComboBoxDropDownBackgroundPointerOver"]        = ColoredGradient();
        dialog.Resources["ComboBoxDropDownBackgroundPointerPressed"]     = ColoredGradient();
        dialog.Resources["ComboBoxDropDownForeground"]                   = white;
        dialog.Resources["ComboBoxDropDownBorderBrush"]                  = inputBorder;

        dialog.Resources["ComboBoxItemBackground"]                       = transparent;
        dialog.Resources["ComboBoxItemBackgroundPointerOver"]            = itemHover;
        dialog.Resources["ComboBoxItemBackgroundPressed"]                = itemPressed;
        dialog.Resources["ComboBoxItemBackgroundSelected"]               = itemSelected;
        dialog.Resources["ComboBoxItemBackgroundSelectedUnfocused"]      = itemSelected;
        dialog.Resources["ComboBoxItemBackgroundSelectedPointerOver"]    = itemHover;
        dialog.Resources["ComboBoxItemBackgroundSelectedPressed"]        = itemPressed;
        dialog.Resources["ComboBoxItemForeground"]                       = white;
        dialog.Resources["ComboBoxItemForegroundPointerOver"]            = white;
        dialog.Resources["ComboBoxItemForegroundPressed"]                = white;
        dialog.Resources["ComboBoxItemForegroundSelected"]               = white;
        dialog.Resources["ComboBoxItemForegroundSelectedUnfocused"]      = white;
        dialog.Resources["ComboBoxItemForegroundSelectedPointerOver"]    = white;
        dialog.Resources["ComboBoxItemForegroundSelectedPressed"]        = white;
        dialog.Resources["ComboBoxItemForegroundDisabled"]               = white;

        // Kill CommandSpace's background so the outer BackgroundElement's gradient is
        // the ONLY paint layer — otherwise both regions render the same brush
        // independently (RelativeToBoundingBox) and the button strip visibly restarts
        // the gradient. RunNowOrOnLoad handles the same two cases as PrimaryButton
        // above (initial show vs. live-preview switch back to Colored on an already-
        // loaded dialog).
        RunNowOrOnLoad(dialog, () =>
        {
            if (FindDescendantByName(dialog, "CommandSpace") is Panel csPanel)
                csPanel.Background = transparent;
        });
    }

    private static void RunNowOrOnLoad(FrameworkElement el, Action action)
    {
        if (el.IsLoaded) action();
        else el.Loaded += (_, _) => action();
    }

    // Resource keys that ApplyToDialog's Colored branch sets. Listed here so we can
    // strip them all in one pass when re-applying for a different theme; keeping a
    // constant list is fragile-looking but at least the alternative (per-key removal
    // scattered through code) would drift out of sync silently. If ApplyToDialog adds
    // a new resource, mirror it here.
    private static readonly string[] ColoredDialogResourceKeys = new[]
    {
        "ContentDialogForeground", "ContentDialogBorderBrush",
        "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
        "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
        "AccentButtonBorderBrush",
        "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
        "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "TextControlForegroundDisabled",
        "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
        "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxBackgroundFocused", "ComboBoxBackgroundUnfocused", "ComboBoxBackgroundDisabled",
        "ComboBoxForeground", "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxForegroundFocused", "ComboBoxForegroundDisabled",
        "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxBorderBrushFocused", "ComboBoxBorderBrushUnfocused", "ComboBoxBorderBrushDisabled",
        "ComboBoxDropDownBackground", "ComboBoxDropDownBackgroundPointerOver", "ComboBoxDropDownBackgroundPointerPressed",
        "ComboBoxDropDownForeground", "ComboBoxDropDownBorderBrush",
        "ComboBoxItemBackground", "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed",
        "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedUnfocused", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed",
        "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed",
        "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedUnfocused", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed",
        "ComboBoxItemForegroundDisabled",
    };

    private static void ClearColoredDialogOverrides(ContentDialog dialog)
    {
        foreach (var key in ColoredDialogResourceKeys)
            dialog.Resources.Remove(key);
        dialog.ClearValue(ContentDialog.BackgroundProperty);
        dialog.ClearValue(ContentDialog.ForegroundProperty);
        if (FindDescendantByName(dialog, "PrimaryButton") is Control pb)
        {
            pb.ClearValue(Control.BackgroundProperty);
            pb.ClearValue(Control.ForegroundProperty);
        }
        if (FindDescendantByName(dialog, "CommandSpace") is Panel cs)
            cs.ClearValue(Panel.BackgroundProperty);
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return fe;
            var deep = FindDescendantByName(child, name);
            if (deep is not null)
                return deep;
        }
        return null;
    }

    /// <summary>
    /// Re-paints an already-open Colored-theme ContentDialog with a new gradient. Used
    /// by the settings dialog's live-preview flow — as the user drags a color picker,
    /// this keeps the dialog surface in sync with the main window instead of staying
    /// frozen at the colors it opened with. AccentButton's Normal-state Background is
    /// updated directly on the visual tree because the ContentDialog template snapshots
    /// its ThemeResource lookup at ApplyTemplate time and doesn't re-resolve on
    /// Resources-dictionary mutation.
    /// </summary>
    public static void RepaintColoredDialog(ContentDialog dialog, Color top, Color bottom)
    {
        dialog.Background = MakeColoredGradient(top, bottom);

        // Setting dialog.Background propagates through {TemplateBinding Background} to
        // BOTH the outer BackgroundElement Border AND the inner CommandSpace Grid — so
        // the button strip's own gradient reappears after every live-preview colour
        // change even though ApplyToDialog's initial Loaded handler cleared it. Re-kill
        // CommandSpace.Background here so the continuous top-to-bottom gradient stays
        // continuous while the user drags a colour picker.
        var transparentBrush = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        if (FindDescendantByName(dialog, "CommandSpace") is Panel csPanel)
            csPanel.Background = transparentBrush;

        var accentBase   = new SolidColorBrush(top);
        var accentHover  = new SolidColorBrush(MixTowards(top, White, 0.30));
        var accentPress  = new SolidColorBrush(MixTowards(top, Black, 0.30));

        dialog.Resources["AccentButtonBackground"]            = accentBase;
        dialog.Resources["AccentButtonBackgroundPointerOver"] = accentHover;
        dialog.Resources["AccentButtonBackgroundPressed"]     = accentPress;

        dialog.Resources["ComboBoxDropDownBackground"]               = MakeColoredGradient(top, bottom);
        dialog.Resources["ComboBoxDropDownBackgroundPointerOver"]    = MakeColoredGradient(top, bottom);
        dialog.Resources["ComboBoxDropDownBackgroundPointerPressed"] = MakeColoredGradient(top, bottom);

        if (FindDescendantByName(dialog, "PrimaryButton") is Control pb)
        {
            pb.Background = accentBase;
            pb.Foreground = new SolidColorBrush(White);
        }
    }

    /// <summary>
    /// Full theme apply: window background, element theme, row palette, menu-flyout brushes.
    /// For AppTheme.Colored the caller may pass explicit gradient colours + plashka bg/fg;
    /// omitted values fall through to the previously-cached ones so live-preview code paths
    /// can update just one dimension at a time.
    /// </summary>
    public static void Apply(FrameworkElement root, AppTheme theme,
        Color? top = null, Color? bottom = null,
        Color? plashkaBg = null, Color? plashkaFg = null)
    {
        if (theme == AppTheme.Colored)
        {
            SetColoredColors(top ?? CurrentTop, bottom ?? CurrentBottom);
            SetColoredPlashka(plashkaBg ?? CurrentPlashkaBg, plashkaFg ?? CurrentPlashkaFg);
        }

        // Palette first — TorrentViewModels created after this point will read the correct
        // brushes on construction. Existing rows are refreshed by the caller (MainWindow
        // calls Refresh + RefreshBrushes after a settings save).
        if (theme == AppTheme.Colored)
            RowBrushes.SetColored(CurrentPlashkaBg, CurrentPlashkaFg);
        else
            RowBrushes.Set(theme);

        CurrentTheme = theme;

        if (root is Panel panel)
        {
            switch (theme)
            {
                case AppTheme.Dark:
                    panel.Background = new SolidColorBrush(DarkSolid);
                    root.RequestedTheme = ElementTheme.Dark;
                    break;
                case AppTheme.Light:
                    panel.Background = new SolidColorBrush(LightSolid);
                    root.RequestedTheme = ElementTheme.Light;
                    break;
                default: // Colored
                    panel.Background = ColoredGradient();
                    root.RequestedTheme = ElementTheme.Dark;
                    break;
            }
        }

        // Context-menu (MenuFlyout) brushes — the row right-click flyout renders on the
        // XamlRoot popup surface, so ThemeResource lookup from the MenuFlyoutPresenter
        // does NOT traverse back through RootGrid. Application.Current.Resources is at
        // the root of every lookup chain, so put overrides there.
        if (IsColored(theme))
            ApplyColoredMenuFlyoutBrushes();
        else
            ClearColoredMenuFlyoutBrushes();
    }

    private static readonly string[] ColoredMenuFlyoutKeys = new[]
    {
        "MenuFlyoutPresenterBackground",
        "MenuFlyoutPresenterBorderBrush",
        "MenuFlyoutItemForeground",
        "MenuFlyoutItemForegroundPointerOver",
        "MenuFlyoutItemForegroundPressed",
        "MenuFlyoutItemBackground",
        "MenuFlyoutItemBackgroundPointerOver",
        "MenuFlyoutItemBackgroundPressed",
        "MenuFlyoutSeparatorBackground",
    };

    private static void ApplyColoredMenuFlyoutBrushes()
    {
        var res = Application.Current.Resources;

        var white       = new SolidColorBrush(White);
        var transparent = new SolidColorBrush(Color.FromArgb(0x00, 0, 0, 0));
        var hover       = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var pressed     = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        var border      = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        var separator   = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        res["MenuFlyoutPresenterBackground"]       = ColoredGradient();
        res["MenuFlyoutPresenterBorderBrush"]      = border;
        res["MenuFlyoutItemForeground"]            = white;
        res["MenuFlyoutItemForegroundPointerOver"] = white;
        res["MenuFlyoutItemForegroundPressed"]     = white;
        res["MenuFlyoutItemBackground"]            = transparent;
        res["MenuFlyoutItemBackgroundPointerOver"] = hover;
        res["MenuFlyoutItemBackgroundPressed"]     = pressed;
        res["MenuFlyoutSeparatorBackground"]       = separator;
    }

    private static void ClearColoredMenuFlyoutBrushes()
    {
        var res = Application.Current.Resources;
        foreach (var key in ColoredMenuFlyoutKeys)
            res.Remove(key);
    }

    /// <summary>
    /// Applies caption-button (minimize / maximize / close) colors that match the active
    /// theme. Windows would otherwise dim inactive-state icons to grey, which reads as
    /// "out of style" on Colored and Dark themes (icons blend into the background) and
    /// inversely, active white-on-white icons vanish on Light theme.
    /// </summary>
    public static void ApplyToTitleBar(Microsoft.UI.Windowing.AppWindowTitleBar titleBar, AppTheme theme)
    {
        var transparent = (Color?)Color.FromArgb(0, 0, 0, 0);

        if (theme == AppTheme.Light)
        {
            var dark      = Color.FromArgb(0xFF, 0x21, 0x21, 0x21);
            var hoverBg   = Color.FromArgb(0x15, 0x00, 0x00, 0x00);
            var pressedBg = Color.FromArgb(0x25, 0x00, 0x00, 0x00);

            titleBar.ButtonForegroundColor            = dark;
            titleBar.ButtonInactiveForegroundColor    = dark;
            titleBar.ButtonHoverForegroundColor       = dark;
            titleBar.ButtonPressedForegroundColor     = dark;
            titleBar.ButtonBackgroundColor            = transparent;
            titleBar.ButtonInactiveBackgroundColor    = transparent;
            titleBar.ButtonHoverBackgroundColor       = hoverBg;
            titleBar.ButtonPressedBackgroundColor     = pressedBg;
        }
        else
        {
            var white     = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            var hoverBg   = Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF);
            var pressedBg = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

            titleBar.ButtonForegroundColor            = white;
            titleBar.ButtonInactiveForegroundColor    = white;
            titleBar.ButtonHoverForegroundColor       = white;
            titleBar.ButtonPressedForegroundColor     = white;
            titleBar.ButtonBackgroundColor            = transparent;
            titleBar.ButtonInactiveBackgroundColor    = transparent;
            titleBar.ButtonHoverBackgroundColor       = hoverBg;
            titleBar.ButtonPressedBackgroundColor     = pressedBg;
        }
    }
}
