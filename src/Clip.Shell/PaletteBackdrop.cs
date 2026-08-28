using System.Runtime.InteropServices;
// Aliased rather than `using System.Windows.Media`: System.Drawing is in scope project-wide and
// brings its own Brush and Color, so the bare names are ambiguous. Same aliases as MainWindow.
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Clip.Shell;

/// <summary>
/// The Raycast-style glass: DWM's own acrylic backdrop (DWMSBT_TRANSIENTWINDOW) drawn behind the
/// palette, with just enough alpha on the theme's background brushes for the blur to read through.
///
/// This is deliberately NOT AllowsTransparency=True. A layered window disables DWM backdrops and
/// ClearType both and adds a readback path — it is the anti-pattern that causes the exact softness
/// the backdrop is meant to replace. The window stays a normal non-layered window; the compositor
/// does the blur GPU-side, the same mechanism as the Windows 11 system flyouts.
/// </summary>
internal static class PaletteBackdrop
{
    /// <summary>
    /// DWMWA_SYSTEMBACKDROP_TYPE first shipped in the Windows 11 22H2 compositor. Older builds
    /// reject the attribute and the palette simply stays opaque.
    /// </summary>
    internal const int MinimumBuild = 22621;

    private const int DwmwaSystemBackdropType = 38;
    private const int BackdropTransientWindow = 3; // DWMSBT_TRANSIENTWINDOW: the acrylic system flyouts use
    private const int BackdropNone = 1;            // DWMSBT_NONE

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static bool IsSupported() => IsSupported(Environment.OSVersion.Version);

    internal static bool IsSupported(Version osVersion) =>
        osVersion.Major >= 10 && osVersion.Build >= MinimumBuild;

    /// <summary>
    /// Best-effort: false means "stay opaque", never a throw. Safe to call repeatedly — a reveal
    /// after a cloak cycle re-asserts the same flags.
    /// </summary>
    internal static bool TryApply(IntPtr hwnd)
    {
        try
        {
            // Sheet-of-glass frame first: the backdrop only draws where the DWM frame is.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0)
            {
                return false;
            }

            var backdrop = BackdropTransientWindow;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "acrylic backdrop apply failed");
            return false;
        }
    }

    /// <summary>Undoes <see cref="TryApply"/>. Harmless on builds that never had a backdrop.</summary>
    internal static void Remove(IntPtr hwnd)
    {
        try
        {
            var backdrop = BackdropNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            var margins = new Margins();
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "acrylic backdrop remove failed");
        }
    }

    /// <summary>
    /// The one rule the whole glass look rests on: <b>the Shell is the single sheet of glass, and
    /// everything layered on it is a relative tint, never a restatement of the base color.</b>
    ///
    /// Bg is that sheet. The Surface family is not a second sheet — it is the wash that separates
    /// the list column from the preview column from the footer, and it is always painted on top of
    /// the Bg sheet, so its alpha compounds: a zone's real opacity is 1-(1-aBg)*(1-aSurface). With
    /// the old CC/E6 pair that came out at 1-0.20*0.10 = 98%, which is why the palette read solid
    /// no matter how good the acrylic behind it was. A6 over 5C lands at 1-0.349*0.639 = 78%, and
    /// the bare sheet (header, and anything that correctly paints nothing of its own) at 65%.
    ///
    /// Anything a zone tint would make illegible gets its own stronger treatment at its own call
    /// site — see <see cref="Opaque"/> — rather than dragging these two numbers back up.
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
            "Bg" => "#A6" + hex[1..],
            "Surface" or "Surface2" or "Surface3" => "#5C" + hex[1..],
            _ => hex,
        };
    }

    /// <summary>
    /// Strips the glass alpha back off a themed brush. Only the palette has the DWM acrylic behind
    /// it; a Popup and an owned window are each their own HWND, so a zone tint there is not glass —
    /// it is a menu you can read the list rows through, or a Window.Background washed out against
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
