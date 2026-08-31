using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Clip.Shell;

namespace Clip.Tests;

public sealed class PaletteBackdropTests
{
    [Theory]
    [InlineData(10, 17134, true)]   // Windows 10 1803 — first build with ACCENT_ENABLE_ACRYLICBLURBEHIND
    [InlineData(10, 19045, true)]   // Windows 10 22H2
    [InlineData(10, 26100, true)]   // Windows 11 24H2
    [InlineData(10, 17133, false)]  // one below the floor
    [InlineData(10, 16299, false)]  // Windows 10 1709 — blur-behind, but not the acrylic one
    [InlineData(6, 22621, false)]   // nonsense major below 10 never qualifies
    public void IsSupportedGatesOnWindows10Build17134(int major, int build, bool expected)
    {
        Assert.Equal(expected, PaletteBackdrop.IsSupported(new Version(major, 0, build)));
    }

    [Theory]
    // ABGR, not ARGB: alpha on top, then blue, green, and red in the LOW byte. A pure red tint has
    // to come out 0x..0000FF and a pure blue one 0x..FF0000 — swap the two and every non-grey theme
    // is tinted with the wrong colour.
    [InlineData("#FF0000", 0xA6, 0xA60000FFu)]
    [InlineData("#0000FF", 0xA6, 0xA6FF0000u)]
    [InlineData("#00FF00", 0xA6, 0xA600FF00u)]
    [InlineData("#1A1A1A", 0xA6, 0xA61A1A1Au)]  // the dark theme's Bg: grey hides the byte order
    [InlineData("#F7F7F7", 0xA6, 0xA6F7F7F7u)]  // and so does the light theme's
    [InlineData("#204080", 0x99, 0x99804020u)]
    [InlineData("not a hex", 0xA6, 0xA6000000u)]  // anything unparseable tints from black
    public void GradientColorPacksTheThemeHexAsAbgr(string hex, byte alpha, uint expected)
    {
        Assert.Equal(expected, PaletteBackdrop.GradientColor(hex, alpha));
    }

    [Fact]
    public void TintAlphaStaysInTheBandWhereBlurAndLegibilityBothSurvive()
    {
        // The accent tint is the entire sheet of glass now, so this one byte is the palette's base
        // opacity. Under 0x99 the desktop reads through hard enough to swim under 11px labels;
        // over 0xB3 the blur stops being visible at all, which is the bug this replaced.
        Assert.InRange(PaletteBackdrop.TintAlpha, (byte)0x99, (byte)0xB3);
    }

    [Theory]
    [InlineData("Bg", "#1A1A1A", "#1A1A1A")]  // the sheet is the accent tint now, not a brush
    [InlineData("Surface", "#212121", "#5C212121")]
    [InlineData("Surface2", "#272727", "#5C272727")]
    [InlineData("Surface3", "#323232", "#5C323232")]
    public void GlassHexBlendsBackgroundAndSurfaceBrushes(string key, string hex, string expected)
    {
        Assert.Equal(expected, PaletteBackdrop.GlassHex(key, hex));
    }

    [Fact]
    public void GlassHexKeepsAZoneWellShortOfOpaqueOnceItIsStackedOnTheSheet()
    {
        // The Surface family is never seen on its own: it is always painted over the acrylic tint,
        // so what the eye gets is 1-(1-aTint)*(1-aSurface). The palette read solid at CC/E6 because
        // that product was 98%. This is the guard on the whole point of the change — the composite
        // has to stay in the band where the desktop still blurs through but 13px text is comfortable.
        var sheet = PaletteBackdrop.TintAlpha / 255.0;
        var surface = int.Parse(PaletteBackdrop.GlassHex("Surface", "#212121")[1..3], NumberStyles.HexNumber) / 255.0;
        var zone = 1 - ((1 - sheet) * (1 - surface));

        Assert.InRange(sheet, 0.60, 0.70);
        Assert.InRange(zone, 0.75, 0.80);
    }

