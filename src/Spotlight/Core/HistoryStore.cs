using System.Text.Json;
using System.Windows.Media;
using Spotlight.Services;

namespace Spotlight.Core;

/// <summary>How a result is opened again later, independent of the provider that found it.</summary>
/// <param name="Kind">open (file, folder, URL, URI) · shell (shell item such as an app) · cmd (command line) · system (system command id)</param>
public sealed record LaunchSpec(string Kind, string Target, string? Args = null);

/// <summary>The last few things opened from Spotlight, shown when the search box is empty.</summary>
internal sealed class HistoryStore
{
    public const int MaxEntries = 5;

    private sealed class Entry
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public Category Category { get; set; }
        public string? IconSource { get; set; }
        public string? IconCacheKey { get; set; }
        public string? Glyph { get; set; }
        public string? GlyphFont { get; set; }
        public string? GlyphFontBase { get; set; }
        public bool NeedsConfirmation { get; set; }
        public LaunchSpec Launch { get; set; } = new("open", "");
    }

    private readonly string _file = Path.Combine(AppPaths.DataDir, "history.json");
    private readonly object _gate = new();
    private List<Entry> _entries = [];

    public static Action<string>? RunSystemCommand { get; set; }

    public void Load()
    {
        try
        {
            if (File.Exists(_file))
                _entries = (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? []).Take(MaxEntries).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("History unreadable, starting fresh", ex);
            _entries = [];
        }
    }

    public void Add(SearchResult result)
    {
        if (result.Launch == null) return;
        var font = result.GlyphFont;
        var entry = new Entry
        {
            Key = result.Key,
            Title = result.Title,
            Subtitle = result.Subtitle,
            Category = result.Category,
            IconSource = result.IconSource,
            IconCacheKey = result.IconCacheKey,
            Glyph = result.Glyph,
            GlyphFont = font?.Source,
            GlyphFontBase = font?.BaseUri?.ToString(),
            NeedsConfirmation = result.NeedsConfirmation,
            Launch = result.Launch,
        };
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Key == entry.Key);
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        Save();
    }

    public void Remove(string key)
    {
        lock (_gate) _entries.RemoveAll(e => e.Key == key);
        Save();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Save();
    }

    /// <summary>Fresh result rows for the history (a new instance per call, like provider results).</summary>
    public List<SearchResult> CreateResults()
    {
        List<Entry> entries;
        lock (_gate) entries = _entries.ToList();
        return entries.Select(e =>
        {
            var launch = e.Launch;
            bool isPath = launch.Kind == "open" && launch.Target.Length > 2 && (launch.Target[1] == ':' || launch.Target.StartsWith(@"\\"));
            return new SearchResult
            {
                Key = e.Key,
                Title = e.Title,
                Subtitle = e.Subtitle,
                Category = e.Category,
                IconSource = e.IconSource,
                IconCacheKey = e.IconCacheKey,
                Glyph = e.Glyph,
                GlyphFont = Font(e),
                NeedsConfirmation = e.NeedsConfirmation,
                Launch = launch,
                Execute = () => Run(launch),
                Reveal = isPath ? () => Launcher.Reveal(launch.Target) : null,
                CopyValue = launch.Kind == "system" ? null : launch.Target,
                FromHistory = true,
            };
        }).ToList();
    }

    public static void Run(LaunchSpec launch)
    {
        switch (launch.Kind)
        {
            case "shell": Launcher.OpenShellItem(launch.Target); break;
            case "cmd": Launcher.RunCommandLine(launch.Target); break;
            case "system": RunSystemCommand?.Invoke(launch.Target); break;
            default: Launcher.Open(launch.Target, launch.Args); break;
        }
    }

    private static FontFamily? Font(Entry e)
    {
        if (e.Glyph == null || e.GlyphFont == null) return null;
        try
        {
            return e.GlyphFontBase != null ? new FontFamily(new Uri(e.GlyphFontBase), e.GlyphFont) : new FontFamily(e.GlyphFont);
        }
        catch (Exception)
        {
            return new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_entries);
            File.WriteAllText(_file + ".tmp", json);
            File.Move(_file + ".tmp", _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving history failed", ex);
        }
    }
}
