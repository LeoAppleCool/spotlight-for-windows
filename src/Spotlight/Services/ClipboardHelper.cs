using System.Runtime.InteropServices;
using System.Windows;

namespace Spotlight.Services;

internal static class ClipboardHelper
{
    /// <summary>Sets clipboard text, retrying briefly because other apps may hold the clipboard open.</summary>
    public static bool SetText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        Log.Error("Clipboard is busy, copy failed");
        return false;
    }
}
