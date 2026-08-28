using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clip.Shell;

/// <summary>
/// Resolves the icon for the app an item was copied from.
///
/// Everything goes through <c>IShellItemImageFactory</c>, which is the only shell API that
/// takes an arbitrary pixel size and returns a 32bpp image with alpha. <c>ExtractAssociatedIcon</c>
/// and <c>SHGetFileInfo</c> are capped at 32x32, so at 150% scaling they can only ever produce a
/// blurry upscale. The same call also resolves packaged (MSIX) apps through
/// <c>shell:AppsFolder\{aumid}</c>, which is how apps like Claude and Raycast get a real icon
/// instead of the ApplicationFrameHost placeholder.
/// </summary>
internal static class SourceAppIcons
{
    private const int MaxCacheEntries = 300;

    // Native icon rungs. Requesting an in-between size makes the shell scale for us; asking for
    // the next rung up and letting WPF scale down keeps edges crisp.
    private static readonly int[] NativeSizes = [16, 20, 24, 32, 40, 48, 64, 96, 256];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recent = new();

    /// <summary>
    /// Returns a frozen icon for the given identity, or null when nothing could be resolved.
    /// <paramref name="aumid"/> wins when present because packaged apps have no usable exe path.
    /// Must be called from an STA thread — shell extensions require it.
    /// </summary>
    public static ImageSource? Resolve(string? aumid, string? exePath, int logicalSize, double dpiScale)
    {
        var parsingName = ParsingNameFor(aumid, exePath);
        if (parsingName is null)
        {
            return null;
        }

        var pixelSize = NativeSizeFor(logicalSize, dpiScale);
        var key = $"{parsingName}|{pixelSize}";

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }
        }

        var resolved = TryLoad(parsingName, pixelSize);

        lock (Gate)
        {
            Cache[key] = resolved;
            Touch(key);
            Evict();
        }

        return resolved;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Recent.Clear();
        }
    }

    /// <summary>
    /// Returns a real thumbnail for a file — a frame from a video, a page from a document —
    /// rather than its type icon. Same shell call, minus the icon-only flag, which is exactly how
    /// File Explorer fills its thumbnail view.
    /// </summary>
    public static ImageSource? Thumbnail(string filePath, int logicalSize, double dpiScale)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var pixelSize = NativeSizeFor(logicalSize, dpiScale);
        var key = $"thumb|{filePath}|{pixelSize}";

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }
        }

        var resolved = TryLoad(filePath, pixelSize, thumbnail: true);

        lock (Gate)
        {
            Cache[key] = resolved;
            Touch(key);
            Evict();
        }

        return resolved;
    }

    /// <summary>
    /// Cache-only lookup. Returns false when the icon has not been resolved yet, so a caller on
    /// the UI thread can paint a placeholder instead of blocking on the shell.
    /// </summary>
    public static bool TryGetCached(string? aumid, string? exePath, int logicalSize, double dpiScale, out ImageSource? icon)
    {
        icon = null;
        var parsingName = ParsingNameFor(aumid, exePath);
        if (parsingName is null)
        {
            return false;
        }

        lock (Gate)
        {
            if (!Cache.TryGetValue($"{parsingName}|{NativeSizeFor(logicalSize, dpiScale)}", out icon))
            {
                return false;
            }

            Touch($"{parsingName}|{NativeSizeFor(logicalSize, dpiScale)}");
            return true;
        }
    }

    /// <summary>
    /// Cache-only thumbnail lookup, mirroring <see cref="TryGetCached"/>: false when the file's
    /// thumbnail has not been extracted yet, so a row being built on the UI thread can paint its
    /// type glyph instead of waiting on the shell. True with a null image means the shell was
    /// already asked and has no thumbnail to give — asking again will not change the answer.
    /// </summary>
    public static bool TryGetCachedThumbnail(string filePath, int logicalSize, double dpiScale, out ImageSource? thumbnail)
    {
        thumbnail = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var key = $"thumb|{filePath}|{NativeSizeFor(logicalSize, dpiScale)}";
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out thumbnail))
            {
                return false;
            }

            Touch(key);
            return true;
        }
    }

    /// <summary>
    /// Extracts a thumbnail off the UI thread and invokes <paramref name="onResolved"/> on the
    /// worker, the same way <see cref="ResolveAsync"/> does for icons — extraction needs the same
    /// STA shell machinery and shares its newest-first queue.
    /// </summary>
    public static void ThumbnailAsync(string filePath, int logicalSize, double dpiScale, Action<ImageSource?> onResolved)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        lock (PendingGate)
        {
            Pending.AddFirst(() => onResolved(Thumbnail(filePath, logicalSize, dpiScale)));
            EnsureWorker();
            PendingSignal.Set();
        }
    }

    /// <summary>
    /// Resolves off the UI thread and invokes <paramref name="onResolved"/> on the worker.
    /// Shell extensions require STA, so this uses a dedicated STA thread rather than the thread
    /// pool. Newest request first, so a fast scroll resolves what is on screen rather than what
    /// has already scrolled past.
    /// </summary>
    public static void ResolveAsync(string? aumid, string? exePath, int logicalSize, double dpiScale, Action<ImageSource?> onResolved)
    {
        if (ParsingNameFor(aumid, exePath) is null)
        {
            return;
        }

        lock (PendingGate)
        {
            Pending.AddFirst(() => onResolved(Resolve(aumid, exePath, logicalSize, dpiScale)));
            EnsureWorker();
            PendingSignal.Set();
        }
    }

    private static readonly object PendingGate = new();
    private static readonly LinkedList<Action> Pending = new();
    private static readonly System.Threading.AutoResetEvent PendingSignal = new(false);
    private static System.Threading.Thread? Worker;

    private static void EnsureWorker()
    {
        if (Worker is not null)
        {
            return;
        }

        Worker = new System.Threading.Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ClipSourceIcons",
        };
        Worker.SetApartmentState(System.Threading.ApartmentState.STA);
        Worker.Start();
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Action? next = null;
            lock (PendingGate)
            {
                if (Pending.First is { } first)
                {
                    next = first.Value;
                    Pending.RemoveFirst();
                }
            }

            if (next is null)
            {
                PendingSignal.WaitOne();
                continue;
            }

            try
            {
                next();
            }
            catch
            {
                // A single unresolvable icon must never take down the worker.
            }
        }
    }

    private static string? ParsingNameFor(string? aumid, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(aumid))
        {
            return $@"shell:AppsFolder\{aumid}";
        }

        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            return exePath;
        }

        return null;
    }

    private static int NativeSizeFor(int logicalSize, double dpiScale)
    {
        var wanted = (int)Math.Ceiling(logicalSize * (dpiScale <= 0 ? 1.0 : dpiScale));

        // Never ask the shell for a 16 or 20 px asset. Apps author those as separate, heavily
        // simplified bitmaps, so a detailed logo comes back as an unreadable smudge. Ask for at
        // least 32 and let WPF downscale a good image instead.
        wanted = Math.Max(wanted * 2, 32);

        foreach (var size in NativeSizes)
        {
            if (size >= wanted)
            {
                return size;
            }
        }

        return NativeSizes[^1];
    }

    private static void Touch(string key)
    {
        Recent.Remove(key);
        Recent.AddFirst(key);
    }

    private static void Evict()
    {
        while (Recent.Count > MaxCacheEntries)
        {
            var oldest = Recent.Last;
            if (oldest is null)
            {
                return;
            }

            Recent.RemoveLast();
            Cache.Remove(oldest.Value);
        }
    }

    private static ImageSource? TryLoad(string parsingName, int pixelSize, bool thumbnail = false)
    {
        object? factoryObject = null;
        var bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factoryObject);
            if (hr != 0 || factoryObject is not IShellItemImageFactory factory)
            {
                return null;
            }

            var size = new SIZE { cx = pixelSize, cy = pixelSize };
            // BIGGERSIZEOK lets the shell hand back a larger native asset that we scale down.
            // SCALEUP is deliberately not set — it would blur small icons back up.
            var flags = thumbnail ? SIIGBF.BIGGERSIZEOK : (SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK);
            hr = factory.GetImage(size, flags, out bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            return FromHBitmap(bitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (factoryObject is not null && Marshal.IsComObject(factoryObject))
            {
                Marshal.ReleaseComObject(factoryObject);
            }
        }
    }

    /// <summary>
    /// Copies a 32bpp HBITMAP into a frozen WPF bitmap, keeping the alpha channel.
    /// <c>CreateBitmapSourceFromHBitmap</c> is deliberately avoided: it yields Bgr32 and drops
    /// alpha, which renders an icon's transparent corners as opaque black.
    /// </summary>
    private static ImageSource? FromHBitmap(IntPtr bitmap)
    {
        // Read the DIB section's own bits rather than going through GetDIBits. GetDIBits does not
        // reliably carry the alpha channel across, which produces a washed-out halo instead of a
        // clean icon. The shell hands back a 32bpp top-down DIB with premultiplied alpha.
        var dibSize = Marshal.SizeOf<DIBSECTION>();
        if (GetObject(bitmap, dibSize, out DIBSECTION dib) != dibSize ||
            dib.dsBm.bmBitsPixel != 32 ||
            dib.dsBm.bmBits == IntPtr.Zero)
        {
            return null;
        }

        var width = dib.dsBm.bmWidth;
        var height = Math.Abs(dib.dsBm.bmHeight);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var sourceStride = dib.dsBm.bmWidthBytes;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        // A positive biHeight means the rows are stored bottom-up and have to be flipped.
        var bottomUp = dib.dsBmih.biHeight > 0;

        for (var row = 0; row < height; row++)
        {
            var sourceRow = bottomUp ? height - 1 - row : row;
            Marshal.Copy(dib.dsBm.bmBits + (sourceRow * sourceStride), pixels, row * stride, stride);
        }

        // Bgra32, not Pbgra32. Despite the documentation, what comes back here is straight alpha —
        // declaring it premultiplied makes every semi-transparent edge pixel render too bright,
        // which shows up as a white halo around the icon on a dark background.
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10,
        SCALEUP = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int count, out DIBSECTION info);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
