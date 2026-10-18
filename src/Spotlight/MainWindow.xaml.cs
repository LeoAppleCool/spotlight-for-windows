using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Spotlight.Core;
using Spotlight.Native;
using Spotlight.Services;

namespace Spotlight;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ForgetQueryAfter = TimeSpan.FromMinutes(10);
    private const int MaskHotkeyId = 0x5350;

    private readonly SearchEngine _engine;
    private readonly IconLoader _icons;
    private readonly UsageStore _usage;
    private readonly HistoryStore _history;
    private readonly Action _onShowing;
    private IntPtr _hwnd;
    private IntPtr _previousForeground;
    private ForegroundWatcher? _foregroundWatcher;
    private bool _maskHotkeyRegistered;
    private DispatcherTimer? _hotkeyFallback;
    private long _hookTriggerTicks;    // written by the keyboard hook thread
    private long _handledTriggerTicks; // UI thread only
    private Query _query = new("");
    private bool _selectionMovedByUser;
    private DateTime _hiddenAtUtc = DateTime.MinValue;

    internal MainWindow(SearchEngine engine, IconLoader icons, UsageStore usage, HistoryStore history, Action onShowing)
    {
        _engine = engine;
        _icons = icons;
        _usage = usage;
        _history = history;
        _onShowing = onShowing;
        InitializeComponent();

        Placeholder.Text = Loc.Get("Search.Placeholder");
        SearchBox.TextChanged += OnTextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => HideWindow();
        ResultsList.PreviewMouseLeftButtonUp += OnResultClicked;
        DpiChanged += (_, e) => _icons.PixelSize = (int)Math.Ceiling(32 * e.NewDpi.DpiScaleX);
        _engine.Completed += outcome => Dispatcher.BeginInvoke(() => Render(outcome));
    }

    internal IntPtr Handle => _hwnd;

    /// <summary>Creates the native window up front so the first Win+Space opens instantly.</summary>
    /// <param name="preview">Screenshot mode of the diagnostics: no hotkey, never steals the focus.</param>
    internal void Initialize(bool preview = false)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        WindowHelper.MakeToolWindow(_hwnd);
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(WndProc);
        if (!preview)
        {
            // Close like Spotlight as soon as another app comes to the front.
            _foregroundWatcher = new ForegroundWatcher(_ => HideWindow());
            // The keyboard hook taps an unassigned key while Win is still held. Registered as Win+<that key> it
            // arrives here as WM_HOTKEY, and receiving a hotkey is what entitles a background app to take the focus.
            _maskHotkeyRegistered = NativeMethods.RegisterHotKey(_hwnd, MaskHotkeyId,
                NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_MASK);
            if (!_maskHotkeyRegistered) Log.Info("Win+vkE8 is taken; using the fallback activation");
        }
        ApplyTheme();
        _icons.PixelSize = (int)Math.Ceiling(32 * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    private void ApplyTheme()
    {
        Theme.Apply(Application.Current.Resources);
        bool glass = WindowHelper.ApplyGlass(_hwnd, Theme.AppsDark, Theme.TransparencyEnabled);
        Root.SetResourceReference(Border.BackgroundProperty, glass ? "Brush.Tint" : "Brush.Solid");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == MaskHotkeyId)
        {
            handled = true;
            // Only when our hook just saw Win+Space (other tools, e.g. AutoHotkey, tap the same key).
            long trigger = Interlocked.Read(ref _hookTriggerTicks);
            if (trigger != 0 && trigger != _handledTriggerTicks && Environment.TickCount64 - trigger < 1000)
            {
                _handledTriggerTicks = trigger;
                _hotkeyFallback?.Stop();
                Toggle();
            }
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            switch (Marshal.PtrToStringUni(lParam))
            {
                case "Environment":
                    EnvironmentRefresher.Refresh();
                    break;
                case "ImmersiveColorSet":
                    Dispatcher.BeginInvoke(ApplyTheme);
                    break;
            }
        }
        return IntPtr.Zero;
    }

    // ---- show / hide ----------------------------------------------------------------------------

    /// <summary>Called on the keyboard hook's thread when it saw Win+Space (before it taps the mask key).</summary>
    internal void NoteHotkeyFromHook() => Interlocked.Exchange(ref _hookTriggerTicks, Environment.TickCount64);

    /// <summary>Called on the UI thread after the keyboard hook saw Win+Space.</summary>
    internal void OnHotkeyPressed()
    {
        long trigger = Interlocked.Read(ref _hookTriggerTicks);
        if (trigger == _handledTriggerTicks) return; // WM_HOTKEY was faster and already toggled
        if (!_maskHotkeyRegistered)
        {
            _handledTriggerTicks = trigger;
            Toggle();
            return;
        }
        // WM_HOTKEY normally follows within milliseconds; should anything swallow it, toggle anyway.
        if (_hotkeyFallback == null)
        {
            _hotkeyFallback = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(250) };
            _hotkeyFallback.Tick += (_, _) =>
            {
                _hotkeyFallback.Stop();
                long pending = Interlocked.Read(ref _hookTriggerTicks);
                if (pending == _handledTriggerTicks) return;
                _handledTriggerTicks = pending;
                Log.Info("Hotkey message missing; toggling from the hook");
                Toggle();
            };
        }
        _hotkeyFallback.Stop();
        _hotkeyFallback.Start();
    }

    internal void Toggle()
    {
        // WPF's IsActive can be stale after hiding, so ask Windows who really has the focus.
        if (IsVisible && NativeMethods.GetForegroundWindow() == _hwnd) HideWindow(restoreFocus: true);
        else ShowWindow();
    }

    internal void ShowWindow()
    {
        _onShowing();
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _hwnd) _previousForeground = foreground;
        if (DateTime.UtcNow - _hiddenAtUtc > ForgetQueryAfter) SearchBox.Text = "";
        PositionOnActiveMonitor();
        Show();
        if (!WindowHelper.ForceForeground(_hwnd)) Log.Info("Could not take the foreground");
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
        _engine.Search(SearchBox.Text); // refreshes the results, or shows the history for an empty box
    }

    /// <summary>Diagnostics screenshot: shows the window with a query without activating it.</summary>
    internal void ShowPreview(string text)
    {
        ShowActivated = false;
        PositionOnActiveMonitor();
        Show();
        SearchBox.Text = text;
        if (text.Length == 0) _engine.Search("");
    }

    /// <param name="restoreFocus">Esc / Win+Space: hand the focus back to the app that had it before.</param>
    internal void HideWindow(bool restoreFocus = false)
    {
        if (!IsVisible) return;
        ClearConfirmation();
        _hiddenAtUtc = DateTime.UtcNow;
        if (restoreFocus && NativeMethods.GetForegroundWindow() == _hwnd && NativeMethods.IsWindow(_previousForeground)
            && NativeMethods.IsWindowVisible(_previousForeground))
            NativeMethods.SetForegroundWindow(_previousForeground);
        Hide();
    }

    private void PositionOnActiveMonitor()
    {
        var (work, scale) = WindowHelper.GetActiveMonitor(_hwnd);
        double widthPx = Width * scale;
        int x = (int)(work.Left + (work.Right - work.Left - widthPx) / 2);
        int y = (int)(work.Top + (work.Bottom - work.Top) * 0.22);
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // ---- searching ------------------------------------------------------------------------------

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectionMovedByUser = false;
        ClearConfirmation();
        _engine.Search(SearchBox.Text);
    }

    private void Render(SearchOutcome outcome)
    {
        if (outcome.Query.Raw != SearchBox.Text) return; // a newer keystroke is on its way
        _query = outcome.Query;

        string? keepKey = _selectionMovedByUser ? (ResultsList.SelectedItem as SearchResult)?.Key : null;
        var entries = outcome.Entries;
        ResultsList.ItemsSource = entries;

        var visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = visibility;
        Divider.Visibility = visibility;
        if (entries.Count == 0) return;

        int index = keepKey == null ? -1 : entries.FindIndex(x => x is SearchResult r && r.Key == keepKey);
        if (index < 0)
        {
            index = FirstSelectable(entries, 0, +1);
            _selectionMovedByUser = false;
        }
        Select(index);

        foreach (var entry in entries)
            if (entry is SearchResult r) _icons.Request(r);
    }

    private List<ListEntry>? Entries => ResultsList.ItemsSource as List<ListEntry>;

    private void Select(int index)
    {
        var entries = Entries;
        if (entries == null || index < 0 || index >= entries.Count) return;
        ResultsList.SelectedIndex = index;
        int first = FirstSelectable(entries, 0, +1);
        ResultsList.ScrollIntoView(index == first ? entries[0] : entries[index > 0 && entries[index - 1].IsHeader ? index - 1 : index]);
        ResultsList.ScrollIntoView(entries[index]);
    }

    private void Move(int delta)
    {
        var entries = Entries;
        if (entries == null || entries.Count == 0) return;
        int step = Math.Sign(delta), target = ResultsList.SelectedIndex;
        for (int i = 0; i < Math.Abs(delta); i++)
        {
            int next = FirstSelectable(entries, target + step, step);
            if (next < 0) break;
            target = next;
        }
        if (target == ResultsList.SelectedIndex) return;
        ClearConfirmation();
        _selectionMovedByUser = true;
        Select(target);
    }

    private static int FirstSelectable(List<ListEntry> entries, int start, int step)
    {
        for (int i = start; i >= 0 && i < entries.Count; i += step)
            if (!entries[i].IsHeader) return i;
        return -1;
    }

    private void ClearConfirmation()
    {
        if (Entries == null) return;
        foreach (var e in Entries)
            if (e is SearchResult { AwaitingConfirmation: true } r) r.AwaitingConfirmation = false;
    }

    // ---- keyboard & mouse -----------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                else HideWindow(restoreFocus: true);
                break;
            case Key.Down: Move(+1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(+6); break;
            case Key.PageUp: Move(-6); break;
            case Key.Enter: ExecuteSelected(mods); break;
            case Key.Tab: Complete(); break;
            case Key.C when mods == ModifierKeys.Control && SearchBox.SelectionLength == 0:
                if (ResultsList.SelectedItem is SearchResult { CopyValue: { } value }) ClipboardHelper.SetText(value);
                break;
            case Key.Delete when SearchBox.Text.Length == 0 && ResultsList.SelectedItem is SearchResult { FromHistory: true } recent:
                _history.Remove(recent.Key);
                _engine.Search("");
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Complete()
    {
        if (ResultsList.SelectedItem is not SearchResult { Completion: { } text }) return;
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
    }

    private void ExecuteSelected(ModifierKeys mods)
    {
        if (ResultsList.SelectedItem is not SearchResult result) return;

        Action? action = result.Execute;
        if (mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift)) action = result.RunAsAdmin;
        else if (mods.HasFlag(ModifierKeys.Control)) action = result.Reveal;
        if (action == null) return;

        if (action == result.Execute && result.NeedsConfirmation && !result.AwaitingConfirmation)
        {
            result.AwaitingConfirmation = true;
            return;
        }

        if (!result.CopyOnEnter) _usage.Record(_query, result.Key);
        _history.Add(result);
        // Whatever gets started may bring itself to the front (we lose that right once hidden).
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        // A copied result goes back to the previous app, ready to paste.
        HideWindow(restoreFocus: result.CopyOnEnter);
        try { action(); }
        catch (Exception ex) { Log.Error($"Running '{result.Title}' failed", ex); }
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsList, source) is not ListBoxItem { DataContext: SearchResult result })
            return;
        ResultsList.SelectedItem = result;
        _selectionMovedByUser = true;
        ExecuteSelected(Keyboard.Modifiers);
        e.Handled = true;
    }
}
