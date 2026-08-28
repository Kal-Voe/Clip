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
    [InlineData(10, 22621, true)]   // Windows 11 22H2 — first build with DWMWA_SYSTEMBACKDROP_TYPE
    [InlineData(10, 26100, true)]   // Windows 11 24H2
    [InlineData(10, 22000, false)]  // Windows 11 21H2 — attribute not there yet
    [InlineData(10, 19045, false)]  // Windows 10 22H2
    [InlineData(6, 22621, false)]   // nonsense major below 10 never qualifies
    public void IsSupportedGatesOnWindows11Build22621(int major, int build, bool expected)
    {
        Assert.Equal(expected, PaletteBackdrop.IsSupported(new Version(major, 0, build)));
    }

    [Theory]
    [InlineData("Bg", "#1A1A1A", "#A61A1A1A")]
    [InlineData("Bg", "#F7F7F7", "#A6F7F7F7")]
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
        // The Surface family is never seen on its own: it is always painted over the Bg sheet, so
        // what the eye gets is 1-(1-aBg)*(1-aSurface). The palette read solid at CC/E6 because that
        // product was 98%. This is the guard on the whole point of the change — the composite has
        // to stay in the band where the desktop still blurs through but 13px text is comfortable.
        var bg = int.Parse(PaletteBackdrop.GlassHex("Bg", "#1A1A1A")[1..3], NumberStyles.HexNumber) / 255.0;
        var surface = int.Parse(PaletteBackdrop.GlassHex("Surface", "#212121")[1..3], NumberStyles.HexNumber) / 255.0;
        var zone = 1 - ((1 - bg) * (1 - surface));

        Assert.InRange(bg, 0.60, 0.70);
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
        // A settings.json written before the feature has no TranslucentBackground key at all;
        // deserializing must land on the default (on), not off.
        var settings = JsonSerializer.Deserialize<ClipShellSettings>("{}");
        Assert.NotNull(settings);
        Assert.True(settings!.TranslucentBackground);
    }

    [Fact]
    public void ShellCornerRadiusMatchesTheXamlThatActuallyPaintsIt()
    {
        // The glass backdrop makes the window background transparent, so DWM's clip and the Shell
        // border's fill are both visible and any disagreement shows as two nested arcs per corner.
        // The constant and the XAML literal are edited in different files; this is what keeps a
        // change to one from silently re-opening that bug.
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
