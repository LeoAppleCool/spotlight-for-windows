namespace Spotlight;

/// <summary>Tiny append-only diagnostics log in %LOCALAPPDATA%\Spotlight\spotlight.log (rotated at 1 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(AppPaths.DataDir, "spotlight.log");
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}

internal static class AppPaths
{
    public static string DataDir { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight"));

    public static string CacheDir { get; } = Ensure(Path.Combine(DataDir, "cache"));

    private static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
