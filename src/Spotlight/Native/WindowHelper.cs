using System.Text;
using static Spotlight.Native.NativeMethods;

namespace Spotlight.Native;

internal static class WindowHelper
{
    /// <summary>
    /// Frosted glass like macOS: a live blur of what is behind the window, tinted lightly by the WPF content,
    /// with rounded corners (Windows 11). Returns false when the user turned off transparency effects or the
    /// blur is unavailable — the caller then paints a solid background.
    /// </summary>
    public static bool ApplyGlass(IntPtr hwnd, bool dark, bool transparencyEnabled)
    {
        int darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // The glass edge is drawn by the window itself (a light gradient), not the system's 1px border.
        int border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // No system acrylic: its fixed tint is too opaque for the glass look.
        int backdrop = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        return SetBlur(hwnd, transparencyEnabled) && transparencyEnabled;
    }

    private static bool SetBlur(IntPtr hwnd, bool enabled)
    {
        var accent = new ACCENT_POLICY { AccentState = enabled ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED };
        int size = System.Runtime.InteropServices.Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Keeps the window out of Alt+Tab and the taskbar.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Monitor the user is working on: the one with the foreground window, else the one under the cursor.</summary>
    public static (RECT work, double scale) GetActiveMonitor(IntPtr ownWindow)
    {
        IntPtr monitor = IntPtr.Zero;
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != ownWindow && !IsShellWindow(fg))
            monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            GetCursorPos(out var pt);
            monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return (info.rcWork, scale);
    }

    // Desktop and taskbar belong to every monitor; use the cursor position for them instead.
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// A background process may not normally take the focus. An (empty) input event sent by this process lifts
    /// the foreground lock; attaching to the foreground thread's input queue is only the last resort because it
    /// can leave the activation state of both windows inconsistent.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd || SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        var input = new INPUT[1];
        input[0].type = 0; // INPUT_MOUSE without movement or buttons
        SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        Log.Info("Taking the foreground via AttachThreadInput");
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint ownThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != ownThread && AttachThreadInput(ownThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ownThread, fgThread, false);
        }
        return GetForegroundWindow() == hwnd;
    }
}
