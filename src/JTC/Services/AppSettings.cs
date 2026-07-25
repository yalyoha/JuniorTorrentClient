using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JTC.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme
{
    // Gradient theme with user-controlled top+bottom colors. Legacy JSON values "Brand"
    // and "Brand2" from earlier builds are migrated to Colored in SettingsStore.Load,
    // preserving their original palette via ColoredTopHex/ColoredBottomHex.
    Colored,
    Dark,
    Light,
}

/// <summary>
/// Row-background ("плашка") style used inside the Colored theme. Dark and Light themes
/// always use their own palette; this only differentiates whether the gradient window
/// hosts white rows with dark text OR dark rows with white text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlashkaStyle
{
    White,
    Dark,
}

/// <summary>
/// A named colour set (window gradient + plashka + status colours) that the user can save
/// from the settings dialog and re-apply later. All hex fields are #AARRGGBB.
///
/// Legacy pre-v0.5.1 presets only stored TopHex + BottomHex; the newer fields are nullable
/// so those records still deserialize cleanly, and preset-selection code only applies a
/// field when it's present (so a built-in gradient-only preset leaves the current plashka
/// and status colours untouched).
/// </summary>
public sealed record ColorPreset
{
    public string Name { get; init; } = "";
    public string TopHex { get; init; } = "";
    public string BottomHex { get; init; } = "";

    // v0.5.1+ — full-palette presets. Optional so pre-v0.5.1 presets keep working and
    // built-in presets can specify only the gradient (leaving plashka / status current).
    public string? PlashkaBgHex        { get; init; }
    public string? PlashkaFgHex        { get; init; }
    public string? StatusIdleHex       { get; init; }
    public string? StatusDownloadingHex{ get; init; }
    public string? StatusSeedingHex    { get; init; }
    public string? StatusHashingHex    { get; init; }
    public string? StatusErrorHex      { get; init; }
}

/// <summary>
/// Built-in color presets that always appear at the top of the preset list in the settings
/// dialog. "Юниор" (blue → lime — the brand palette, doubles as fresh-install default) and
/// "Фламинго" (pink → orange) are the historical brand pair, originally shipped as
/// "Фирменная 1/2", then descriptive "Сине-зелёная" / "Розово-оранжевая", renamed in v0.6
/// so the built-in list reads as one coherent set of names.
/// </summary>
public static class BuiltInColorPresets
{
    public const string FlamingoName = "Фламинго";
    public const string YuniorName   = "Юниор";

    public const string PinkTopHex     = "#FFE52E71";
    public const string PinkBottomHex  = "#FFFF8A00";
    public const string BlueTopHex     = "#FF324166";
    public const string BlueBottomHex  = "#FF7AB317";

