using System.Globalization;
using System.Runtime.InteropServices;
// Aliased rather than `using System.Windows.Media`: System.Drawing is in scope project-wide and
// brings its own Brush and Color, so the bare names are ambiguous. Same aliases as MainWindow.
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Clip.Shell;

/// <summary>
/// The Raycast-style glass: real acrylic blur-behind, applied with user32's
/// SetWindowCompositionAttribute and ACCENT_ENABLE_ACRYLICBLURBEHIND. This is the same call
/// tauri's window-vibrancy makes in <c>apply_acrylic</c>, which is what asyar uses on Windows.
///
/// It replaced DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW, and the replacement is not a
/// matter of taste. Measured on this machine (Windows 11 build 26200, 150% DPI) with a hard
/// RED|BLUE vertical edge painted behind the window and a scanline sampled through it:
///
///   no API call, layered window   FF0000 ... 0000FF   sharp edge, pure transparency
///   DWMSBT_TRANSIENTWINDOW        D3D3D3 ... D3D3D3   hr=0 "success", flat light grey, and not
///                                                     one trace of the red or blue behind it
///   accent acrylic, tint A61A1A1A 481313 ... 1A1486   red and blue both tinted AND blended
///                                                     across the boundary — actual blur
///
/// The DWM backdrop never samples what is behind the window on this build; it just paints a flat
/// sheet. That flat light grey IS the "the glass did not look like glass, it just looked light
/// grey" complaint. Do not reintroduce DwmSetWindowAttribute here on the theory that it is the
/// more official API — it is inert, and it was measured to be inert.
///
/// The palette window is <c>AllowsTransparency="True"</c> (layered), which this API requires.
/// The usual objection to that is ClearType, and it does not apply: the palette already sets
/// TextOptions.TextRenderingMode="Grayscale" on purpose, so there is no subpixel rendering to
/// lose. See the note on AllowsTransparency in MainWindow.xaml.
/// </summary>
internal static class PaletteBackdrop
{
    /// <summary>
    /// ACCENT_ENABLE_ACRYLICBLURBEHIND landed in Windows 10 1803. That is a far older floor than
    /// the 22621 the DWM backdrop needed — this API predates DWMSBT by four years, so the old
    /// build gate was excluding machines that can run the glass perfectly well.
    /// </summary>
    internal const int MinimumBuild = 17134;

    /// <summary>
    /// How much of the theme's background colour the acrylic tint carries. The accent tint is now
    /// the whole sheet of glass — the Shell paints nothing under it — so this single number is what
    /// the palette's base opacity is. Below ~0x99 the desktop shows through hard enough that 11px
    /// muted labels start swimming; above ~0xB3 the blur stops reading as blur. 0xA6 is 65%.
    /// </summary>
    internal const byte TintAlpha = 0xA6;

    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;

    /// <summary>Draws the accent on all four edges. 0 leaves the blur clipped to nothing useful.</summary>
    private const int AccentFlagsAllBorders = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    internal static bool IsSupported() => IsSupported(Environment.OSVersion.Version);

    internal static bool IsSupported(Version osVersion) =>
        osVersion.Major >= 10 && osVersion.Build >= MinimumBuild;

    /// <summary>
    /// Packs a theme hex into the accent policy's gradient colour, which is <b>ABGR</b> — a Win32
    /// COLORREF with the alpha in the top byte, so <b>red is the low byte</b>, not the high one.
    /// Get this backwards and the palette is tinted with the swapped complement of the theme
    /// colour, which reads as "the theme is broken" rather than "a byte order is wrong": for the
    /// dark theme's near-grey #1A1A1A the mistake is invisible, and it only shows up the first
    /// time somebody picks a background that is not on the grey diagonal.
    ///
    /// Anything that is not a plain #RRGGBB tints from black, which is the safe direction.
    /// </summary>
    internal static uint GradientColor(string hex, byte alpha)
    {
        byte r = 0, g = 0, b = 0;
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            (r, g, b) = (red, green, blue);
        }

