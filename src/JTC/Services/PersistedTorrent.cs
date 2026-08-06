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
    //
    // LEGACY read-only path: current builds persist SkipFilePaths (below) instead —
    // index-based lookup was fragile because MonoTorrent's manager.Files list can
    // reorder or reindex files relative to the parsed .torrent (BEP-47 padding
    // handling, sha256 v2 quirks). Records written by older builds are migrated on
    // load by re-parsing the .torrent and translating indices → paths.
    public int[]? SkipFileIndices { get; init; }

    // File paths (relative to torrent root, exactly matching ITorrentFile.Path)
    // that were marked DoNotDownload at add time. Preferred over SkipFileIndices;
    // stable across MonoTorrent version changes and unaffected by any internal
    // file-list reordering.
    public string[]? SkipFilePaths { get; init; }
}
