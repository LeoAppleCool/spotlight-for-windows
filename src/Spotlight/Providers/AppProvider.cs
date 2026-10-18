using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Spotlight.Core;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>
/// Everything in the Start menu's "All apps" list — classic programs and Store apps — read from the shell's
/// AppsFolder, so names are already in the Windows display language.
/// </summary>
internal sealed partial class AppProvider : ISearchProvider
{
    private sealed record AppEntry(string Name, string AppId, MatchTarget Target, MatchTarget? ExeTarget, string? FilePath, bool IsStoreApp, bool IsUninstaller);

    private volatile IReadOnlyList<AppEntry> _apps = [];
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _refreshing;

    public int Count => _apps.Count;

    /// <summary>Re-reads the app list in the background if it is older than <paramref name="maxAge"/>.</summary>
    public void RefreshIfStale(TimeSpan maxAge)
    {
        if (DateTime.UtcNow - _lastRefresh < maxAge || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        var thread = new Thread(() =>
        {
            try { Refresh(); }
            catch (Exception ex) { Log.Error("Reading apps failed", ex); }
            finally { _refreshing = 0; }
        })
        { IsBackground = true, Name = "AppScan", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); // Shell.Application needs STA
        thread.Start();
    }

    private void Refresh()
    {
        var list = new List<AppEntry>(300);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic items = shell.NameSpace("shell:AppsFolder").Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;

                string? file = ResolveFilePath(id);
                bool store = id.Contains('!') && !id.Contains('\\');
                string? exeName = file != null ? Path.GetFileNameWithoutExtension(file) : null;
                bool uninstaller = UninstallerRegex().IsMatch(name)
                    || (exeName != null && exeName.StartsWith("unins", StringComparison.OrdinalIgnoreCase));

                list.Add(new AppEntry(
                    name, id, new MatchTarget(name),
                    exeName != null && !name.Contains(exeName, StringComparison.OrdinalIgnoreCase) ? new MatchTarget(exeName) : null,
                    file, store, uninstaller));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        _apps = list;
        _lastRefresh = DateTime.UtcNow;
        Log.Info($"Apps indexed: {list.Count}");
    }

    /// <summary>App IDs of classic programs are file paths, sometimes relative to a known folder GUID.</summary>
    private static string? ResolveFilePath(string id)
    {
        try
        {
            var m = KnownFolderRegex().Match(id);
            if (m.Success)
            {
                string? root = KnownFolders.GetPath(Guid.Parse(m.Groups[1].Value));
                if (root == null) return null;
                id = Path.Combine(root, m.Groups[2].Value);
            }
            return id.Length > 3 && id[1] == ':' && File.Exists(id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        foreach (var app in _apps)
        {
            int score = Matcher.Score(query, app.Target);
            if (app.ExeTarget != null) score = Math.Max(score, Matcher.Score(query, app.ExeTarget, allowFuzzy: false) * 85 / 100);
            if (score <= 0) continue;
            if (app.IsUninstaller) score -= 200;

            string parsingName = @"shell:AppsFolder\" + app.AppId;
            string? file = app.FilePath;
            results.Add(new SearchResult
            {
                Key = "app:" + app.AppId,
                Title = app.Name,
                Subtitle = Loc.Get("Sub.Application"),
                Category = Category.Applications,
                Score = score,
                IconSource = parsingName,
                IconCacheKey = "app:" + app.AppId,
                Execute = () => Launcher.OpenShellItem(parsingName),
                Launch = new LaunchSpec("shell", parsingName),
                RunAsAdmin = app.IsStoreApp ? null : () => Launcher.OpenShellItem(parsingName, "runas"),
                Reveal = file != null ? () => Launcher.Reveal(file) : null,
                CopyValue = file ?? app.Name,
            });
        }
    }

    [GeneratedRegex(@"^\{([0-9A-Fa-f\-]{36})\}\\(.+)$")]
    private static partial Regex KnownFolderRegex();

    [GeneratedRegex(@"^(Uninstall|Deinstall|Désinstaller|Desinstalar|Disinstalla|Verwijder)", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerRegex();
}

internal static class KnownFolders
{
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string? GetPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
}
