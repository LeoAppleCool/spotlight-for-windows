using System.Text.RegularExpressions;
using Spotlight.Core;

namespace Spotlight.Providers;

/// <summary>Typing a path ("C:\Us", "~\Docu", "%appdata%\") lists matching entries; Tab completes.</summary>
internal sealed partial class PathProvider : ISearchProvider
{
    private const int MaxResults = 30;

    public static bool LooksLikePath(string text)
    {
        text = text.TrimStart();
        return DriveRegex().IsMatch(text) || text.StartsWith(@"\\", StringComparison.Ordinal)
            || text.StartsWith('~') || (text.StartsWith('%') && text.IndexOf('%', 1) > 0);
    }

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        string raw = query.Raw.Trim().Trim('"');
        if (!LooksLikePath(raw)) return;

        string path = Expand(raw);
        try
        {
            bool endsWithSeparator = path.EndsWith('\\') || path.EndsWith('/');
            if (DriveOnlyRegex().IsMatch(path)) { path += "\\"; endsWithSeparator = true; }

            if (!endsWithSeparator && (File.Exists(path) || Directory.Exists(path)))
            {
                bool isDir = Directory.Exists(path);
                results.Add(FileResult.Create(path, isDir, 1600, isDir ? path.TrimEnd('\\') + "\\" : path));
            }

            string? folder = endsWithSeparator ? path : Path.GetDirectoryName(path);
            string prefix = endsWithSeparator ? "" : Path.GetFileName(path);
            if (folder == null || !Directory.Exists(folder)) return;

            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            int count = 0, scanned = 0;
            var matches = new List<(string Path, bool IsDir)>();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (++scanned > 5000 || ct.IsCancellationRequested) break;
                if (prefix.Length > 0 && !entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (prefix.Length > 0 && entry.Name.Length == prefix.Length) continue; // already shown above
                matches.Add((entry.FullName, entry is DirectoryInfo));
            }

            foreach (var (p, isDir) in matches.OrderByDescending(m => m.IsDir).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > MaxResults) break;
                results.Add(FileResult.Create(p, isDir, 1500 - count, isDir ? p + "\\" : p));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static string Expand(string raw)
    {
        if (raw.StartsWith('~'))
            raw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + raw[1..];
        return Environment.ExpandEnvironmentVariables(raw).Replace('/', '\\');
    }

    [GeneratedRegex(@"^[a-zA-Z]:([\\/]|$)")]
    private static partial Regex DriveRegex();

    [GeneratedRegex(@"^[a-zA-Z]:$")]
    private static partial Regex DriveOnlyRegex();
}