        return ((uint)alpha << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    /// <summary>
    /// Best-effort: false means "stay opaque", never a throw. Safe to call repeatedly — a reveal
    /// after a cloak cycle re-asserts the same policy.
    /// </summary>
    internal static bool TryApply(IntPtr hwnd, uint gradientColor)
    {
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = AccentFlagsAllBorders,
                GradientColor = gradientColor,
                AnimationId = 0,
            };
            return TrySetAccentPolicy(hwnd, ref policy);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "acrylic blur apply failed");
            return false;
        }
    }

    /// <summary>Undoes <see cref="TryApply"/>. Harmless on builds that never had the blur.</summary>
    internal static void Remove(IntPtr hwnd)
    {
        try
        {
            var policy = new AccentPolicy { AccentState = AccentDisabled };
            _ = TrySetAccentPolicy(hwnd, ref policy);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "acrylic blur remove failed");
        }
    }

    /// <summary>
    /// Clips the window — and, the whole point, the acrylic blur — to a rounded rectangle.
    ///
    /// The blur is painted behind the layered surface across the entire window rectangle; it is not
    /// masked by what WPF actually drew. The spike saw the tint in places where WPF had painted
    /// nothing at all. So the Shell border's rounded corners cut the fill, and four squared-off
    /// wedges of tinted blur are left outside the arc. A window region is the only thing that clips
    /// the compositor's own paint, and it is how Windows 10 acrylic apps got rounded corners.
    ///
    /// The cost is that a region has no antialiasing: the arc is a hard staircase instead of the
    /// soft one WPF draws. At this radius and 150% DPI that is a pixel or two, and it beats four
    /// grey wedges. <paramref name="radiusPx"/> of 0 (fullscreen, expanded image) drops the region
    /// entirely rather than clipping a full-screen video's corners off.
    ///
    /// Best-effort like everything else here: a failure just leaves the window unclipped.
    /// </summary>
    internal static void ClipToRoundedRect(IntPtr hwnd, int radiusPx)
    {
        try
        {
            if (radiusPx <= 0 || !GetWindowRect(hwnd, out var rect))
            {
                ClearClip(hwnd);
                return;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // CreateRoundRectRgn takes the corner ELLIPSE size, which is twice the radius. The
            // region is in window coordinates, so it always starts at 0,0 whatever Left/Top are —
            // which is why the palette being parked off screen does not disturb it.
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radiusPx * 2, radiusPx * 2);
            if (region == IntPtr.Zero)
            {
                return;
            }

            // On success the window owns the region; deleting it here would free it out from under
            // the compositor. Only a failed call leaves it ours to clean up.
            if (SetWindowRgn(hwnd, region, true) == 0)
            {
                _ = DeleteObject(region);
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "rounded window region failed");
        }
    }

    /// <summary>Drops the region, so WPF's own per-pixel alpha is the only thing shaping the window.</summary>
    internal static void ClearClip(IntPtr hwnd)
    {
        try
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "rounded window region clear failed");
        }
    }

    private static bool TrySetAccentPolicy(IntPtr hwnd, ref AccentPolicy policy)
    {
        // The attribute is passed by pointer-and-size rather than by ref, so the struct has to be
        // marshalled to unmanaged memory for the duration of the one call.
        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The one rule the whole glass look rests on: <b>the acrylic tint is the single sheet of
    /// glass, and everything layered on it is a relative tint, never a restatement of the base
    /// colour.</b>
    ///
    /// The sheet is no longer a brush at all — it is <see cref="TintAlpha"/> inside the accent
    /// policy, painted by the compositor underneath everything WPF draws. So Bg stays fully opaque
    /// here (it is also the Toast's text colour, which must never go see-through) and the Shell
    /// simply paints nothing when the glass is on.
    ///
    /// The Surface family is not a second sheet either — it is the wash that separates the list
    /// column from the preview column from the footer, and it sits on top of the tint, so its alpha
    /// compounds: a zone's real opacity is 1-(1-aTint)*(1-aSurface). With the old CC/E6 pair that
    /// came out at 98%, which is why the palette read solid no matter what was behind it. 5C over
    /// the A6 tint lands at 1-0.349*0.639 = 78%, and the bare sheet (header, and anything that
    /// correctly paints nothing of its own) at 65%.
    ///
    /// Anything a zone tint would make illegible gets its own stronger treatment at its own call
    /// site — see <see cref="Opaque"/> — rather than dragging these numbers back up.
    ///
    /// Lines, text and selection chrome stay fully opaque. Anything that is not a plain #RRGGBB
    /// (already carries alpha, named color) passes through untouched.
    /// </summary>
    internal static string GlassHex(string key, string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            return hex;
        }

        return key switch
        {
            "Surface" or "Surface2" or "Surface3" => "#5C" + hex[1..],
            _ => hex,
        };
    }

    /// <summary>
    /// Strips the glass alpha back off a themed brush. Only the palette has the acrylic behind it;
    /// a Popup and an owned window are each their own HWND, so a zone tint there is not glass — it
    /// is a menu you can read the list rows through, or a Window.Background washed out against
    /// whatever the compositor happens to clear the frame to. Those surfaces opt out here.
    /// </summary>
    internal static WpfBrush Opaque(WpfBrush brush)
    {
        if (brush is not WpfSolidColorBrush solid || solid.Color.A == 255)
        {
            return brush;
        }

        var opaque = new WpfSolidColorBrush(WpfColor.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B));
        opaque.Freeze();
        return opaque;
    }
}
