using System.Runtime.InteropServices;

namespace Spotlight.Native;

/// <summary>
/// Reports when a window of another process becomes the foreground window. More reliable than WPF's
/// Deactivated event, which can be skipped after the window was hidden while active.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly WinEventProc _proc; // referenced so the delegate is not collected
    private readonly IntPtr _hook;

    /// <summary>Must be created on a thread with a message loop (the UI thread); the callback runs there.</summary>
    public ForegroundWatcher(Action<IntPtr> onOtherProcessForeground)
    {
        _proc = (_, _, hwnd, _, _, _, _) => onOtherProcessForeground(hwnd);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
