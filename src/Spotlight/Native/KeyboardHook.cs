using System.Runtime.InteropServices;
using static Spotlight.Native.NativeMethods;

namespace Spotlight.Native;

/// <summary>
/// Catches Win+Space with a low-level keyboard hook. Windows reserves that combination for switching the input
/// language, so RegisterHotKey fails with ERROR_HOTKEY_ALREADY_REGISTERED; a hook is the only reliable way.
/// The hook lives on its own thread with a message loop so a busy UI thread can never make Windows drop it.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const uint WmReinstall = WM_APP + 1;
    private static readonly UIntPtr OwnInputMarker = new(0x53504F54); // "SPOT"

    private readonly Action _onTriggered;
    private readonly LowLevelKeyboardProc _proc; // keep a reference so the delegate is not collected
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _swallowSpaceUp;

    public KeyboardHook(Action onTriggered)
    {
        _onTriggered = onTriggered;
        _proc = HookProc;
    }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "KeyboardHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Re-registers the hook (Windows silently removes hooks that time out, e.g. under heavy load).</summary>
    public void Reinstall()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmReinstall, UIntPtr.Zero, IntPtr.Zero);
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        Install();
        ready.Set();

        // Periodic safety net: re-install every 10 minutes.
        SetTimer(IntPtr.Zero, UIntPtr.Zero, 10 * 60 * 1000, IntPtr.Zero);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmReinstall || (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero))
            {
                Install();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private void Install()
    {
        var old = _hook;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        if (_hook == IntPtr.Zero) Log.Error($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == VK_SPACE && info.dwExtraInfo != OwnInputMarker)
            {
                int msg = wParam.ToInt32();
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = msg is WM_KEYUP or WM_SYSKEYUP;

                if (down)
                {
                    // Win state comes from the async key state instead of our own tracking, so a missed key-up
                    // (lock screen, UAC desktop, ...) can never leave us swallowing plain spaces.
                    bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
                    if (win && !IsDown(VK_CONTROL) && !IsDown(VK_MENU) && !IsDown(VK_SHIFT))
                    {
                        if (!_swallowSpaceUp) // ignore auto-repeat while the keys stay pressed
                        {
                            _swallowSpaceUp = true;
                            // Notify first: input injected from here is processed synchronously, so the
                            // WM_HOTKEY caused by the mask key can arrive before anything posted afterwards.
                            try { _onTriggered(); } catch (Exception ex) { Log.Error("Hotkey handler", ex); }
                            SendMaskKey();
                        }
                        return (IntPtr)1;
                    }
                }
                else if (up && _swallowSpaceUp)
                {
                    _swallowSpaceUp = false;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Releasing Win after a swallowed Space would otherwise look like a lone Win tap and open the Start menu.
    /// Tapping an unassigned key while Win is held tells the shell that Win was used as a modifier.
    /// </summary>
    private static void SendMaskKey()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki = new KEYBDINPUT { wVk = VK_MASK, dwExtraInfo = OwnInputMarker };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = OwnInputMarker };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, (uint)WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _threadId = 0;
    }
}
