using JTC.Services;

namespace JTC.Tests;

public class TorrentRestartPolicyTests
{
    [Fact]
    public void Fresh_HasZeroAttemptsAndIsNotExhausted()
    {
        var policy = new TorrentRestartPolicy();
        Assert.Equal(0, policy.AttemptsUsed);
        Assert.False(policy.IsExhausted);
    }

    [Fact]
    public void TryReserveNextAttempt_ReturnsEscalatingBackoff_ThenCapsAtSixtySeconds()
    {
        var policy = new TorrentRestartPolicy();

        Assert.True(policy.TryReserveNextAttempt(out var d1));
        Assert.Equal(TimeSpan.FromSeconds(5), d1);

        Assert.True(policy.TryReserveNextAttempt(out var d2));
        Assert.Equal(TimeSpan.FromSeconds(15), d2);

        Assert.True(policy.TryReserveNextAttempt(out var d3));
        Assert.Equal(TimeSpan.FromSeconds(60), d3);

        Assert.True(policy.TryReserveNextAttempt(out var d4));
        Assert.Equal(TimeSpan.FromSeconds(60), d4);

        Assert.True(policy.TryReserveNextAttempt(out var d5));
        Assert.Equal(TimeSpan.FromSeconds(60), d5);
    }

    [Fact]
    public void TryReserveNextAttempt_ConsumesExactlyOneAttemptPerCall()
    {
        var policy = new TorrentRestartPolicy();
        policy.TryReserveNextAttempt(out _);
        Assert.Equal(1, policy.AttemptsUsed);
        policy.TryReserveNextAttempt(out _);
        Assert.Equal(2, policy.AttemptsUsed);
    }

    [Fact]
    public void TryReserveNextAttempt_AfterMaxAttempts_ReturnsFalseAndDoesNotIncrement()
    {
        var policy = new TorrentRestartPolicy();
        for (int i = 0; i < TorrentRestartPolicy.MaxAttempts; i++)
            Assert.True(policy.TryReserveNextAttempt(out _));

        Assert.True(policy.IsExhausted);

        Assert.False(policy.TryReserveNextAttempt(out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(TorrentRestartPolicy.MaxAttempts, policy.AttemptsUsed);
    }

    [Fact]
    public void MarkFatal_ExhaustsPolicyEvenWithAttemptsRemaining()
    {
        var policy = new TorrentRestartPolicy();
        policy.TryReserveNextAttempt(out _);
        policy.MarkFatal();

        Assert.True(policy.IsExhausted);
        Assert.False(policy.TryReserveNextAttempt(out _));
    }

    [Fact]
    public void RecordSuccess_ClearsAttemptsAndFatalFlag()
    {
        var policy = new TorrentRestartPolicy();
        policy.TryReserveNextAttempt(out _);
        policy.TryReserveNextAttempt(out _);
        policy.MarkFatal();
        Assert.True(policy.IsExhausted);

        policy.RecordSuccess();

        Assert.Equal(0, policy.AttemptsUsed);
        Assert.False(policy.IsExhausted);
        Assert.True(policy.TryReserveNextAttempt(out var delay));
        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public void IsFatalException_UnauthorizedAccess_IsFatal()
    {
        Assert.True(TorrentRestartPolicy.IsFatalException(new UnauthorizedAccessException()));
    }

    [Fact]
    public void IsFatalException_DirectoryNotFound_IsFatal()
    {
        Assert.True(TorrentRestartPolicy.IsFatalException(new DirectoryNotFoundException()));
    }

    [Fact]
    public void IsFatalException_DiskFullByHResult_IsFatal()
    {
        var ex = new IOException("something failed")
        {
            HResult = unchecked((int)0x80070070),
        };
        Assert.True(TorrentRestartPolicy.IsFatalException(ex));
    }

    [Theory]
    [InlineData("There is not enough space on the disk.")]
    [InlineData("not enough space")]
    [InlineData("The disk is full.")]
    public void IsFatalException_DiskFullByMessage_IsFatal(string message)
    {
        Assert.True(TorrentRestartPolicy.IsFatalException(new IOException(message)));
    }

    [Fact]
    public void IsFatalException_GenericIOException_IsNotFatal()
    {
        // A run-of-the-mill IO error (e.g. transient sharing violation) must be
        // retryable — otherwise a single blip permanently disables the torrent.
        Assert.False(TorrentRestartPolicy.IsFatalException(new IOException("sharing violation")));
    }

    [Fact]
    public void IsFatalException_Null_IsNotFatal()
    {
        Assert.False(TorrentRestartPolicy.IsFatalException(null));
    }

    [Fact]
    public void IsFatalException_UnrelatedException_IsNotFatal()
    {
        Assert.False(TorrentRestartPolicy.IsFatalException(new InvalidOperationException("something")));
    }

    [Fact]
    public void MaxAttempts_MatchesBackoffScheduleLength()
    {
        // Guard against the schedule and the cap drifting apart — the policy assumes
        // BackoffSchedule[_attemptsUsed] is always in range while IsExhausted is false.
        var policy = new TorrentRestartPolicy();
        int reserved = 0;
        while (policy.TryReserveNextAttempt(out _)) reserved++;
        Assert.Equal(TorrentRestartPolicy.MaxAttempts, reserved);
    }
}