    // Ten built-in themes, each a fully-specified colour set (gradient + plashka + all
    // 5 status colours). Users can pick any and start with a coherent look; they can
    // still tweak individual swatches after selection. All 10 stay listed at the top
    // of the ComboBox and are non-editable / non-deletable (edit + delete apply only
    // to user-saved presets appended below them).
    //
    // v0.5.6 note: Юниор (blue → lime, the brand palette) is intentionally first — it
    // doubles as the default palette for fresh installs (see ThemeHelper.DefaultTop /
    // DefaultBottom). Фламинго (pink → orange) is the legacy default from v0.3-v0.5.5,
    // kept as #2.
    public static readonly IReadOnlyList<ColorPreset> All = new[]
    {
        new ColorPreset { Name = YuniorName,            TopHex = BlueTopHex,     BottomHex = BlueBottomHex,
            PlashkaBgHex = "#FFFFFFFF", PlashkaFgHex = "#FF212121",
            StatusIdleHex        = "#FF90A4AE", StatusDownloadingHex = "#FFFF9100",
            StatusSeedingHex     = "#FF00E676", StatusHashingHex     = "#FF2979FF",
            StatusErrorHex       = "#FFFF1744" },
        new ColorPreset { Name = FlamingoName,          TopHex = PinkTopHex,     BottomHex = PinkBottomHex,
            PlashkaBgHex = "#FFFFFFFF", PlashkaFgHex = "#FF212121",
            StatusIdleHex        = "#FF90A4AE", StatusDownloadingHex = "#FFFF9100",
            StatusSeedingHex     = "#FF00E676", StatusHashingHex     = "#FF2979FF",
            StatusErrorHex       = "#FFFF1744" },
        // ---- 8 new themes (v0.5.4) ----
        new ColorPreset { Name = "Закат",               TopHex = "#FFFF6F00",    BottomHex = "#FFB71C1C",
            PlashkaBgHex = "#FF1E1E1E", PlashkaFgHex = "#FFFFEBCD",
            StatusIdleHex        = "#FFBCAAA4", StatusDownloadingHex = "#FFFFAB40",
            StatusSeedingHex     = "#FFFFEB3B", StatusHashingHex     = "#FFFF80AB",
            StatusErrorHex       = "#FFFF1744" },
        new ColorPreset { Name = "Океан",               TopHex = "#FF01579B",    BottomHex = "#FF00838F",
            PlashkaBgHex = "#FFFFFFFF", PlashkaFgHex = "#FF212121",
            StatusIdleHex        = "#FF90A4AE", StatusDownloadingHex = "#FF0091EA",
            StatusSeedingHex     = "#FF00E5FF", StatusHashingHex     = "#FF00B8D4",
            StatusErrorHex       = "#FFFF1744" },
        new ColorPreset { Name = "Фиолетовый мрак",    TopHex = "#FF311B92",    BottomHex = "#FFAA00FF",
            PlashkaBgHex = "#FF212121", PlashkaFgHex = "#FFF3E5F5",
            StatusIdleHex        = "#FF9575CD", StatusDownloadingHex = "#FFE040FB",
            StatusSeedingHex     = "#FF7C4DFF", StatusHashingHex     = "#FF651FFF",
            StatusErrorHex       = "#FFFF3D00" },
        new ColorPreset { Name = "Мятная свежесть",    TopHex = "#FF00897B",    BottomHex = "#FF80DEEA",
            PlashkaBgHex = "#FFFAFAFA", PlashkaFgHex = "#FF1B1B1B",
            StatusIdleHex        = "#FFB0BEC5", StatusDownloadingHex = "#FFFF6D00",
            StatusSeedingHex     = "#FF64DD17", StatusHashingHex     = "#FF00B8D4",
            StatusErrorHex       = "#FFD50000" },
        new ColorPreset { Name = "Лес",                 TopHex = "#FF1B5E20",    BottomHex = "#FFC0CA33",
            PlashkaBgHex = "#FFFAFAFA", PlashkaFgHex = "#FF212121",
            StatusIdleHex        = "#FF8D6E63", StatusDownloadingHex = "#FFFFC107",
            StatusSeedingHex     = "#FF43A047", StatusHashingHex     = "#FF00ACC1",
            StatusErrorHex       = "#FFE53935" },
        new ColorPreset { Name = "Киберпанк",          TopHex = "#FFE91E63",    BottomHex = "#FF00E5FF",
            PlashkaBgHex = "#FF0F0F0F", PlashkaFgHex = "#FFEEFF41",
            StatusIdleHex        = "#FF546E7A", StatusDownloadingHex = "#FFFF00E5",
            StatusSeedingHex     = "#FF00FF88", StatusHashingHex     = "#FF00E5FF",
            StatusErrorHex       = "#FFFF073F" },
        new ColorPreset { Name = "Кофе",                TopHex = "#FF3E2723",    BottomHex = "#FFD7CCC8",
            PlashkaBgHex = "#FFF5EDE0", PlashkaFgHex = "#FF3E2723",
            StatusIdleHex        = "#FFA1887F", StatusDownloadingHex = "#FFFFB300",
            StatusSeedingHex     = "#FF8BC34A", StatusHashingHex     = "#FF00838F",
            StatusErrorHex       = "#FFBF360C" },
        new ColorPreset { Name = "Северное сияние",   TopHex = "#FF1A237E",    BottomHex = "#FF00E676",
            PlashkaBgHex = "#FF0D1B2A", PlashkaFgHex = "#FFE8EAF6",
            StatusIdleHex        = "#FF7986CB", StatusDownloadingHex = "#FF00B0FF",
            StatusSeedingHex     = "#FF69F0AE", StatusHashingHex     = "#FF7C4DFF",
            StatusErrorHex       = "#FFFF5252" },
    };
}

/// <summary>
/// Baked-in default hex values for the plashka + status colour groups. Kept as public
/// constants so the settings dialog can offer a "reset to default" gesture and so
/// SettingsStore.Load can migrate legacy (pre-v0.5) rows that never wrote these fields.
/// </summary>
public static class DefaultColors
{
    // Plashka bg/fg — defaults for the "White plashka" look. Dark-plashka defaults are
    // derived at load time (see SettingsStore) when ColoredPlashkaStyle == Dark.
    public const string WhitePlashkaBgHex = "#FFFFFFFF";
    public const string WhitePlashkaFgHex = "#FF212121";
    public const string DarkPlashkaBgHex  = "#FF2A2A2A";
    public const string DarkPlashkaFgHex  = "#FFFFFFFF";

