using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Spotlight.Core;
using static Spotlight.Native.NativeMethods;

namespace Spotlight.Services;

/// <summary>
/// Loads shell icons on a dedicated STA thread (shell extensions require it), newest request first,
/// and caches them as frozen bitmaps.
/// </summary>
internal sealed class IconLoader
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new();
    private readonly Stack<(SearchResult Result, int Size)> _pending = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly HashSet<SearchResult> _queued = new();

    public IconLoader(Dispatcher ui)
    {
        _ui = ui;
        var thread = new Thread(Loop) { IsBackground = true, Name = "IconLoader", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Icon edge length in physical pixels for the current DPI.</summary>
    public int PixelSize { get; set; } = 32;

    public void Request(SearchResult result)
    {
        if (result.Image != null || result.IconSource == null) return;
        string key = result.IconCacheKey ?? result.IconSource;
        if (_cache.TryGetValue(key + "@" + PixelSize, out var cached))
        {
            if (cached != null) result.Image = cached;
            return;
        }
        lock (_pending)
        {
            if (!_queued.Add(result)) return;
            _pending.Push((result, PixelSize));
        }
        _signal.Set();
    }

    private void Loop()
    {
        while (true)
        {
            _signal.WaitOne();
            while (true)
            {
                SearchResult result;
                int size;
                lock (_pending)
                {
                    if (_pending.Count == 0) break;
                    (result, size) = _pending.Pop();
                    _queued.Remove(result);
                }

                string key = (result.IconCacheKey ?? result.IconSource!) + "@" + size;
                if (!_cache.TryGetValue(key, out var image))
                {
                    image = Load(result.IconSource!, size, result.Category == Category.Images);
                    _cache[key] = image;
                }
                if (image != null) _ui.BeginInvoke(() => result.Image = image, DispatcherPriority.Background);
            }
        }
    }

    private static ImageSource? Load(string source, int size, bool allowThumbnail)
    {
        try
        {
            return source.StartsWith("res:", StringComparison.Ordinal)
                ? LoadResourceIcon(source[4..], size)
                : LoadShellImage(source, size, allowThumbnail);
        }
        catch (Exception ex)
        {
            Log.Error($"Icon for '{source}'", ex);
            return null;
        }
    }

    /// <summary>"%SystemRoot%\System32\shell32.dll,-24" style icon references.</summary>
    private static ImageSource? LoadResourceIcon(string reference, int size)
    {
        string expanded = Environment.ExpandEnvironmentVariables(reference.Trim().Trim('@'));
        int comma = expanded.LastIndexOf(',');
        string file = comma > 0 ? expanded[..comma].Trim('"') : expanded.Trim('"');
        int index = comma > 0 && int.TryParse(expanded[(comma + 1)..], out int i) ? i : 0;

        var icons = new IntPtr[1];
        if (PrivateExtractIcons(file, index, size, size, icons, null, 1, 0) == 0 || icons[0] == IntPtr.Zero)
            return LoadShellImage(file, size, false);
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    private static ImageSource? LoadShellImage(string parsingName, int size, bool allowThumbnail)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
            return null;
        try
        {
            int flags = allowThumbnail ? SIIGBF_RESIZETOFIT : SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32-bit DIB with (premultiplied) alpha; CreateBitmapSourceFromHBitmap would lose transparency.</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight), stride = width * 4;
        var pixels = new byte[stride * height];
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbm, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            DeleteDC(dc);
        }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }
}
