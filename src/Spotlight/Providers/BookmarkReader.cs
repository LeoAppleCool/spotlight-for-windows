using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Spotlight.Providers;

/// <summary>A bookmark read from an installed browser profile.</summary>
/// <param name="Title">Bookmark title; falls back to the URL host.</param>
/// <param name="Url">An http, https, file or ftp URL.</param>
/// <param name="Browser">Display name, e.g. "Chrome", or "Chrome (Profile 1)" when several profiles have bookmarks.</param>
/// <param name="BrowserExePath">The browser executable (for its icon), or null when it was not found.</param>
/// <param name="Folder">Folder path joined with " › ", without the root folders (bar, other, menu, ...), or null.</param>
public sealed record Bookmark(string Title, string Url, string Browser, string? BrowserExePath, string? Folder);

/// <summary>
/// Reads the bookmarks of the current user's Chromium- and Firefox-family browsers, strictly read-only.
/// Chromium: each profile's "Bookmarks" / "AccountBookmarks" JSON. Firefox: places.sqlite via Windows' built-in
/// winsqlite3.dll, opened immutable (no locks, no writes) or, while the running browser still holds recent changes in
/// places.sqlite-wal (which an immutable open ignores), from a short-lived private copy.
/// </summary>
public static class BookmarkReader
{
    private const string FolderSeparator = " \u203A ";
    private const int MaxFolderDepth = 100;
    private const long MaxFileBytes = 64L << 20;
    private const long WalHeaderBytes = 32;

    // Folder paths are built downwards from the visible roots, so the roots' own names are left out and so are the
    // tag folders (tags root) whose entries merely duplicate real bookmarks. The depth limit guards against cycles.
    private const string PlacesSql = $"""
        WITH RECURSIVE folder(id, path, depth) AS (
            SELECT id, NULL, 0 FROM moz_bookmarks WHERE guid IN ('menu________', 'toolbar_____', 'unfiled_____', 'mobile______')
            UNION ALL
            SELECT b.id, CASE WHEN trim(coalesce(b.title, '')) = '' THEN f.path WHEN f.path IS NULL THEN trim(b.title)
                              ELSE f.path || '{FolderSeparator}' || trim(b.title) END, f.depth + 1
            FROM moz_bookmarks b JOIN folder f ON b.parent = f.id WHERE b.type = 2 AND f.depth < 100)
        SELECT b.title, p.url, f.path FROM moz_bookmarks b JOIN moz_places p ON b.fk = p.id JOIN folder f ON b.parent = f.id
        WHERE b.type = 1 AND p.url NOT LIKE 'place:%' AND p.url NOT LIKE 'javascript:%'
        """;

    private static readonly string[] ChromiumRoots = ["bookmark_bar", "other", "synced"];