    // Status stripe hues — Material A400 across the board so they read on any bg.
    public const string StatusIdleHex        = "#FF90A4AE";
    public const string StatusDownloadingHex = "#FFFF9100";
    public const string StatusSeedingHex     = "#FF00E676";
    public const string StatusHashingHex     = "#FF2979FF";
    public const string StatusErrorHex       = "#FFFF1744";
}

public sealed record AppSettings
{
    public string? LastDownloadDir { get; init; }
    public int MaxSimultaneousDownloads { get; init; } = 3;
    public AppTheme Theme { get; init; } = AppTheme.Colored;

    // For AppTheme.Colored: current gradient endpoint colors (hex #AARRGGBB). Null on
    // fresh installs → ThemeHelper falls back to the first built-in preset.
    public string? ColoredTopHex { get; init; }
    public string? ColoredBottomHex { get; init; }

    // For AppTheme.Colored: which row-background palette to use over the gradient.
    // Legacy hint from v0.4.8; still used by SettingsStore.Load to derive PlashkaBgHex /
    // PlashkaFgHex when those newer fields are absent (pre-v0.5 settings.json).
    public PlashkaStyle ColoredPlashkaStyle { get; init; } = PlashkaStyle.White;

    // For AppTheme.Colored: plashka (row) background + foreground text colours. v0.5+
    // makes these user-picked (see DefaultColors for the two ship-defaults). Null on
    // pre-v0.5 records → SettingsStore.Load fills them in from ColoredPlashkaStyle.
    public string? PlashkaBgHex { get; init; }
    public string? PlashkaFgHex { get; init; }

    // Status-stripe colours. Applied globally regardless of theme — a torrent's state
    // is orthogonal to the window backdrop. Null on pre-v0.5 records → defaults kick
    // in at load time. The row's "progress bar" gradient fill is a 17%-opacity
    // composite of the row's current status colour over the plashka bg, so changing
    // e.g. StatusDownloading tints every downloading row's fill accordingly.
    public string? StatusIdleHex        { get; init; }
    public string? StatusDownloadingHex { get; init; }
    public string? StatusSeedingHex     { get; init; }
    public string? StatusHashingHex     { get; init; }
    public string? StatusErrorHex       { get; init; }

    // User-saved color-preset library. Built-in presets live in BuiltInColorPresets and
    // are prepended in the settings dialog — they are not persisted here to avoid drift
    // if we ever tweak the defaults in a future release.
    public List<ColorPreset> CustomPresets { get; init; } = new();

    // v0.5.4+ — when true (default), an available update is downloaded automatically,
    // the installer is launched in /VERYSILENT mode, and JTC relaunches after the
    // background install finishes. When false, the classic "Обновить / Позже" dialog
    // still appears with release notes. Missing on pre-v0.5.4 settings.json ⇒ default
    // true, so upgrading users get seamless updates from the next version onward.
    public bool AutoUpdateEnabled { get; init; } = true;

    // v0.5.7+ — user-controlled corner rounding.
    // ButtonCornerRadius: applies to toolbar buttons. Slider caps at 15 (v0.6.1);
    //   0 = square, 15 = very rounded but not fully pill.
    // PlashkaCornerRadius: applies to row Borders. Slider caps at 20 (v0.6.1);
    //   0 = square, 20 = very rounded (a fully-pill 44-tall row would need 22, but
    //   the aesthetic call is that near-pill is the intended max).
    // Legacy users may have values above the new caps saved from v0.5.7-v0.6.0 — those
    // render at the saved value; the slider clamps on next touch.
    public int ButtonCornerRadius { get; init; } = 15;
    public int PlashkaCornerRadius { get; init; } = 20;

    // v0.5.7+ — how the status indicator is drawn on each row. Circle (default) is
    // an 8×8 dot inset from the left, matching the capsule plashka look. Stripe is
    // the pre-v0.5.5 4 px vertical bar on the leftmost edge, better paired with a
    // square (PlashkaCornerRadius=0) plashka.
    public StatusIndicatorStyle StatusIndicatorStyle { get; init; } = StatusIndicatorStyle.Circle;
}

/// <summary>How the per-row status indicator renders — see AppSettings.StatusIndicatorStyle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StatusIndicatorStyle
{
    Circle,
    Stripe,
}
