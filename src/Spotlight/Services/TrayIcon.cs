using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using Spotlight.Core;

namespace Spotlight.Services;

/// <summary>Notification-area icon: shows that Spotlight is running and offers autostart / quit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostart;

    public TrayIcon(Action open, Action<bool> setAutostart, Action reindex, Action clearHistory, Action quit)
    {
        bool dark = Theme.AppsDark;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new MenuColors(dark)), ShowImageMargin = false };
        menu.ForeColor = dark ? Color.White : Color.Black;

        menu.Items.Add(Loc.Get("Tray.Open"), null, (_, _) => open());
        _autostart = new ToolStripMenuItem(Loc.Get("Tray.Autostart")) { CheckOnClick = true };
        _autostart.Click += (_, _) => setAutostart(_autostart.Checked);
        menu.Items.Add(_autostart);
        menu.Items.Add(Loc.Get("Tray.Reindex"), null, (_, _) => reindex());
        menu.Items.Add(Loc.Get("Tray.ClearHistory"), null, (_, _) => clearHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.Get("Tray.Quit"), null, (_, _) => quit());
        menu.Opening += (_, _) => _autostart.Checked = Autostart.IsEnabled();

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(Theme.TaskbarDark),
            Text = Truncate(Loc.Get("Tray.Tooltip"), 127),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
    }

    public void ShowBalloon(string text) => _icon.ShowBalloonTip(5000, "Spotlight", text, ToolTipIcon.None);

    /// <summary>Monochrome magnifier glyph matching the taskbar colour, rendered at the system icon size.</summary>
    private static Icon CreateIcon(bool darkTaskbar)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var font = new Font("Segoe Fluent Icons", size * 0.8f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(darkTaskbar ? Color.White : Color.Black);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("", font, brush, new RectangleF(0, 0, size, size + 1), format);
        }
        IntPtr handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Native.NativeMethods.DestroyIcon(handle);
        return icon;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class MenuColors(bool dark) : ProfessionalColorTable
    {
        private Color Back => dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        private Color Hover => dark ? Color.FromArgb(0x3D, 0x3D, 0x3D) : Color.FromArgb(0xE5, 0xE5, 0xE5);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xD0, 0xD0, 0xD0);
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xD0, 0xD0, 0xD0);
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