    [Fact]
    public void OpaqueDropsTheGlassAlphaAndKeepsTheColor()
    {
        var glass = (SolidColorBrush)PaletteBackdrop.Opaque(new SolidColorBrush(Color.FromArgb(0x5C, 0x21, 0x21, 0x21)));
        Assert.Equal(Color.FromRgb(0x21, 0x21, 0x21), glass.Color);
        Assert.True(glass.IsFrozen);
    }

    [Fact]
    public void OpaqueLeavesAnAlreadyOpaqueBrushAlone()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x21));
        Assert.Same(brush, PaletteBackdrop.Opaque(brush));
    }

    [Theory]
    [InlineData(800, 520, 8, 8)]
    [InlineData(800, 520, 0, 0)]     // fullscreen and expanded-image flatten the radius to 0
    [InlineData(10, 800, 8, 5)]      // never more than half the shorter side
    public void ShellClipGeometryRoundsToTheShellRadius(double width, double height, double radius, double expected)
    {
        var clip = MainWindow.ShellClipGeometry(width, height, radius);
        Assert.NotNull(clip);
        Assert.Equal(new Rect(0, 0, width, height), clip!.Rect);
        Assert.Equal(expected, clip.RadiusX);
        Assert.Equal(expected, clip.RadiusY);
    }

    [Theory]
    [InlineData(0, 520)]
    [InlineData(800, 0)]
    [InlineData(double.NaN, 520)]
    public void ShellClipGeometryRefusesToBlankTheWindowBeforeItHasASize(double width, double height)
    {
        Assert.Null(MainWindow.ShellClipGeometry(width, height, 8));
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Line")]
    [InlineData("Line2")]
    [InlineData("Muted")]
    [InlineData("Accent")]
    [InlineData("Selected")]
    [InlineData("SelectedBorder")]
    public void GlassHexLeavesTextAndChromeBrushesOpaque(string key)
    {
        Assert.Equal("#123456", PaletteBackdrop.GlassHex(key, "#123456"));
    }

    [Fact]
    public void GlassHexPassesThroughValuesThatAlreadyCarryAlpha()
    {
        Assert.Equal("#A61A1A1A", PaletteBackdrop.GlassHex("Bg", "#A61A1A1A"));
    }

    [Fact]
    public void TranslucentBackgroundDefaultsOnAndSurvivesMissingKey()
    {
        // Back on by default now that the blur is real: 1.2.4 turned it off only because the DWM
        // backdrop behind it painted a flat grey sheet. A settings.json written before the feature
        // has no TranslucentBackground key at all, so the default is what those installs get.
        var settings = JsonSerializer.Deserialize<ClipShellSettings>("{}");
        Assert.NotNull(settings);
        Assert.True(settings!.TranslucentBackground);
    }

    [Fact]
    public void ShellCornerRadiusMatchesTheXamlThatActuallyPaintsIt()
    {
        // The palette is a layered window, so nothing below the Shell border rounds it — this
        // radius is the entire silhouette, and the code that flattens it for fullscreen reads the
        // constant while the resting shape comes from the XAML literal. They are edited in
        // different files; this is what keeps them from drifting apart.
        var xaml = File.ReadAllText(RepoPath("src", "Clip.Shell", "MainWindow.xaml"));
        var shell = xaml.IndexOf("x:Name=\"Shell\"", StringComparison.Ordinal);
        Assert.True(shell >= 0, "MainWindow.xaml no longer declares a Border named Shell.");

        var match = Regex.Match(xaml[shell..], "CornerRadius=\"(?<radius>[0-9.]+)\"");
        Assert.True(match.Success, "The Shell border no longer sets a uniform CornerRadius.");
        Assert.Equal(
            MainWindow.ShellCornerRadius,
            double.Parse(match.Groups["radius"].Value, CultureInfo.InvariantCulture));
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void TranslucentBackgroundRoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(new ClipShellSettings { TranslucentBackground = false });
        var reloaded = JsonSerializer.Deserialize<ClipShellSettings>(json);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.TranslucentBackground);
    }
}
