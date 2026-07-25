using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace JTC.Services;

/// <summary>
/// Auto-update client for the GitHub Releases page. Queries the "latest" release,
/// parses tag_name → Version, and offers download+install if it is newer than the
/// currently-running assembly. All network calls swallow their own exceptions and
/// return null — no user-visible error path is worth a red UI when the check itself
/// is best-effort.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/yalyoha/JuniorTorrentClient/releases/latest";

    // Reused HttpClient — one per process. GitHub requires a User-Agent for any
    // API access, otherwise it returns 403.
    private static readonly HttpClient _http;

    static UpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "JTC-updater");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// Fetches the latest release and returns an UpdateInfo pointing to its setup.exe
    /// asset. Returns null if the fetch failed, if the tag can't be parsed as a
    /// version, or if the release has no *-setup.exe asset attached.
    /// </summary>
    public static async Task<UpdateInfo?> CheckLatestVersionAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(LatestReleaseUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tag)) return null;
            var versionStr = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionStr, out var version)) return null;

            var htmlUrl  = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var rawBody  = root.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "";
            var notes    = MarkdownToPlain(rawBody);

            if (!root.TryGetProperty("assets", out var assets)) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                var url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (string.IsNullOrEmpty(url)) continue;
                return new UpdateInfo
                {
                    Version         = version,
                    TagName         = tag,
                    SetupAssetName  = name,
                    SetupAssetUrl   = url,
                    SetupAssetSize  = size,
                    HtmlUrl         = htmlUrl,
                    ReleaseNotes    = notes,
                };
            }
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Error("UpdateService.CheckLatestVersionAsync", ex);
            return null;
        }
    }

    /// <summary>
    /// Version of the currently-running assembly (from <c>&lt;Version&gt;</c> in the
    /// csproj). Returns null in the extremely unlikely case that the assembly has no
    /// version — treat null as "no update needed" upstream so a broken read doesn't
    /// nag the user with a bogus prompt.
    /// </summary>
    public static Version? CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>
    /// True iff <paramref name="latest"/> is a newer Major.Minor.Build than
    /// <paramref name="current"/>. Revision is ignored — our .csproj uses 3-part
    /// versions, so any Revision from Assembly metadata is a build-side detail.
    /// </summary>
    public static bool IsNewer(Version latest, Version current)
    {
        var lM = latest.Major;
        var cM = current.Major;
        if (lM != cM) return lM > cM;
        var lm = latest.Minor;
        var cm = current.Minor;
        if (lm != cm) return lm > cm;
        var lb = Math.Max(0, latest.Build);
        var cb = Math.Max(0, current.Build);
        return lb > cb;
    }

    /// <summary>
    /// Downloads the setup .exe from <paramref name="info"/> to
    /// <c>%TEMP%\&lt;SetupAssetName&gt;</c> and returns the full path on success,
    /// or null on any error. Reports progress in the 0..1 range if
    /// <paramref name="progress"/> is provided.
    /// </summary>
    public static async Task<string?> DownloadInstallerAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        try
        {
            var target = Path.Combine(Path.GetTempPath(), info.SetupAssetName);
            using var resp = await _http.GetAsync(info.SetupAssetUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.SetupAssetSize;

            await using var body = await resp.Content.ReadAsStreamAsync();
            await using var file = File.Create(target);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                copied += read;
                if (progress is not null && total > 0)
                    progress.Report((double)copied / total);
            }
            return target;
        }
        catch (Exception ex)
        {
            DebugLog.Error("UpdateService.DownloadInstallerAsync", ex);
            return null;
        }
    }

    /// <summary>
    /// Launches the freshly-downloaded installer via the shell and terminates the
    /// current process so the installer can immediately overwrite our files. Torrent
    /// state is persisted after every user action, so a hard exit here loses at most
    /// current-second rate stats — same trade-off as the tray "Выход" path.
    /// </summary>
    /// <summary>
    /// Trim the most disruptive Markdown noise from a release-notes body so it renders
    /// cleanly in a plain WinUI TextBlock — WinUI 3 has no built-in Markdown control,
    /// and pulling in a full renderer for a small update dialog is overkill. Strips
    /// leading heading hashes, bold asterisks, code backticks, and normalises line
    /// separators. The result is still recognisably the release notes, just as plain
    /// text.
    /// </summary>
    private static string MarkdownToPlain(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder(md.Length);
        foreach (var raw in lines)
        {
            var line = raw;
            // Heading markers: '## Foo' → 'Foo'. Keep the text.
            while (line.StartsWith("#"))
                line = line.Substring(1);
            line = line.TrimStart();
            // Bold: **foo** → foo. Applied blindly — non-matched pairs will just
            // strip stray asterisks, no worse than raw markdown.
            line = line.Replace("**", "");
            // Inline code backticks — same treatment.
            line = line.Replace("`", "");
            // Unordered list markers "- foo" → "• foo" reads cleaner in a dialog.
            if (line.StartsWith("- ")) line = "• " + line.Substring(2);
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DebugLog.Error("UpdateService.LaunchInstallerAndExit", ex);
            return;
        }
        // Give the installer a beat to spawn before we die — otherwise the shell's
        // "elevate?" prompt might not manage to inherit our foreground state.
        System.Threading.Thread.Sleep(300);
        Environment.Exit(0);
    }

    /// <summary>
    /// Silent-mode counterpart to LaunchInstallerAndExit — starts the installer
    /// with <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART</c> so no wizard UI shows,
    /// then exits the current process immediately. The installer's [Run] entry
    /// (without <c>skipifsilent</c>) launches JTC.exe again once the file copy
    /// completes, so the user sees the app come back on its own.
    /// </summary>
    public static void LaunchInstallerSilentAndExit(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DebugLog.Error("UpdateService.LaunchInstallerSilentAndExit", ex);
            return;
        }
        System.Threading.Thread.Sleep(300);
        Environment.Exit(0);
    }
}

public sealed record UpdateInfo
{
    public required Version Version        { get; init; }
    public required string  TagName        { get; init; }
    public required string  SetupAssetName { get; init; }
    public required string  SetupAssetUrl  { get; init; }
    public required long    SetupAssetSize { get; init; }
    public required string  HtmlUrl        { get; init; }
    public required string  ReleaseNotes   { get; init; }
}
