using System;
using Windows.UI;
using JTC.Services;

namespace JTC.Helpers;

// Row-background palette + status stripe colours.
//
// Three moving parts per row:
//   1. Plashka surface (Bg / BgSelected / Fg) — from Palette. Coloured theme takes it
//      from AppSettings.PlashkaBgHex / PlashkaFgHex (user-picked in v0.5+); Dark and
//      Light themes still use their baked palettes.
//   2. 4 px left-edge status stripe — one of the five StatusXxx colours below, full
//      opacity so it reads on any plashka.
//   3. Progress-bar "fill" (the left portion of the row that grows with Progress %) —
//      NOT stored in the Palette any more. It's computed per-row as a 17% composite of
//      the row's current status colour over the plashka Bg, so download rows tint
//      orange, seeding tint green, etc.
public static class RowBrushes
{
    public sealed record Palette(Color Bg, Color BgSelected, Color Fg);

    private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    // Status stripe hues. Public setters via SetStatusColors so the settings dialog
    // can override them from user-picked values; defaults are the Material A400 palette
    // that shipped through v0.4.10.
    public static Color StatusIdle        { get; private set; } = C(0xFF, 0x90, 0xA4, 0xAE);
    public static Color StatusDownloading { get; private set; } = C(0xFF, 0xFF, 0x91, 0x00);
    public static Color StatusSeeding     { get; private set; } = C(0xFF, 0x00, 0xE6, 0x76);
    public static Color StatusHashing     { get; private set; } = C(0xFF, 0x29, 0x79, 0xFF);
    public static Color StatusError       { get; private set; } = C(0xFF, 0xFF, 0x17, 0x44);

    public static void SetStatusColors(Color idle, Color downloading, Color seeding, Color hashing, Color error)
    {
        StatusIdle        = idle;
        StatusDownloading = downloading;
        StatusSeeding     = seeding;
        StatusHashing     = hashing;
        StatusError       = error;
    }

    // Light theme baseline: white plashka with a slightly-darker "selected" step.
    private static readonly Palette LightPalette = new(
        Bg:         C(0xFF, 0xFF, 0xFF, 0xFF),
        BgSelected: C(0xFF, 0xEC, 0xEC, 0xEC),
        Fg:         C(0xFF, 0x21, 0x21, 0x21));

    // Dark theme baseline: faint white lift over the #212121 window.
    private static readonly Palette DarkPalette = new(
        Bg:         C(0xFF, 0x2A, 0x2A, 0x2A),
        BgSelected: C(0xFF, 0x36, 0x36, 0x36),
        Fg:         C(0xFF, 0xFF, 0xFF, 0xFF));

    public static Palette Current { get; private set; } = LightPalette;

    /// <summary>
    /// Non-Coloured theme setter — uses the baked-in Dark / Light palette. Coloured
    /// theme should call the (theme, plashkaBg, plashkaFg) overload below with the
    /// user's picked colours instead.
    /// </summary>
    public static void Set(AppTheme theme)
    {
        Current = theme switch
        {
            AppTheme.Dark  => DarkPalette,
            AppTheme.Light => LightPalette,
            _              => LightPalette, // Coloured fallback — real Set below overrides
        };
    }

    /// <summary>
    /// Coloured-theme setter — builds a Palette from user-picked plashka bg + fg. The
    /// "selected" background is derived by nudging the base 6% toward black so a picked
    /// pure-white plashka still has a visible selected state.
    /// </summary>
    public static void SetColored(Color plashkaBg, Color plashkaFg)
    {
        var bgSelected = MixColors(plashkaBg, C(0xFF, 0, 0, 0), 0.06);
        Current = new Palette(plashkaBg, bgSelected, plashkaFg);
    }

    /// <summary>
    /// Alpha-composite <paramref name="over"/> at opacity <paramref name="alpha"/>
    /// (0..1) on top of the opaque <paramref name="under"/>, returning an opaque
    /// result colour. Used by TorrentViewModel to tint the row-fill portion with
    /// 17 % of the row's current status colour.
    /// </summary>
    public static Color CompositeOver(Color under, Color over, double alpha)
    {
        alpha = Math.Clamp(alpha, 0.0, 1.0);
        byte lerp(byte u, byte o) => (byte)Math.Round(u * (1 - alpha) + o * alpha);
        return C(0xFF, lerp(under.R, over.R), lerp(under.G, over.G), lerp(under.B, over.B));
    }

    private static Color MixColors(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte lerp(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return C(0xFF, lerp(a.R, b.R), lerp(a.G, b.G), lerp(a.B, b.B));
    }
}
