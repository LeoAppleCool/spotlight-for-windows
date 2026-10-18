using Spotlight.Providers;

namespace Spotlight.Core;

internal sealed class SearchOutcome
{
    public required Query Query { get; init; }
    public required List<ListEntry> Entries { get; init; }
    public required bool IsFinal { get; init; }
}

/// <summary>
/// Runs all providers off the UI thread for every keystroke, ranks the results (match quality, category,
/// what the user picked before) and groups them like Spotlight: Top Hit first, then one section per category.
/// </summary>
internal sealed class SearchEngine(IReadOnlyList<ISearchProvider> providers, IReadOnlyList<IAsyncSearchProvider> asyncProviders, UsageStore usage)
{
    private const double TopHitThreshold = 640;
    private CancellationTokenSource? _cts;
    private int _generation;

    /// <summary>Raised on a background thread; first with the fast results, again when slow providers finish.</summary>
    public event Action<SearchOutcome>? Completed;

    /// <summary>Recently opened items, listed while the search box is empty.</summary>
    public HistoryStore? History { get; set; }

    public void Search(string text)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        int generation = Interlocked.Increment(ref _generation);
        var query = new Query(text);

        if (text.Trim().Length == 0)
        {
            var recent = History?.CreateResults() ?? [];
            List<ListEntry> entries = recent.Count == 0 ? [] : [new HeaderEntry(Loc.Get("Cat.Recent")), .. recent];
            Completed?.Invoke(new SearchOutcome { Query = query, Entries = entries, IsFinal = true });
            return;
        }

        Task.Run(async () =>
        {
            // A typed path only lists that folder (plus the web fallback).
            bool pathMode = PathProvider.LooksLikePath(query.Raw);
            var results = new List<SearchResult>(128);
            foreach (var provider in providers)
            {
                if (ct.IsCancellationRequested) return;
                if (pathMode && provider is not (PathProvider or WebProvider)) continue;
                try { provider.Search(query, results, ct); }
                catch (Exception ex) { Log.Error($"{provider.GetType().Name} failed", ex); }
            }
            if (ct.IsCancellationRequested) return;

            // Full-text matches are a fallback: when file names already match (or it's a calculation), they add noise.
            int nameMatches = results.Count(r => r.Category is Category.Folders or Category.Documents
                or Category.Images or Category.Media or Category.Files);
            bool calculation = results.Any(r => r.IsCalculation);
            var slow = nameMatches >= 4 || calculation ? [] : asyncProviders.Where(p => p.Accepts(query)).ToList();
            Publish(query, results, slow.Count == 0, generation);
            if (slow.Count == 0) return;

            try { await Task.Delay(150, ct); }
            catch (OperationCanceledException) { return; }

            foreach (var provider in slow)
            {
                try { results.AddRange(await provider.SearchAsync(query, ct)); }
                catch (Exception ex) { Log.Error($"{provider.GetType().Name} failed", ex); }
                if (ct.IsCancellationRequested) return;
            }
            Publish(query, results, true, generation);
        }, ct);
    }

    private void Publish(Query query, List<SearchResult> results, bool final, int generation)
    {
        if (generation != Volatile.Read(ref _generation)) return;
        var entries = Layout(query, results);
        if (generation != Volatile.Read(ref _generation)) return;
        Completed?.Invoke(new SearchOutcome { Query = query, Entries = entries, IsFinal = final });
    }

    private List<ListEntry> Layout(Query query, List<SearchResult> results)
    {
        var byKey = new Dictionary<string, SearchResult>(results.Count);
        foreach (var r in results)
            if (!byKey.TryGetValue(r.Key, out var existing) || existing.Score < r.Score)
                byKey[r.Key] = r;

        var ranked = byKey.Values
            .Select(r => (Result: r, Score: r.Rank = r.Score + CategoryWeight(r.Category) + Learned(query, r)))
            .OrderByDescending(x => x.Score)
            .ToList();

        bool pathMode = PathProvider.LooksLikePath(query.Raw);
        bool hasCalculation = ranked.Any(x => x.Result.Category is Category.Calculator or Category.Conversion);
        var top = ranked.FirstOrDefault(x => x.Result.Category != Category.Web);

        var entries = new List<ListEntry>(64);
        SearchResult? topHit = null;
        if (!hasCalculation && top.Result != null && top.Score >= TopHitThreshold)
        {
            topHit = top.Result;
            entries.Add(new HeaderEntry(Loc.Get("Cat.TopHit")));
            entries.Add(topHit);
        }

        foreach (var category in Enum.GetValues<Category>())
        {
            // "2+2" or "10 km": the answer is all that matters; "2" matching file names would only be noise.
            if (hasCalculation && category is not (Category.Calculator or Category.Conversion or Category.Web)) continue;
            int cap = Cap(category, pathMode);
            var items = ranked.Where(x => x.Result.Category == category && x.Result != topHit).Take(cap).ToList();
            if (items.Count == 0) continue;
            entries.Add(new HeaderEntry(Loc.Get("Cat." + category)));
            entries.AddRange(items.Select(x => x.Result));
        }
        return entries;
    }

    private double Learned(Query query, SearchResult r) =>
        r.Category is Category.Calculator or Category.Conversion or Category.Web ? 0 : usage.Boost(query, r.Key);

    // Like Spotlight, apps and settings win over equally named files buried somewhere on disk.
    private static double CategoryWeight(Category c) => c switch
    {
        Category.Applications => 150,
        Category.Settings => 60,
        Category.System => 60,
        Category.Folders => -40,
        Category.Documents => -40,
        Category.Images or Category.Media => -60,
        Category.Files => -80,
        Category.Web => -100_000,
        _ => 0,
    };

    private static int Cap(Category c, bool pathMode) => c switch
    {
        Category.Calculator => 1,
        Category.Conversion => 3,
        Category.Applications => 6,
        Category.Settings => 5,
        Category.System => 3,
        Category.Websites => 1,
        Category.Bookmarks => 4,
        Category.Folders => pathMode ? 30 : 4,
        Category.Documents => pathMode ? 30 : 5,
        Category.Images or Category.Media or Category.Files => pathMode ? 30 : 4,
        Category.Web => 1,
        _ => 4,
    };
}
