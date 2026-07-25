namespace JTC.Services;

/// <summary>
/// Per-torrent restart budget and backoff schedule used by TorrentService's watchdog when
/// a manager transitions to <c>TorrentState.Error</c>. Extracted from TorrentService so
/// the retry policy itself has no MonoTorrent dependency and can be unit-tested in
/// isolation — the actual restart mechanics (StartAsync / HashCheckAsync) live in
/// TorrentService and consult this class for "should I retry and if so, when."
/// </summary>
public sealed class TorrentRestartPolicy
{
    /// <summary>Absolute upper bound on retries before we give up on a torrent.</summary>
    public const int MaxAttempts = 5;

    // Backoff before each attempt. Escalating so we don't hammer a transiently-broken
    // resource (e.g. path briefly locked by an antivirus scan) and settling at 60 s so
    // we don't wait too long once a real transient dies down. Indices 3–4 are the cap.
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
    };

    private int _attemptsUsed;
    private bool _fatal;

    /// <summary>How many restart attempts this policy has already consumed.</summary>
    public int AttemptsUsed => _attemptsUsed;

    /// <summary>Whether further restart attempts are still allowed.</summary>
    public bool IsExhausted => _fatal || _attemptsUsed >= MaxAttempts;

    /// <summary>
    /// Reserve the next restart attempt. If retries remain, returns <c>true</c> and
    /// sets <paramref name="delay"/> to the backoff to wait before attempting.
    /// The caller is responsible for actually running the restart after the delay
    /// — this method just increments the counter.
    /// </summary>
    public bool TryReserveNextAttempt(out TimeSpan delay)
    {
        if (IsExhausted)
        {
            delay = TimeSpan.Zero;
            return false;
        }
        delay = BackoffSchedule[_attemptsUsed];
        _attemptsUsed++;
        return true;
    }

    /// <summary>
    /// Torrent resumed downloading — reset both the retry counter and the fatal flag
    /// so the next stall starts with a fresh budget.
    /// </summary>
    public void RecordSuccess()
    {
        _attemptsUsed = 0;
        _fatal = false;
    }

    /// <summary>
    /// Mark the current failure as unrecoverable (e.g. disk full, permission denied)
    /// and stop retrying even if the attempt budget hasn't been spent yet.
    /// </summary>
    public void MarkFatal() => _fatal = true;

    /// <summary>
    /// Classifies an exception as "no point retrying" — the same restart would fail
    /// the same way. Only patterns we can identify with high confidence: retrying
    /// is much cheaper than a false negative (we'd wait 5 s and try once), so the
    /// bar for calling something fatal is deliberately high.
    /// </summary>
    public static bool IsFatalException(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is UnauthorizedAccessException) return true;
        if (ex is DirectoryNotFoundException) return true;
        if (ex is IOException io)
        {
            // Windows ERROR_DISK_FULL = 0x70; HResult is 0x80070000 | 0x70 for IOException.
            const int ERROR_DISK_FULL_HRESULT = unchecked((int)0x80070070);
            if (io.HResult == ERROR_DISK_FULL_HRESULT) return true;
            var msg = io.Message ?? string.Empty;
            if (msg.Contains("not enough space", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("disk is full", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
