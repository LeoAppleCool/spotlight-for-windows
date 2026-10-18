using Microsoft.Win32;

namespace Spotlight.Services;

/// <summary>Executable of the user's default web browser (used for icons only).</summary>
internal static class DefaultBrowser
{
    private static readonly Lazy<string?> Exe = new(Find);

    public static string? ExePath => Exe.Value;

    private static string? Find()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId) return null;
            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is not string commandLine) return null;
            var (file, _) = Launcher.SplitCommandLine(Environment.ExpandEnvironmentVariables(commandLine));
            return File.Exists(file) ? file : null;
        }
        catch
        {
            return null;
        }
    }
}
