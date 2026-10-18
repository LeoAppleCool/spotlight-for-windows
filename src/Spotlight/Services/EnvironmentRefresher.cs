using System.Collections;

namespace Spotlight.Services;

/// <summary>
/// Spotlight runs for days, but programs it starts inherit its environment. When the user edits environment
/// variables (e.g. PATH), Windows broadcasts WM_SETTINGCHANGE "Environment" and we reload them like Explorer does.
/// </summary>
internal static class EnvironmentRefresher
{
    public static void Refresh()
    {
        try
        {
            var machine = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine);
            var user = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User);

            foreach (DictionaryEntry e in machine)
                Environment.SetEnvironmentVariable((string)e.Key, e.Value as string);

            foreach (DictionaryEntry e in user)
            {
                string name = (string)e.Key;
                string? value = e.Value as string;
                if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase) && machine["Path"] is string machinePath)
                    value = machinePath.TrimEnd(';') + ";" + value;
                Environment.SetEnvironmentVariable(name, value);
            }
            Log.Info("Environment variables reloaded");
        }
        catch (Exception ex)
        {
            Log.Error("Reloading environment failed", ex);
        }
    }
}
