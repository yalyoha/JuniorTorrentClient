using System.Globalization;

namespace JTC.Helpers;

public static class Formatting
{
    private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string BytesToHuman(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double value = bytes;
        int suffix = 0;
        while (value >= 1024 && suffix < SizeSuffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return string.Format(Culture, "{0:0.00} {1}", value, SizeSuffixes[suffix]);
    }

    public static string RateToHuman(long bytesPerSec)
    {
        if (bytesPerSec <= 0)
            return "—";
        return BytesToHuman(bytesPerSec) + "/s";
    }

    public static string EtaToHuman(TimeSpan eta)
    {
        if (eta <= TimeSpan.Zero)
            return "—";
        if (eta == TimeSpan.MaxValue || eta.TotalDays > 3650)
            return "∞";

        if (eta.Days > 0)
            return string.Format(Culture, "{0}d {1:hh\\:mm\\:ss}", eta.Days, eta);

        return eta.ToString(@"hh\:mm\:ss", Culture);
    }
}
