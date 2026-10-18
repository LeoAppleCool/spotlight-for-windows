using System.IO.Enumeration;
using Spotlight.Core;

namespace Spotlight.Providers;

/// <summary>
/// In-memory index of file and folder names in the user's own folders (Desktop, Documents, Downloads, ...).
/// Build output and package folders (node_modules, .git, obj, ...) are skipped, which keeps results clean and
/// matching fast. Kept current with FileSystemWatchers; directory-level changes trigger a debounced re-crawl.
/// </summary>
internal sealed class FileIndex : IDisposable
{
    private const int MaxEntries = 500_000;
    // Cloud placeholders (OneDrive "Files On-Demand") are reparse points too, but must still be crawled.
    private const FileAttributes CloudAttributes = (FileAttributes)(0x40000 | 0x80000 | 0x100000 | 0x400000) | FileAttributes.Offline;

    public sealed class Entry
    {
        public required string Name { get; init; }
        public required int Dir { get; init; }
        public required MatchTarget Target { get; init; }
        public bool IsDir { get; init; }
        public long ModifiedTicks { get; init; }
        public byte Depth { get; init; }
        public bool Deleted { get; set; }
    }

    public readonly record struct Hit(string Path, bool IsDir, int Score);

    private readonly object _gate = new();
    private readonly AppConfig _config;
    private readonly HashSet<string> _excluded;
    private readonly List<FileSystemWatcher> _watchers = [];
    private List<string> _dirs = [];
    private Dictionary<string, int> _dirIds = new(StringComparer.OrdinalIgnoreCase);
    private List<Entry> _entries = [];
    private Timer? _rebuildTimer;
    private int _building;

    public FileIndex(AppConfig config)
    {
        _config = config;
        _excluded = new HashSet<string>(config.ExcludedFolderNames, StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information",
        };
        Roots = ResolveRoots(config);
    }

    public IReadOnlyList<string> Roots { get; }
    public int Count { get { lock (_gate) return _entries.Count; } }

    public void Start()
    {
        ScheduleRebuild(TimeSpan.Zero);
        foreach (var root in Roots) Watch(root);
    }

    private static IReadOnlyList<string> ResolveRoots(AppConfig config)
    {
        var candidates = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            KnownFolders.GetPath(KnownFolders.Downloads),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
        };
        candidates.AddRange(config.ExtraFolders.Select(Environment.ExpandEnvironmentVariables));

