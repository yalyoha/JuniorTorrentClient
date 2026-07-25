namespace JTC.Services;

public enum PersistedSourceKind
{
    TorrentFile,
    Magnet,
}

public sealed record PersistedTorrent
{
    public string Source { get; init; } = "";
    public PersistedSourceKind SourceKind { get; init; }
    public string DownloadDir { get; init; } = "";
    public bool Paused { get; init; }

    // File indices that were marked DoNotDownload at add time. Nullable so records
    // written by pre-task-7 builds (no "SkipFileIndices" field in torrents.json) still
    // deserialize into a PersistedTorrent with SkipFileIndices == null → "download
    // everything", matching legacy behaviour. Non-null empty array means "explicitly
    // nothing skipped" (also equivalent, but distinguishable in logs).
    public int[]? SkipFileIndices { get; init; }
}
