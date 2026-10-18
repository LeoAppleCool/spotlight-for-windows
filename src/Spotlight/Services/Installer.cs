using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using Spotlight.Core;

namespace Spotlight.Services;

/// <summary>
/// Makes the downloaded exe self-installing: started from anywhere (e.g. Downloads) it copies itself to
/// %LOCALAPPDATA%\Programs\Spotlight, adds a Start menu shortcut and an entry under Settings › Apps (for
/// uninstalling), and continues from there. No admin rights needed.
/// </summary>
internal static class Installer
{
    public const string RepositoryUrl = "https://github.com/leoapplecool/spotlight-for-windows";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Spotlight";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Spotlight");

    public static string InstalledExe => Path.Combine(InstallDir, "Spotlight.exe");

    private static string CurrentExe => Environment.ProcessPath ?? "";

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Spotlight.lnk");

    public static bool IsInstalledCopy => string.Equals(CurrentExe, InstalledExe, StringComparison.OrdinalIgnoreCase);

    // Builds started from the source tree run in place.
    private static bool IsDevelopmentBuild =>
        CurrentExe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || CurrentExe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Installs this exe if it runs from elsewhere. True: the installed copy was started, exit now.</summary>
    public static bool InstallIfNeeded(Action quitRunningInstance)
    {
        if (IsInstalledCopy || IsDevelopmentBuild || CurrentExe.Length == 0) return false;
        try
        {
            if (!SameFile(CurrentExe, InstalledExe))
            {
                quitRunningInstance(); // an older running copy must exit before its exe can be replaced
                Directory.CreateDirectory(InstallDir);
                CopyWithRetry(CurrentExe, InstalledExe);
                // The download carries the "from the internet" mark; the user already confirmed it when starting
                // it, so the installed copy doesn't ask again at every login.
                DeleteFile(InstalledExe + ":Zone.Identifier");
            }
            EnsureRegistered();
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Installing failed; running from the download location", ex);
            return false;
        }
    }

    /// <summary>Start menu shortcut and Settings › Apps entry; repaired at every start of the installed copy.</summary>
    public static void EnsureRegistered()
    {
        try
        {
            if (!File.Exists(ShortcutPath)) CreateShortcut();
        }
        catch (Exception ex) { Log.Error("Creating the Start menu shortcut failed", ex); }
        try { RegisterUninstallEntry(); }
        catch (Exception ex) { Log.Error("Registering the uninstall entry failed", ex); }
    }

    /// <summary>"--uninstall" (Settings › Apps): asks, then removes everything Spotlight created.</summary>
    public static void Uninstall(Action quitRunningInstance)
    {
        var answer = MessageBox.Show(Loc.Get("Uninstall.Confirm"), "Spotlight", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        quitRunningInstance();
        Autostart.Remove();
        Try(() => File.Delete(ShortcutPath));
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        Try(() => Directory.Delete(AppPaths.DataDir, recursive: true));
        // The program folder contains this very exe, so it is removed a moment after this process has ended.
        if (Directory.Exists(InstallDir))
        {
            Try(() => Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            }));
        }
        MessageBox.Show(Loc.Get("Uninstall.Done"), "Spotlight", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell missing");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.Description = "Spotlight (Win+Space)";
            link.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void RegisterUninstallEntry()
    {
        if (!File.Exists(InstalledExe)) return;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Spotlight");
        key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        key.SetValue("Publisher", "leoapplecool");
        key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("URLInfoAbout", RepositoryUrl);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    private static bool SameFile(string a, string b)
    {
        if (!File.Exists(b)) return false;
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250); // the old copy may still be shutting down
            }
        }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* best effort */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFile(string path);
}