        var roots = candidates
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Select(p => Path.GetFullPath(p!).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Drop roots nested in other roots (e.g. Documents redirected into OneDrive).
        return roots.Where(r => !roots.Any(o => o != r && r.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Re-crawls all folders in the background.</summary>
    public void Rebuild() => ScheduleRebuild(TimeSpan.Zero);

    private void ScheduleRebuild(TimeSpan delay)
    {
        _rebuildTimer ??= new Timer(_ => RebuildCore(), null, Timeout.Infinite, Timeout.Infinite);
        _rebuildTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void RebuildCore()
    {
        if (Interlocked.Exchange(ref _building, 1) == 1)
        {
            ScheduleRebuild(TimeSpan.FromSeconds(30));
            return;
        }
        var previousPriority = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var dirs = new List<string>(20_000);
            var dirIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<Entry>(100_000);

            foreach (var root in Roots)
            {
                string? parent = Path.GetDirectoryName(root);
                if (parent != null)
                    entries.Add(NewEntry(Path.GetFileName(root), GetDirId(parent, dirs, dirIds), true, Directory.GetLastWriteTimeUtc(root).Ticks, 0));
                Crawl(root, dirs, dirIds, entries);
                if (entries.Count >= MaxEntries) break;
            }

            lock (_gate)
            {
                _dirs = dirs;
                _dirIds = dirIds;
                _entries = entries;
            }
            Log.Info($"File index built: {entries.Count} items in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Error("Building file index failed", ex);
        }
        finally
        {
            Thread.CurrentThread.Priority = previousPriority;
            _building = 0;
        }
    }

    private void Crawl(string root, List<string> dirs, Dictionary<string, int> dirIds, List<Entry> entries)
    {
        var excluded = _excluded.GetAlternateLookup<ReadOnlySpan<char>>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.Temporary,
            ReturnSpecialDirectories = false,
        };
        int rootDepth = root.Count(c => c == '\\');
        var items = new FileSystemEnumerable<(string Dir, string Name, bool IsDir, long Ticks)>(root,
            (ref FileSystemEntry e) => (e.Directory.ToString(), e.FileName.ToString(), e.IsDirectory, e.LastWriteTimeUtc.UtcTicks),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                e.FileName.Length > 0 && e.FileName[0] != '.' && e.FileName[0] != '~'
                && !(e.IsDirectory && excluded.Contains(e.FileName)),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                e.FileName.Length > 0 && e.FileName[0] != '.' && !excluded.Contains(e.FileName)
                && ((e.Attributes & FileAttributes.ReparsePoint) == 0 || (e.Attributes & CloudAttributes) != 0),
        };

        foreach (var (dir, name, isDir, ticks) in items)
        {
            int depth = dir.Count(c => c == '\\') - rootDepth;
            entries.Add(NewEntry(name, GetDirId(dir, dirs, dirIds), isDir, ticks, depth + 1));
            if (entries.Count >= MaxEntries) return;
        }
    }

    private static Entry NewEntry(string name, int dir, bool isDir, long ticks, int depth) => new()
    {
        Name = name,
        Dir = dir,
        Target = new MatchTarget(name),
        IsDir = isDir,
        ModifiedTicks = ticks,
        Depth = (byte)Math.Min(depth, 255),
    };

    private static int GetDirId(string dir, List<string> dirs, Dictionary<string, int> dirIds)
    {
        if (dirIds.TryGetValue(dir, out int id)) return id;
        id = dirs.Count;
        dirs.Add(dir);
        dirIds[dir] = id;
        return id;
    }

    public List<Hit> Search(Query query, int max, CancellationToken ct)
    {
        var best = new PriorityQueue<(Entry Entry, string Dir), int>(max + 1);
        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if ((i & 0xFFF) == 0 && ct.IsCancellationRequested) return [];
                var e = _entries[i];
                if (e.Deleted) continue;
                int score = Matcher.Score(query, e.Target, allowFuzzy: false);
                if (score < 520) continue;
                score += Bonus(e, now);
                best.Enqueue((e, _dirs[e.Dir]), score);
                if (best.Count > max) best.Dequeue();
            }
        }

        var hits = new List<Hit>(best.Count);
        while (best.TryDequeue(out var item, out int score))
            hits.Add(new Hit(Path.Combine(item.Dir, item.Entry.Name), item.Entry.IsDir, score));
        hits.Reverse();
        return hits;
    }

    private static int Bonus(Entry e, long now)
    {
        double days = (now - e.ModifiedTicks) / (double)TimeSpan.TicksPerDay;
        int bonus = days < 2 ? 45 : days < 14 ? 30 : days < 90 ? 15 : 0;
        bonus += 24 - Math.Min((int)e.Depth, 10) * 7; // deep paths are usually program data, not the user's files
        if (!e.IsDir && (FileKinds.IsDeveloperFile(e.Name) || !e.Name.Contains('.'))) bonus -= 30; // code, lock and config files
        return bonus;
    }

    // ---- incremental updates -----------------------------------------------------------------

    private void Watch(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => OnCreated(e.FullPath);
            watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
            watcher.Renamed += (_, e) => { OnDeleted(e.OldFullPath); OnCreated(e.FullPath); };
            watcher.Error += (_, e) => { Log.Error("File watcher overflow", e.GetException()); ScheduleRebuild(TimeSpan.FromSeconds(10)); };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Log.Error($"Cannot watch {root}", ex);
        }
    }

