using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Spotlight.Core;
using Spotlight.Native;
using Spotlight.Providers;
using Spotlight.Services;

namespace Spotlight;

/// <summary>
/// Background app: one instance per user session, Win+Space toggles the search window,
/// a tray icon shows it's running, and HKCU\...\Run starts it with Windows.
/// Command line: (none) open the window · --background start hidden · --quit stop the running instance ·
/// --uninstall remove Spotlight (used by Settings › Apps).
/// </summary>
public partial class App : Application
{
    private const string InstanceId = "Spotlight-6C1F6A52-8E0B-4D5B-9E43-7D2F0A1B9C11";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal, _quitSignal;
    private KeyboardHook? _hook;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private FileIndex? _fileIndex;
    private ContentSearchProvider? _contentSearch;
    private readonly UsageStore _usage = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--query")
        {
            try { Diagnostics.Run(e.Args); }
            catch (Exception ex) { Log.Error("Diagnostics failed", ex); }
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--preview")
        {
            Diagnostics.StartPreview(e.Args);
            return;
        }
        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        bool quit = e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase);

        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Loc.Initialize(CultureInfo.CurrentUICulture);
            Installer.Uninstall(QuitRunningInstance);
            Shutdown();
            return;
        }
        // Started from a download: copy into the programs folder and continue from there.
        if (!quit && Installer.InstallIfNeeded(QuitRunningInstance, e.Args))
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, $@"Local\{InstanceId}", out bool firstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceId}-Show");
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceId}-Quit");
        if (!firstInstance)
        {
            // A second start opens the running instance's window — except a duplicate autostart at login.
            if (quit) _quitSignal.Set();
            else if (!background) _showSignal.Set();
            Shutdown();
            return;
        }
        if (quit)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ev) => { Log.Error("UI exception", ev.Exception); ev.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Fatal exception", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Task exception", ev.Exception); ev.SetObserved(); };

        Loc.Initialize(CultureInfo.CurrentUICulture);
        Log.Info($"Starting (language {Loc.Language}, culture {CultureInfo.CurrentCulture.Name})");
        var config = AppConfig.Load();
        _usage.Load();
        var history = new HistoryStore();
        history.Load();

        // Providers
        var apps = new AppProvider();
        var settings = new SettingsProvider();
        var bookmarks = new BookmarkProvider();
        var calc = new CalcProvider();
        var systemCommands = new SystemCommandProvider();
        HistoryStore.RunSystemCommand = systemCommands.Run;
        _fileIndex = new FileIndex(config);
        _contentSearch = new ContentSearchProvider(config);
        var engine = new SearchEngine(
            [calc, new WebProvider(config), apps, settings, systemCommands, bookmarks,
             new PathProvider(), new FileSearchProvider(_fileIndex)],
            [_contentSearch],
            _usage)
        {
            History = history,
        };

        apps.RefreshIfStale(TimeSpan.Zero);
        settings.LoadInBackground();
        bookmarks.RefreshIfChanged();
        _fileIndex.Start();

        var icons = new IconLoader(Dispatcher);
        _window = new MainWindow(engine, icons, _usage, history, onShowing: () =>
        {
            apps.RefreshIfStale(TimeSpan.FromMinutes(1));
            bookmarks.RefreshIfChanged();
        });
        _window.Initialize();
        calc.RatesUpdated += () => Dispatcher.BeginInvoke(() =>
        {
            if (_window.IsVisible) engine.Search(_window.SearchBox.Text);
        });
        Launcher.Failed += target => Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(Loc.Format("Error.Open", target)));

        Autostart.Sync(config.Autostart);
        if (Installer.IsInstalledCopy) Installer.EnsureRegistered();

        _hook = new KeyboardHook(() =>
        {
            _window.NoteHotkeyFromHook();
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () => _window.OnHotkeyPressed());
        });
        _hook.Start();
        SystemEvents.SessionSwitch += (_, ev) => { if (ev.Reason == SessionSwitchReason.SessionUnlock) _hook.Reinstall(); };
        SystemEvents.PowerModeChanged += (_, ev) => { if (ev.Mode == PowerModes.Resume) _hook.Reinstall(); };
        SystemEvents.SessionEnding += (_, _) => _usage.Save();

        _tray = new TrayIcon(
            open: () => _window.ShowWindow(),
            setAutostart: enabled =>
            {
                config.Autostart = enabled;
                config.Save();
                Autostart.Set(enabled);
            },
            reindex: () =>
            {
                apps.RefreshIfStale(TimeSpan.Zero);
                settings.LoadInBackground();
                _fileIndex.Rebuild();
            },
            clearHistory: history.Clear,
            quit: Quit);

        ListenForSignals();
        CompactMemoryLater();

        if (!config.WelcomeShown)
        {
            _tray.ShowBalloon(Loc.Get("Tray.Running"));
            config.WelcomeShown = true;
            config.Save();
        }
        if (!background) _window.ShowWindow();
    }

    /// <summary>Asks a running instance (e.g. an older version) to exit and waits until it has.</summary>
    private static void QuitRunningInstance()
    {
        string name = $@"Local\{InstanceId}";
        if (!Mutex.TryOpenExisting(name, out var running)) return;
        running.Dispose();
        using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceId}-Quit")) quit.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && Mutex.TryOpenExisting(name, out var stillRunning))
        {
            stillRunning.Dispose();
            Thread.Sleep(100);
        }
    }

    /// <summary>A second start shows the window; "--quit" (used by the installer) ends this instance.</summary>
    private void ListenForSignals()
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showSignal!, _quitSignal! };
            while (true)
            {
                int signaled = WaitHandle.WaitAny(handles);
                if (signaled == 1)
                {
                    Dispatcher.BeginInvoke(Quit);
                    return;
                }
                Dispatcher.BeginInvoke(() => _window?.ShowWindow());
            }
        })
        { IsBackground = true, Name = "InstanceSignals" };
        thread.Start();
    }

    /// <summary>Indexing at startup leaves a lot of garbage (XML, JSON); give it back once everything is loaded.</summary>
    private static void CompactMemoryLater()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
        timer.Start();
    }

    private void Quit()
    {
        Log.Info("Quitting");
        _usage.Save();
        _hook?.Dispose();
        _tray?.Dispose();
        _fileIndex?.Dispose();
        _contentSearch?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