    // Usual install locations, first existing wins (%ProgramFiles% is also tried as %ProgramW6432%, the 64-bit folder).
    private static readonly BrowserDef Chrome = new("Chrome", @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
        @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe");
    private static readonly BrowserDef Edge = new("Edge",
        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe");
    private static readonly BrowserDef Brave = new("Brave", @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
        @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe");
    private static readonly BrowserDef Vivaldi = new("Vivaldi", @"%LOCALAPPDATA%\Vivaldi\Application\vivaldi.exe", @"%ProgramFiles%\Vivaldi\Application\vivaldi.exe");
    private static readonly BrowserDef Chromium = new("Chromium", @"%LOCALAPPDATA%\Chromium\Application\chrome.exe", @"%ProgramFiles%\Chromium\Application\chrome.exe");
    private static readonly BrowserDef Opera = new("Opera", @"%LOCALAPPDATA%\Programs\Opera\opera.exe", @"%ProgramFiles%\Opera\opera.exe");
    private static readonly BrowserDef OperaGx = new("Opera GX", @"%LOCALAPPDATA%\Programs\Opera GX\opera.exe", @"%ProgramFiles%\Opera GX\opera.exe");
    private static readonly BrowserDef Firefox = new("Firefox", @"%ProgramFiles%\Mozilla Firefox\firefox.exe",
        @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe", @"%LOCALAPPDATA%\Mozilla Firefox\firefox.exe");
    private static readonly BrowserDef Zen = new("Zen", @"%ProgramFiles%\Zen Browser\zen.exe", @"%LOCALAPPDATA%\Zen Browser\zen.exe");
    private static readonly BrowserDef Floorp = new("Floorp", @"%ProgramFiles%\Ablaze Floorp\floorp.exe");
    private static readonly BrowserDef LibreWolf = new("LibreWolf", @"%ProgramFiles%\LibreWolf\librewolf.exe");
    private static readonly BrowserDef Waterfox = new("Waterfox", @"%ProgramFiles%\Waterfox\waterfox.exe");

    private static readonly object CopyGate = new();
    private static bool _tempLeftoversRemoved;
    private static volatile bool _sqliteUnavailable;

    private enum Family { Chromium, Firefox }
    private sealed record BrowserDef(string Name, params string[] ExeCandidates);
    private sealed record ProfileSource(BrowserDef Browser, Family Family, string Dir, string Profile, bool IsDefault);
    private readonly record struct Item(string Title, string Url, string? Folder);

    /// <summary>
    /// Reads bookmarks of all installed browsers/profiles for the current user. Never throws; skips unreadable sources.
    /// Blocking file I/O (a few ms; tens of ms while a Firefox-family browser runs and its places.sqlite is copied).
    /// </summary>
    public static IReadOnlyList<Bookmark> LoadAll()
    {
        var result = new List<Bookmark>();
        try
        {
            var loaded = new List<(ProfileSource Source, List<Item> Items)>();
            foreach (var source in DiscoverSources())
            {
                List<Item> items = [];
                try { items = source.Family == Family.Chromium ? ReadChromiumProfile(source.Dir) : ReadFirefoxProfile(source.Dir); }
                catch (Exception) { /* skip this source */ }
                if (items.Count > 0) loaded.Add((source, items));
            }

            var seen = new HashSet<(string Url, string Browser)>();
            foreach (var group in loaded.GroupBy(entry => entry.Source.Browser))
            {
                bool severalProfiles = group.Count() > 1; // only then are non-default profiles named
                string? exePath = ResolveExe(group.Key);
                foreach (var (source, items) in group)
                {
                    string browser = severalProfiles && !source.IsDefault ? $"{group.Key.Name} ({source.Profile})" : group.Key.Name;
                    foreach (var item in items)
                        if (seen.Add((item.Url, browser))) result.Add(new Bookmark(item.Title, item.Url, browser, exePath, item.Folder));
                }
            }
        }
        catch (Exception) { /* never throw; keep what was collected */ }
        return result;
    }

    /// <summary>
    /// Files whose last-write time changes with the bookmarks: each Chromium profile's "Bookmarks" and "AccountBookmarks",
    /// each Firefox-family profile's places.sqlite and places.sqlite-wal. Paths may not exist (yet). Note that a running
    /// Firefox-family browser also writes the -wal on every page visit.
    /// </summary>
    public static IReadOnlyList<string> GetSourceFiles()
    {
        try
        {
            return DiscoverSources().SelectMany(s => s.Family == Family.Chromium
                    ? new[] { Path.Combine(s.Dir, "Bookmarks"), Path.Combine(s.Dir, "AccountBookmarks") }
                    : new[] { Path.Combine(s.Dir, "places.sqlite"), Path.Combine(s.Dir, "places.sqlite-wal") })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return []; }
    }

    // ------------------------------------------------------------------------------------------------ discovery

    private static List<ProfileSource> DiscoverSources()
    {
        var list = new List<ProfileSource>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (local.Length > 0)
        {
            AddChromium(list, Chrome, Path.Combine(local, @"Google\Chrome\User Data"));
            AddChromium(list, Edge, Path.Combine(local, @"Microsoft\Edge\User Data"));
            AddChromium(list, Brave, Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"));
            AddChromium(list, Vivaldi, Path.Combine(local, @"Vivaldi\User Data"));
            AddChromium(list, Chromium, Path.Combine(local, @"Chromium\User Data"));
        }
        if (roaming.Length > 0)
        {
            AddOpera(list, Opera, Path.Combine(roaming, @"Opera Software\Opera Stable"));
            AddOpera(list, OperaGx, Path.Combine(roaming, @"Opera Software\Opera GX Stable"));
            AddFirefox(list, Firefox, Path.Combine(roaming, @"Mozilla\Firefox"));
            AddFirefox(list, Zen, Path.Combine(roaming, "zen"));
            AddFirefox(list, Floorp, Path.Combine(roaming, "Floorp"));
            AddFirefox(list, LibreWolf, Path.Combine(roaming, "librewolf"));
            AddFirefox(list, Waterfox, Path.Combine(roaming, "Waterfox"));
        }
        try
        {
            string packages = Path.Combine(local, "Packages"); // Microsoft Store Firefox
            if (local.Length > 0 && Directory.Exists(packages))
                foreach (string package in Directory.EnumerateDirectories(packages, "Mozilla.Firefox_*"))
                    AddFirefox(list, Firefox, Path.Combine(package, @"LocalCache\Roaming\Mozilla\Firefox"));
        }
        catch (Exception) { /* optional */ }
        return list;
    }

    // "Default" and "Profile N" only; "Guest Profile", "System Profile" and the many cache folders are no profiles.
    private static void AddChromium(List<ProfileSource> list, BrowserDef browser, string userDataDir)
    {
        try
        {
            if (!Directory.Exists(userDataDir)) return;
            var profiles = new List<(int Order, string Name, string Dir)>();
            foreach (string dir in Directory.EnumerateDirectories(userDataDir))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    profiles.Add((-1, name, dir));
                else if (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(8), out int number))
                    profiles.Add((number, name, dir));
            }
            foreach (var (order, name, dir) in profiles.OrderBy(p => p.Order))
                list.Add(new ProfileSource(browser, Family.Chromium, dir, name, IsDefault: order < 0));
        }
        catch (Exception) { /* unreadable user data folder: skip the browser */ }
    }

    // Opera keeps "Bookmarks" in the user data folder itself; newer builds in its "Default" subfolder.
    private static void AddOpera(List<ProfileSource> list, BrowserDef browser, string dir)
    {
        foreach (string candidate in new[] { dir, Path.Combine(dir, "Default") })
            if (Directory.Exists(candidate)) list.Add(new ProfileSource(browser, Family.Chromium, candidate, "Default", IsDefault: true));
    }

    // profiles.ini: [ProfileN] Name/Path/IsRelative/Default=1 and [Install<hash>] Default=<path of that install's profile>.
    private static void AddFirefox(List<ProfileSource> list, BrowserDef browser, string rootDir)
    {
        try
        {
            string ini = Path.Combine(rootDir, "profiles.ini");
            if (!File.Exists(ini) || ReadAllBytesShared(ini) is not { } iniBytes) return;
            var installDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var profiles = new List<(string Dir, string Name, bool Flagged)>();
            foreach (var (section, keys) in ParseIni(Encoding.UTF8.GetString(iniBytes).TrimStart('\uFEFF')))
            {
                if (section.StartsWith("Install", StringComparison.OrdinalIgnoreCase))
                {
                    if (keys.TryGetValue("Default", out string? path) && ResolveProfileDir(rootDir, path, null) is { } dir) installDefaults.Add(dir);
                }
                else if (section.Length > 7 && section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)
                         && section.AsSpan(7).IndexOfAnyExceptInRange('0', '9') < 0 && keys.TryGetValue("Path", out string? path))
                {
                    bool? relative = keys.TryGetValue("IsRelative", out string? isRelative) ? isRelative == "1" : null;
                    if (ResolveProfileDir(rootDir, path, relative) is not { } dir) continue;
                    string name = keys.TryGetValue("Name", out string? n) && n.Length > 0 ? n : ProfileFolderName(dir);
                    profiles.Add((dir, name, keys.TryGetValue("Default", out string? flag) && flag == "1"));
                }
            }
            foreach (string dir in installDefaults)
                if (!profiles.Exists(p => p.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase))) profiles.Add((dir, ProfileFolderName(dir), false));

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, name, flagged) in profiles)
            {
                if (!added.Add(dir) || !Directory.Exists(dir)) continue;
                // Current versions name each install's default profile in [Install*]; Default=1 is the legacy marker.
                bool isDefault = installDefaults.Count > 0 ? installDefaults.Contains(dir) : flagged;
                list.Add(new ProfileSource(browser, Family.Firefox, dir, name, isDefault));
            }
        }
        catch (Exception) { /* unreadable profiles.ini: skip the browser */ }
    }

    private static string? ResolveProfileDir(string rootDir, string path, bool? isRelative)
    {
        try
        {
            path = path.Trim().Replace('/', '\\');
            if (path.Length == 0) return null;
            if (isRelative == true || !Path.IsPathRooted(path)) path = Path.Combine(rootDir, path);
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception) { return null; }
    }

    // "ab12cd34.default-release" -> "default-release"
    private static string ProfileFolderName(string dir)
    {
        string name = Path.GetFileName(dir);
        return name.IndexOf('.') is > 0 and var dot && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    private static List<(string Section, Dictionary<string, string> Keys)> ParseIni(string text)
    {
        var sections = new List<(string, Dictionary<string, string>)>();
        Dictionary<string, string>? current = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
                sections.Add((line[1..^1].Trim(), current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
            else if (current is not null && line.IndexOf('=') is > 0 and var eq)
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    // ------------------------------------------------------------------------------------------------ Chromium

    private static List<Item> ReadChromiumProfile(string dir)
    {
        var items = new List<Item>();
        ReadChromiumFile(Path.Combine(dir, "Bookmarks"), items);
        ReadChromiumFile(Path.Combine(dir, "AccountBookmarks"), items); // saved to the Google account without sync
        return items;
    }

    private static void ReadChromiumFile(string path, List<Item> items)
    {
        try
        {
            if (!File.Exists(path) || ReadAllBytesShared(path) is not { } bytes) return;
            ReadOnlyMemory<byte> json = bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes.AsMemory(3) : bytes;
            var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 2 * MaxFolderDepth + 16 };
            using var document = JsonDocument.Parse(json, options);
            if (!document.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object) return;
            foreach (string key in ChromiumRoots)
                if (roots.TryGetProperty(key, out var root)) WalkChromium(root, null, items, 0);
            // Opera: speed dials, unsorted bookmarks and "my folders" live under custom_root.
            if (roots.TryGetProperty("custom_root", out var custom) && custom.ValueKind == JsonValueKind.Object)
                foreach (var root in custom.EnumerateObject())
                    if (!root.NameEquals("trash")) WalkChromium(root.Value, null, items, 0);
        }
        catch (Exception) { /* unreadable or half-written file: skip it */ }
    }

    private static void WalkChromium(JsonElement node, string? folder, List<Item> items, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object || depth > MaxFolderDepth) return;
        if (GetString(node, "type") == "url")
        {
            AddItem(items, GetString(node, "name"), GetString(node, "url"), folder);
            return;
        }
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;
        if (depth > 0 && GetString(node, "name")?.Trim() is { Length: > 0 } name) // depth 0 = a root, not part of the path
            folder = folder is null ? name : folder + FolderSeparator + name;
        foreach (var child in children.EnumerateArray()) WalkChromium(child, folder, items, depth + 1);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ------------------------------------------------------------------------------------------------ Firefox

    private static List<Item> ReadFirefoxProfile(string dir)
    {
        string db = Path.Combine(dir, "places.sqlite");
        if (_sqliteUnavailable || !File.Exists(db)) return [];
        // An immutable open never locks or writes, but it ignores the -wal, which holds every change since the browser's
        // last checkpoint (e.g. a bookmark added a minute ago). So while the WAL has frames, read a private copy first.
        var wal = new FileInfo(db + "-wal");
        bool walHasFrames = wal.Exists && wal.Length > WalHeaderBytes;
        return (walHasFrames ? ReadViaCopy(db) ?? ReadImmutable(db) : ReadImmutable(db) ?? ReadViaCopy(db)) ?? [];
    }

    private static List<Item>? ReadImmutable(string db)
    {
        try { return QueryPlaces(ToImmutableUri(db), Sqlite.OpenReadOnly | Sqlite.OpenUri); }
        catch (Exception) { return null; }
    }

    // Copies places.sqlite (+ -wal) to %TEMP%\Spotlight-bookmarks\<hash>\, queries the copy, then deletes it.
    private static List<Item>? ReadViaCopy(string db)
    {
        if (_sqliteUnavailable) return null;
        lock (CopyGate)
        {
            string? tempRoot = null, dir = null;
            try
            {
                tempRoot = Path.Combine(Path.GetTempPath(), "Spotlight-bookmarks");
                if (!_tempLeftoversRemoved) TryDeleteDirectory(tempRoot); // copies left by a crashed run (they hold the history)
                _tempLeftoversRemoved = true;
                dir = Path.Combine(tempRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(db.ToUpperInvariant())), 0, 8));
                TryDeleteDirectory(dir);
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, "places.sqlite");
                CopySnapshot(db, copy);
                return QueryPlaces(copy, Sqlite.OpenReadOnly); // read-only: replays the copied WAL, never checkpoints
            }
            catch (Exception) { return null; }
            finally
            {
                if (dir is not null) TryDeleteDirectory(dir);
                try { if (tempRoot is not null) Directory.Delete(tempRoot); } catch (Exception) { /* not empty */ }
            }
        }
    }

    // db first, then wal. A checkpoint (the browser writing the db) during the copy could pair a stale db with a newer
    // wal, so retry while the db changed meanwhile; SQLite rejects whatever is still inconsistent.
    private static void CopySnapshot(string db, string copy)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var before = Stamp(db);
            CopyShared(db, copy);
            if (File.Exists(db + "-wal")) CopyShared(db + "-wal", copy + "-wal");
            else File.Delete(copy + "-wal");
            if (Stamp(db) == before) return;
        }

        static (long, DateTime) Stamp(string path) => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    }

    private static List<Item>? QueryPlaces(string fileNameOrUri, int flags)
    {
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(fileNameOrUri + "\0"), out db, flags | Sqlite.OpenNoMutex, IntPtr.Zero) != Sqlite.Ok
                || Sqlite.sqlite3_prepare16_v2(db, PlacesSql, -1, out statement, IntPtr.Zero) != Sqlite.Ok || statement == IntPtr.Zero)
                return null;
            var items = new List<Item>();
            int rc;
            while ((rc = Sqlite.sqlite3_step(statement)) == Sqlite.Row)
                AddItem(items, ColumnText(statement, 0), ColumnText(statement, 1), ColumnText(statement, 2));
            return rc == Sqlite.Done ? items : null; // SQLITE_BUSY & co. return at once: no busy timeout is set
        }
        catch (Exception ex)
        {
            if (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) _sqliteUnavailable = true;
            return null;
        }
        finally
        {
            if (statement != IntPtr.Zero) _ = Sqlite.sqlite3_finalize(statement);
            if (db != IntPtr.Zero) _ = Sqlite.sqlite3_close_v2(db);
        }
    }

    private static string? ColumnText(IntPtr statement, int column)
    {
        IntPtr text = Sqlite.sqlite3_column_text16(statement, column); // must precede column_bytes16
        return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, Sqlite.sqlite3_column_bytes16(statement, column) / 2);
    }

    // file:///C:/Users/x/AppData/Roaming/zen/Profiles/ab.Default%20%28release%29/places.sqlite?immutable=1 (UNC: file:////host/...)
    private static string ToImmutableUri(string path)
    {
        const string hex = "0123456789ABCDEF";
        string full = Path.GetFullPath(path).Replace('\\', '/');
        var uri = new StringBuilder(full.Length + 32).Append(full.StartsWith("//", StringComparison.Ordinal) ? "file://" : "file:///");
        foreach (byte b in Encoding.UTF8.GetBytes(full))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'/' or (byte)':' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~') uri.Append((char)b);
            else uri.Append('%').Append(hex[b >> 4]).Append(hex[b & 0xF]);
        }
        return uri.Append("?immutable=1").ToString();
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static void AddItem(List<Item> items, string? title, string? url, string? folder)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url) || !IsSupportedUrl(url)) return;
        title = title?.Trim().ReplaceLineEndings(" ");
        items.Add(new Item(string.IsNullOrEmpty(title) ? HostOf(url) : title, url, folder));
    }

    private static bool IsSupportedUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url;

    private static string? ResolveExe(BrowserDef browser)
    {
        foreach (string candidate in browser.ExeCandidates)
        {
            // In a 32-bit process %ProgramFiles% is the x86 folder; %ProgramW6432% is always the native one.
            string[] variants = candidate.StartsWith("%ProgramFiles%", StringComparison.Ordinal) ? ["%ProgramW6432%" + candidate[14..], candidate] : [candidate];
            foreach (string variant in variants)
            {
                string path = Environment.ExpandEnvironmentVariables(variant);
                if (!path.Contains('%') && Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
            }
        }
        return null;
    }

    // Shares read/write/delete: the browser may keep writing, atomically replacing or deleting the file meanwhile.
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static byte[]? ReadAllBytesShared(string path)
    {
        using var stream = OpenShared(path);
        if (stream.Length > MaxFileBytes) return null;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void CopyShared(string source, string target)
    {
        using var input = OpenShared(source);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        input.CopyTo(output, 1 << 20);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* e.g. held by a virus scanner; removed on the next run */ }
    }

    // Windows' own SQLite (System32\winsqlite3.dll, __stdcall exports): no NuGet package needed.
    private static class Sqlite
    {
        private const string Lib = "winsqlite3.dll";
        public const int Ok = 0, Row = 100, Done = 101;
        public const int OpenReadOnly = 0x1, OpenUri = 0x40, OpenNoMutex = 0x8000;

        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_open_v2(byte[] utf8FileName, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_step(IntPtr statement);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_column_bytes16(IntPtr statement, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_finalize(IntPtr statement);
    }
}
