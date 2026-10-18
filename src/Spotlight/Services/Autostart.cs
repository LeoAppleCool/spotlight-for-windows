using Microsoft.Win32;

namespace Spotlight.Services;

/// <summary>Start with Windows via HKCU\...\Run (respects "Startup apps" toggles made in Task Manager/Settings).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Spotlight";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // Even first byte = enabled, odd = disabled by the user in Task Manager / Settings.
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || flags[0] % 2 == 0;
        }
        catch { return false; }
    }

    /// <summary>Called at every start: keeps the registered path pointing at this executable.</summary>
    public static void Sync(bool wanted)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!wanted)
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }
            if (run.GetValue(ValueName) as string != Command) run.SetValue(ValueName, Command);

            // Give the entry an explicit "enabled" record in Settings › Apps › Startup (the same value Settings
            // writes); a Run entry without one was observed being skipped at login. An existing record, e.g. the
            // user turned Spotlight off there, is left alone.
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (approved.GetValue(ValueName) == null) approved.SetValue(ValueName, Enabled, RegistryValueKind.Binary);
        }
        catch (Exception ex) { Log.Error("Updating autostart failed", ex); }
    }

    private static byte[] Enabled => [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Uninstall: removes the Run entry and its Startup apps record.</summary>
    public static void Remove()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Error("Removing autostart failed", ex); }
    }

    /// <summary>Explicit user choice from the tray menu, overriding a previous "disabled" in Task Manager.</summary>
    public static void Set(bool enabled)
    {
        Sync(enabled);
        if (!enabled) return;
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && flags[0] % 2 == 1)
                approved.SetValue(ValueName, Enabled, RegistryValueKind.Binary);
        }
        catch (Exception ex) { Log.Error("Re-enabling autostart failed", ex); }
    }
}
