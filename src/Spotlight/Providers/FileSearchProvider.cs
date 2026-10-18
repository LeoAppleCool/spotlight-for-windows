using Spotlight.Core;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>Files and folders from the in-memory <see cref="FileIndex"/>.</summary>
internal sealed class FileSearchProvider(FileIndex index) : ISearchProvider
{
    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        if (query.Norm.Length < 2 || PathProvider.LooksLikePath(query.Raw)) return;
        foreach (var hit in index.Search(query, 40, ct))
            results.Add(FileResult.Create(hit.Path, hit.IsDir, hit.Score));
    }
}

internal static class FileResult
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static SearchResult Create(string path, bool isDir, double score, string? completion = null)
    {
        string name = Path.GetFileName(path.TrimEnd('\\'));
        if (name.Length == 0) name = path;
        return new SearchResult
        {
            Key = "file:" + path.ToLowerInvariant(),
            Title = name,
            Subtitle = PrettyFolder(Path.GetDirectoryName(path.TrimEnd('\\')) ?? path),
            Category = FileKinds.CategoryOf(path, isDir),
            Score = score,
            IconSource = path,
            IconCacheKey = FileKinds.IconKey(path, isDir),
            Execute = () => Launcher.Open(path),
            Launch = new LaunchSpec("open", path),
            Reveal = () => Launcher.Reveal(path),
            RunAsAdmin = !isDir && FileKinds.IsExecutable(path) ? () => Launcher.Open(path, verb: "runas") : null,
            CopyValue = path,
            Completion = completion,
        };
    }

    /// <summary>"C:\Users\Anna\Desktop\Projects" → "Desktop › Projects".</summary>
    public static string PrettyFolder(string folder)
    {
        if (folder.StartsWith(Profile + "\\", StringComparison.OrdinalIgnoreCase))
            folder = folder[(Profile.Length + 1)..];
        else if (string.Equals(folder, Profile, StringComparison.OrdinalIgnoreCase))
            return folder;
        return folder.Replace("\\", " › ").TrimEnd(' ', '›');
    }
}
