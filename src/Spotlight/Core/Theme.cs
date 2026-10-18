using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Spotlight.Core;

/// <summary>
/// Follows the Windows light/dark mode, accent colour and "Transparency effects" setting, and exposes the
/// frosted-glass palette as WPF brush resources.
/// </summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsDark => ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
    public static bool TaskbarDark => ReadDword(PersonalizeKey, "SystemUsesLightTheme", 1) == 0;
    public static bool TransparencyEnabled => ReadDword(PersonalizeKey, "EnableTransparency", 1) != 0;

    public static void Apply(ResourceDictionary res)
    {
        bool dark = AppsDark;
        var accent = AccentColor(dark);

        // Glass: only a light tint over the blurred background, darker towards the bottom, plus a bright
        // edge that fades downwards and a faint sheen at the top — the look of macOS materials.
        Set(res, "Brush.Tint", dark
            ? Vertical(Color.FromArgb(0x5C, 0x34, 0x34, 0x3A), Color.FromArgb(0x70, 0x1C, 0x1C, 0x21))
            : Vertical(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xA8, 0xF2, 0xF2, 0xF5)));
        Set(res, "Brush.Edge", dark
            ? Vertical(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
            : Vertical(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x24, 0x00, 0x00, 0x00)));
        Set(res, "Brush.Sheen", Vertical(Color.FromArgb(dark ? (byte)0x14 : (byte)0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)));
        Set(res, "Brush.Solid", dark ? Rgb(0x26, 0x26, 0x2A) : Rgb(0xF4, 0xF4, 0xF6));

        Set(res, "Brush.Text", dark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1D, 0x1D, 0x1F));
        Set(res, "Brush.SubText", dark ? Argb(0xB3, 0xFF, 0xFF, 0xFF) : Argb(0xA6, 0x00, 0x00, 0x00));
        Set(res, "Brush.Header", dark ? Argb(0x94, 0xFF, 0xFF, 0xFF) : Argb(0x8C, 0x00, 0x00, 0x00));
        Set(res, "Brush.Placeholder", dark ? Argb(0x80, 0xFF, 0xFF, 0xFF) : Argb(0x73, 0x00, 0x00, 0x00));
        Set(res, "Brush.Separator", dark ? Argb(0x1F, 0xFF, 0xFF, 0xFF) : Argb(0x17, 0x00, 0x00, 0x00));
        Set(res, "Brush.Hover", dark ? Argb(0x14, 0xFF, 0xFF, 0xFF) : Argb(0x12, 0x00, 0x00, 0x00));
        Set(res, "Brush.IconTile", dark ? Argb(0x24, 0xFF, 0xFF, 0xFF) : Argb(0x6B, 0xFF, 0xFF, 0xFF));

        // Selection is a tinted pane of glass in the accent colour; the text keeps its normal colour.
        Set(res, "Brush.Selection", new SolidColorBrush(Color.FromArgb(dark ? (byte)0x8C : (byte)0x59, accent.R, accent.G, accent.B)));
        Set(res, "Brush.SelectionText", dark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1D, 0x1D, 0x1F));
        Set(res, "Brush.SelectionSubText", dark ? Argb(0xCC, 0xFF, 0xFF, 0xFF) : Argb(0xB3, 0x00, 0x00, 0x00));
        Set(res, "Brush.KeyCap", dark ? Argb(0x2E, 0xFF, 0xFF, 0xFF) : Argb(0x8C, 0xFF, 0xFF, 0xFF));

        Set(res, "Brush.Caret", dark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1D, 0x1D, 0x1F));
        Set(res, "Brush.TextSelection", new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)));
        Set(res, "Brush.ScrollThumb", dark ? Argb(0x59, 0xFF, 0xFF, 0xFF) : Argb(0x47, 0x00, 0x00, 0x00));
    }

    /// <summary>The accent colour, nudged so it stands out from the glass (gray accents are common).</summary>
    private static Color AccentColor(bool dark)
    {
        Color[] palette = ReadAccentPalette();
        Color c = palette.Length >= 7 ? palette[3] : ReadDwmAccent() ?? Color.FromRgb(0x00, 0x67, 0xC0);

        double lum = Luminance(c);
        if (dark && lum < 0.09 && palette.Length >= 7) c = palette[2];        // too close to the dark glass
        else if (!dark && lum > 0.45 && palette.Length >= 7) c = palette[4];  // too close to the light glass
        return c;
    }

    private static Color[] ReadAccentPalette()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] bytes && bytes.Length >= 32)
            {
                var colors = new Color[8];
                for (int i = 0; i < 8; i++)
                    colors[i] = Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
                return colors;
            }
        }
        catch { }
        return [];
    }

    private static Color? ReadDwmAccent()
    {
        int v = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", -1);
        if (v == -1) return null;
        return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF)); // stored as ABGR
    }

    private static double Luminance(Color c) =>
        0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

    private static double Lin(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static int ReadDword(string keyPath, string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    private static void Set(ResourceDictionary res, string key, Brush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        res[key] = brush;
    }

    private static LinearGradientBrush Vertical(Color top, Color bottom) => new(top, bottom, new Point(0, 0), new Point(0, 1));
    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static SolidColorBrush Argb(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
}
