using System.Text.Json;
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
    [InlineData("Bg", "#1A1A1A", "#CC1A1A1A")]
    [InlineData("Bg", "#F7F7F7", "#CCF7F7F7")]
    [InlineData("Surface", "#212121", "#E6212121")]
    [InlineData("Surface2", "#272727", "#E6272727")]
    [InlineData("Surface3", "#323232", "#E6323232")]
    public void GlassHexBlendsBackgroundAndSurfaceBrushes(string key, string hex, string expected)
    {
        Assert.Equal(expected, PaletteBackdrop.GlassHex(key, hex));
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
        Assert.Equal("#CC1A1A1A", PaletteBackdrop.GlassHex("Bg", "#CC1A1A1A"));
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
    public void TranslucentBackgroundRoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(new ClipShellSettings { TranslucentBackground = false });
        var reloaded = JsonSerializer.Deserialize<ClipShellSettings>(json);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.TranslucentBackground);
    }
}
