using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Spotlight.Core;
using Spotlight.Providers;
using Spotlight.Services;

namespace Spotlight;

/// <summary>
/// Troubleshooting modes that run next to a normal instance without touching it (no hotkey, tray or autostart):
/// <c>--query "text" ["text" ...] --out results.txt</c> writes the ranked results of each query;
/// <c>--preview "text" --out shot.png</c> shows the window for that query without taking the focus and saves a
/// screenshot of it.
/// </summary>
internal static class Diagnostics
{
    private sealed record Setup(SearchEngine Engine, AppProvider Apps, SettingsProvider Settings, BookmarkProvider Bookmarks,
        FileIndex Files, CalcProvider Calc, ContentSearchProvider Content, UsageStore Usage);

    /// <param name="demo">Screenshots for publishing: leave out the user's own files and bookmarks.</param>
    private static Setup Build(bool demo = false)
    {
        Loc.Initialize(CultureInfo.CurrentUICulture);
        var config = AppConfig.Load();
        var apps = new AppProvider();
        var settings = new SettingsProvider();
        var bookmarks = new BookmarkProvider();
        var files = new FileIndex(config);
        var calc = new CalcProvider();
        var content = new ContentSearchProvider(config);
        var usage = new UsageStore();
        List<ISearchProvider> providers = demo
            ? [calc, new WebProvider(config), apps, settings, new SystemCommandProvider()]
            : [calc, new WebProvider(config), apps, settings, new SystemCommandProvider(), bookmarks,
               new PathProvider(), new FileSearchProvider(files)];
        var engine = new SearchEngine(providers, demo ? [] : [content], usage);

        apps.RefreshIfStale(TimeSpan.Zero);
        settings.LoadInBackground();
        bookmarks.RefreshIfChanged();
        files.Start();
        return new Setup(engine, apps, settings, bookmarks, files, calc, content, usage);
    }

    private static bool Ready(Setup s) => s.Apps.Count > 0 && s.Settings.Count > 0 && s.Files.Count > 0;

    private static bool Ready(Setup s, bool demo) => demo ? s.Apps.Count > 0 && s.Settings.Count > 0 : Ready(s);

    private static (List<string> Queries, string? Out) ParseArgs(string[] args)
    {
        var queries = new List<string>();
        string? outFile = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
            else if (args[i] != "--demo") queries.Add(args[i]);
        }
        return (queries, outFile);
    }

    public static void Run(string[] args)
    {
        var (queries, outFile) = ParseArgs(args);
        outFile ??= Path.Combine(AppPaths.DataDir, "query-results.txt");
        var s = Build();
        s.Calc.EnsureRatesAsync().Wait(TimeSpan.FromSeconds(15));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Ready(s) && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(100);
        Thread.Sleep(500);

        var sb = new StringBuilder();
        sb.AppendLine($"language={Loc.Language} culture={CultureInfo.CurrentCulture.Name} apps={s.Apps.Count} settings={s.Settings.Count} files={s.Files.Count} bookmarks={s.Bookmarks.Count}");
        foreach (string query in queries)
        {
            SearchOutcome? final = null;
            using var done = new ManualResetEventSlim();
            void OnCompleted(SearchOutcome o)
            {
                if (o.Query.Raw != query || !o.IsFinal) return;
                final = o;
                done.Set();
            }
            s.Engine.Completed += OnCompleted;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            s.Engine.Search(query);
            done.Wait(TimeSpan.FromSeconds(10));
            s.Engine.Completed -= OnCompleted;

            sb.AppendLine().AppendLine($"=== \"{query}\" ({timer.ElapsedMilliseconds} ms)");
            foreach (var entry in final?.Entries ?? [])
            {
                sb.AppendLine(entry switch
                {
                    HeaderEntry h => $"[{h.Title}]",
                    SearchResult r => $"  {r.Rank,6:0} {r.Title} | {r.Subtitle}",
                    _ => "",
                });
            }
        }
        s.Files.Dispose();
        s.Content.Dispose();
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>Starts the screenshot mode; the app shuts down by itself when the picture is saved.</summary>
    public static void StartPreview(string[] args)
    {
        var (queries, outFile) = ParseArgs(args);
        string query = queries.FirstOrDefault() ?? "";
        outFile ??= Path.Combine(AppPaths.DataDir, "preview.png");
        bool demo = args.Contains("--demo");
        var s = Build(demo);
        // The real history, read-only here, so an empty query previews the "Recent" list.
        var history = new HistoryStore();
        history.Load();
        s.Engine.History = history;
        var window = new MainWindow(s.Engine, new IconLoader(Dispatcher.CurrentDispatcher), s.Usage, history, onShowing: () => { });
        window.Initialize(preview: true);

        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        bool shown = false;
        DateTime shownAt = default;
        timer.Tick += (_, _) =>
        {
            if (!shown && (Ready(s, demo) || DateTime.UtcNow - started > TimeSpan.FromSeconds(20)))
            {
                window.ShowPreview(query);
                shown = true;
                shownAt = DateTime.UtcNow;
            }
            else if (shown && DateTime.UtcNow - shownAt > TimeSpan.FromMilliseconds(1800))
            {
                timer.Stop();
                try { Capture(window.Handle, outFile); }
                catch (Exception ex) { Log.Error("Preview capture failed", ex); }
                window.Close();
                s.Files.Dispose();
                s.Content.Dispose();
                Application.Current.Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>Copies the window's screen area (with some surroundings) as composed by DWM, glass included.</summary>
    private static void Capture(IntPtr hwnd, string file)
    {
        Native.NativeMethods.GetWindowRectangle(hwnd, out var r);
        const int margin = 40;
        int x = r.Left - margin, y = r.Top - margin, w = r.Right - r.Left + 2 * margin, h = r.Bottom - r.Top + 2 * margin;
        using var bitmap = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
