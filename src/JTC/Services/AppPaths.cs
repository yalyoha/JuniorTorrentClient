namespace JTC.Services;

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JTC");

    public static string CacheDir { get; } = Path.Combine(Root, "cache");

    public static void EnsureExists()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
    }
}
