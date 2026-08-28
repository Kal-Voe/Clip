using System.Runtime.InteropServices;

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
    /// Alpha per brush while glass is on. Bg is the big sheet the blur reads through; the Surface
    /// brushes back the panels text actually sits on, so they stay closer to opaque — ClearType
    /// cannot do subpixel over true transparency, and greyscale text needs the contrast. Lines,
    /// text and selection chrome stay fully opaque. Anything that is not a plain #RRGGBB (already
    /// carries alpha, named color) passes through untouched.
    /// </summary>
    internal static string GlassHex(string key, string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            return hex;
        }

        return key switch
        {
            "Bg" => "#CC" + hex[1..],
            "Surface" or "Surface2" or "Surface3" => "#E6" + hex[1..],
            _ => hex,
        };
    }
}
