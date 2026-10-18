using System.ComponentModel;
using System.Runtime.InteropServices;
using Spotlight.Core;
using static Spotlight.Native.NativeMethods;

namespace Spotlight.Services;

/// <summary>Opens things the way Explorer would (ShellExecuteEx on an STA thread, never blocking the UI).</summary>
internal static class Launcher
{
    public static event Action<string>? Failed;

    /// <summary>Opens a file, folder, URL or registered URI scheme (ms-settings:, mailto:, ...).</summary>
    public static void Open(string target, string? arguments = null, string? verb = null, string? workingDirectory = null) =>
        RunOnSta(() =>
        {
            var info = NewInfo();
            info.lpFile = target;
            info.lpParameters = arguments;
            info.lpVerb = verb;
            info.lpDirectory = workingDirectory;
            Execute(ref info, target);
        });

    /// <summary>Opens a shell namespace item (e.g. "shell:AppsFolder\{AUMID}") by PIDL, optionally with a verb such as "runas".</summary>
    public static void OpenShellItem(string parsingName, string? verb = null) =>
        RunOnSta(() =>
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                // Fall back to letting Explorer resolve it.
                var fallback = NewInfo();
                fallback.lpFile = "explorer.exe";
                fallback.lpParameters = $"\"{parsingName}\"";
                Execute(ref fallback, parsingName);
                return;
            }
            try
            {
                var info = NewInfo();
                info.fMask |= SEE_MASK_INVOKEIDLIST;
                info.lpIDList = pidl;
                info.lpVerb = verb;
                Execute(ref info, parsingName);
            }
            finally
            {
                ILFree(pidl);
            }
        });

    /// <summary>Runs a command line such as "%windir%\system32\rundll32.exe shell32.dll,Control_RunDLL desk.cpl".</summary>
    public static void RunCommandLine(string commandLine)
    {
        string expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var (file, args) = SplitCommandLine(expanded);
        Open(file, string.IsNullOrWhiteSpace(args) ? null : args);
    }

    /// <summary>Shows the item selected in its Explorer folder (reuses an already open window).</summary>
    public static void Reveal(string path) =>
        RunOnSta(() =>
        {
            var pidl = ILCreateFromPath(path);
            if (pidl == IntPtr.Zero)
            {
                var info = NewInfo();
                info.lpFile = "explorer.exe";
                info.lpParameters = $"/select,\"{path}\"";
                Execute(ref info, path);
                return;
            }
            try { SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0); }
            finally { ILFree(pidl); }
        });

    public static (string File, string Args) SplitCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (commandLine.StartsWith('"'))
        {
            int end = commandLine.IndexOf('"', 1);
            if (end > 0) return (commandLine[1..end], commandLine[(end + 1)..].Trim());
        }
        // Unquoted paths may contain spaces: take the longest prefix that is an existing file.
        int space = -1;
        while ((space = commandLine.IndexOf(' ', space + 1)) > 0)
        {
            string candidate = commandLine[..space];
            if (File.Exists(candidate)) return (candidate, commandLine[(space + 1)..].Trim());
        }
        return File.Exists(commandLine) ? (commandLine, "") : SplitAtFirstSpace(commandLine);
    }

    private static (string, string) SplitAtFirstSpace(string s)
    {
        int i = s.IndexOf(' ');
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..].Trim());
    }

    private static SHELLEXECUTEINFO NewInfo() => new()
    {
        cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
        fMask = SEE_MASK_NOASYNC,
        nShow = SW_SHOWNORMAL,
    };

    private static void Execute(ref SHELLEXECUTEINFO info, string label)
    {
        if (ShellExecuteEx(ref info)) return;
        int error = Marshal.GetLastWin32Error();
        if (error == 1223) return; // ERROR_CANCELLED: user declined the UAC prompt
        Log.Error($"ShellExecuteEx failed for '{label}': {new Win32Exception(error).Message}");
        Failed?.Invoke(label);
    }

    public static void RunOnSta(Action action)
    {
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Launch failed", ex); }
        })
        { IsBackground = true, Name = "Launcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
