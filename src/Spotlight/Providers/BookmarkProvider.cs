using Spotlight.Core;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>Bookmarks of all installed browsers; reloaded when a browser's bookmark file changes.</summary>
internal sealed class BookmarkProvider : ISearchProvider
{
    private sealed record Entry(Bookmark Bookmark, string Host, MatchTarget Title, MatchTarget HostTarget);

    private volatile IReadOnlyList<Entry> _entries = [];
    private Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCheck = DateTime.MinValue;
    private int _loading;

    public int Count => _entries.Count;

    /// <summary>Cheap check (file timestamps), at most every 10 minutes; reloads in the background if needed.</summary>
    public void RefreshIfChanged()
    {
        // Firefox-based browsers touch their database on every page visit, so don't look too often.
        if (DateTime.UtcNow - _lastCheck < TimeSpan.FromMinutes(10) || Interlocked.Exchange(ref _loading, 1) == 1) return;
        _lastCheck = DateTime.UtcNow;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var stamps = BookmarkReader.GetSourceFiles()
                    .ToDictionary(f => f, f => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue, StringComparer.OrdinalIgnoreCase);
                if (_entries.Count > 0 && stamps.Count == _stamps.Count && stamps.All(s => _stamps.TryGetValue(s.Key, out var t) && t == s.Value))
                    return;
                Load();
                _stamps = stamps;
            }
            catch (Exception ex) { Log.Error("Reading bookmarks failed", ex); }
            finally { _loading = 0; }
        });
    }

    private void Load()
    {
        string? defaultBrowser = DefaultBrowser.ExePath;
        var byUrl = new Dictionary<string, Bookmark>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in BookmarkReader.LoadAll())
        {
            // Same page bookmarked in several browsers: keep one, preferring the default browser.
            string key = b.Url.TrimEnd('/');
            if (!byUrl.TryGetValue(key, out var existing)
                || (!IsDefault(existing, defaultBrowser) && IsDefault(b, defaultBrowser)))
                byUrl[key] = b;
        }

        _entries = byUrl.Values.Select(b =>
        {
            string host = Uri.TryCreate(b.Url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : "";
            return new Entry(b, host, new MatchTarget(b.Title), new MatchTarget(host));
        }).ToList();
        Log.Info($"Bookmarks loaded: {_entries.Count}");
    }

    private static bool IsDefault(Bookmark b, string? defaultExe) =>
        defaultExe != null && string.Equals(b.BrowserExePath, defaultExe, StringComparison.OrdinalIgnoreCase);

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        if (query.Norm.Length < 2) return;
        var matches = new List<(Entry Entry, int Score)>();
        foreach (var e in _entries)
        {
            int score = Math.Max(Matcher.Score(query, e.Title, allowFuzzy: false), Matcher.Score(query, e.HostTarget, allowFuzzy: false) * 9 / 10);
            if (score >= 520) matches.Add((e, score));
        }

        foreach (var (e, score) in matches.OrderByDescending(m => m.Score).Take(8))
        {
            var b = e.Bookmark;
            string url = b.Url;
            results.Add(new SearchResult
            {
                Key = "bookmark:" + url.ToLowerInvariant(),
                Title = string.IsNullOrWhiteSpace(b.Title) ? e.Host : b.Title,
                Subtitle = $"{e.Host} · {Loc.Format("Sub.Bookmark", b.Browser)}",
                Category = Category.Bookmarks,
                Score = score,
                Glyph = Glyphs.Globe,
                GlyphFont = Glyphs.Font,
                IconSource = b.BrowserExePath,
                IconCacheKey = b.BrowserExePath != null ? "path:" + b.BrowserExePath.ToLowerInvariant() : null,
                Execute = () => Launcher.Open(url),
                Launch = new LaunchSpec("open", url),
                CopyValue = url,
            });
        }
    }
}