    private bool IsIgnoredPath(string path)
    {
        foreach (var segment in path.Split('\\'))
            if (segment.Length > 0 && (segment[0] == '.' || segment[0] == '~' || _excluded.Contains(segment))) return true;
        return false;
    }

    private void OnCreated(string path)
    {
        try
        {
            if (IsIgnoredPath(path)) return;
            if (Directory.Exists(path))
            {
                ScheduleRebuild(TimeSpan.FromSeconds(60)); // its contents arrive without separate events when moved in
                AddEntry(path, isDir: true);
            }
            else if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary)) == 0)
                    AddEntry(path, isDir: false);
            }
        }
        catch { }
    }

    private void AddEntry(string path, bool isDir)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir == null) return;
        string root = Roots.FirstOrDefault(r => path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) ?? dir;
        int depth = dir.Count(c => c == '\\') - root.Count(c => c == '\\') + 1;
        long ticks = isDir ? Directory.GetLastWriteTimeUtc(path).Ticks : File.GetLastWriteTimeUtc(path).Ticks;
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries) return;
            _entries.Add(NewEntry(Path.GetFileName(path), GetDirId(dir, _dirs, _dirIds), isDir, ticks, depth));
        }
    }

    private void OnDeleted(string path)
    {
        if (IsIgnoredPath(path)) return;
        string? dir = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (dir == null) return;
        lock (_gate)
        {
            if (!_dirIds.TryGetValue(dir, out int dirId)) return;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Dir == dirId && !e.Deleted && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    e.Deleted = true;
                    if (e.IsDir) ScheduleRebuild(TimeSpan.FromSeconds(60));
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _rebuildTimer?.Dispose();
    }
}

internal static class FileKinds
{
    private static readonly HashSet<string> Images = Set(".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".svg", ".ico", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".psd", ".avif", ".jfif");
    private static readonly HashSet<string> Media = Set(".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp");
    private static readonly HashSet<string> Documents = Set(".pdf", ".doc", ".docx", ".docm", ".dot", ".dotx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".csv", ".epub", ".pages", ".numbers", ".key", ".one", ".xps", ".tex", ".html", ".htm", ".eml", ".msg", ".vsdx", ".pub");
    private static readonly HashSet<string> Developer = Set(".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".map", ".json", ".lock", ".yml", ".yaml", ".xml", ".css", ".scss", ".less", ".h", ".hpp", ".c", ".cc", ".cpp", ".cs", ".py", ".pyc", ".java", ".class", ".jar", ".kt", ".go", ".rs", ".rb", ".php", ".dll", ".pdb", ".obj", ".o", ".log", ".tmp", ".cache", ".bin", ".dat", ".sln", ".csproj", ".props", ".targets", ".gradle", ".toml", ".ini", ".cfg");
    private static readonly HashSet<string> Executables = Set(".exe", ".msi", ".bat", ".cmd", ".ps1", ".lnk", ".appref-ms");
    private static readonly HashSet<string> OwnIcon = Set(".exe", ".lnk", ".ico", ".url", ".appref-ms", ".cur", ".ani", ".msc", ".cpl", ".scr");

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    public static Category CategoryOf(string path, bool isDir)
    {
        if (isDir) return Category.Folders;
        string ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return Category.Images;
        if (Media.Contains(ext)) return Category.Media;
        if (Documents.Contains(ext)) return Category.Documents;
        return Category.Files;
    }

    public static bool IsDeveloperFile(string name) => Developer.Contains(Path.GetExtension(name));
    public static bool IsExecutable(string path) => Executables.Contains(Path.GetExtension(path));

    /// <summary>Icon cache key: icons that depend only on the file type are shared per extension.</summary>
    public static string IconKey(string path, bool isDir)
    {
        string ext = Path.GetExtension(path);
        if (isDir || ext.Length == 0 || OwnIcon.Contains(ext) || Images.Contains(ext)) return "path:" + path.ToLowerInvariant();
        return "ext:" + ext.ToLowerInvariant();
    }
}
