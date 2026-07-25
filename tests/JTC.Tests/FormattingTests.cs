using JTC.Helpers;

namespace JTC.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    public void BytesToHuman_UnderOneKilobyte_UsesBytesUnit(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.BytesToHuman(bytes));
    }

    [Theory]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1024L * 1024, "1.00 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.00 TB")]
    [InlineData(1024L * 1024 * 1024 * 1024 * 1024, "1.00 PB")]
    public void BytesToHuman_KilobyteAndAbove_UsesTwoDecimalsAndScaledUnit(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.BytesToHuman(bytes));
    }

    [Fact]
    public void BytesToHuman_UsesInvariantCulture_DotAsDecimalSeparator()
    {
        // Regression: earlier builds used current-culture formatting and produced
        // "1,50 KB" under ru-RU. All strings must render with '.' regardless of locale.
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal("1.50 KB", Formatting.BytesToHuman(1536));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void BytesToHuman_CapsAtPetabyte_WhenValueExceedsSuffixTable()
    {
        // 2048 PB = 2 EB — no EB in table, so it should stay at PB and grow numerically.
        long twoExabytes = 2048L * 1024 * 1024 * 1024 * 1024 * 1024;
        var result = Formatting.BytesToHuman(twoExabytes);
        Assert.EndsWith(" PB", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1024)]
    public void RateToHuman_ZeroOrNegative_ReturnsDash(long rate)
    {
        Assert.Equal("—", Formatting.RateToHuman(rate));
    }

    [Theory]
    [InlineData(1, "1 B/s")]
    [InlineData(1024, "1.00 KB/s")]
    [InlineData(1024L * 1024, "1.00 MB/s")]
    public void RateToHuman_Positive_AppendsPerSecond(long rate, string expected)
    {
        Assert.Equal(expected, Formatting.RateToHuman(rate));
    }

    [Fact]
    public void EtaToHuman_ZeroOrNegative_ReturnsDash()
    {
        Assert.Equal("—", Formatting.EtaToHuman(TimeSpan.Zero));
        Assert.Equal("—", Formatting.EtaToHuman(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void EtaToHuman_MaxValue_ReturnsInfinity()
    {
        Assert.Equal("∞", Formatting.EtaToHuman(TimeSpan.MaxValue));
    }

    [Fact]
    public void EtaToHuman_MoreThanTenYears_ReturnsInfinity()
    {
        // 3650 days is the cutoff — anything strictly greater collapses to ∞ so a
        // "will never finish" torrent doesn't print a nonsensical "12345d 03:14:07".
        Assert.Equal("∞", Formatting.EtaToHuman(TimeSpan.FromDays(3651)));
    }

    [Fact]
    public void EtaToHuman_ShortDuration_ReturnsHHMMSS()
    {
        var eta = new TimeSpan(hours: 1, minutes: 23, seconds: 45);
        Assert.Equal("01:23:45", Formatting.EtaToHuman(eta));
    }

    [Fact]
    public void EtaToHuman_MultiDay_PrefixesDayCount()
    {
        var eta = new TimeSpan(days: 2, hours: 3, minutes: 4, seconds: 5);
        Assert.Equal("2d 03:04:05", Formatting.EtaToHuman(eta));
    }

    [Fact]
    public void EtaToHuman_UsesInvariantCulture()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            var eta = new TimeSpan(days: 1, hours: 2, minutes: 3, seconds: 4);
            Assert.Equal("1d 02:03:04", Formatting.EtaToHuman(eta));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
