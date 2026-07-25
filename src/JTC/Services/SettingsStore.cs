using System.Text.Json;
using System.Text.Json.Nodes;

namespace JTC.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Global lock. SettingsStore is static (unlike StateStore) so we serialise across all
    // callers in the process. Save is rare (only user-visible settings changes) — a lock
    // is fine and matches StateStore's "one writer at a time" invariant.
    private static readonly object _writeLock = new();

    private static string FilePath => Path.Combine(AppPaths.Root, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            var text = File.ReadAllText(FilePath);
            text = MigrateLegacyBrandTheme(text);
            return JsonSerializer.Deserialize<AppSettings>(text, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Rewrites legacy <c>Theme: Brand</c> / <c>Theme: Brand2</c> JSON values (from builds
    /// before the Colored theme collapsed the two branded themes into one) into
    /// <c>Theme: Colored</c> with the matching preset colors preserved. Preserves the
    /// user's brand choice across the upgrade — a Brand2 user keeps blue → lime, not
    /// the default pink → orange.
    /// </summary>
    private static string MigrateLegacyBrandTheme(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return json;
            var themeVal = obj["Theme"]?.GetValue<string>();
            if (themeVal is null) return json;
            // Newer builds already write ColoredTopHex — nothing to migrate.
            if (obj.ContainsKey("ColoredTopHex")) return json;

            string? topHex, bottomHex;
            if (string.Equals(themeVal, "Brand", System.StringComparison.OrdinalIgnoreCase))
            {
                topHex    = BuiltInColorPresets.PinkTopHex;
                bottomHex = BuiltInColorPresets.PinkBottomHex;
            }
            else if (string.Equals(themeVal, "Brand2", System.StringComparison.OrdinalIgnoreCase))
            {
                topHex    = BuiltInColorPresets.BlueTopHex;
                bottomHex = BuiltInColorPresets.BlueBottomHex;
            }
            else
            {
                return json;
            }

            obj["Theme"]            = "Colored";
            obj["ColoredTopHex"]    = topHex;
            obj["ColoredBottomHex"] = bottomHex;
            return obj.ToJsonString(Options);
        }
        catch
        {
            // Bad JSON — let the outer Deserialize catch handle it and return defaults.
            return json;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Root);
        lock (_writeLock)
        {
            // Unique per-write temp — see StateStore.SaveAsync for the rationale.
            var tmp = Path.Combine(AppPaths.Root, Path.GetRandomFileName() + ".tmp");
            try
            {
                using (var stream = File.Create(tmp))
                {
                    JsonSerializer.Serialize(stream, settings, Options);
                }
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
