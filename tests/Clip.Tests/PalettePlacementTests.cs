using Clip.Shell;

namespace Clip.Tests;

public sealed class PalettePlacementTests
{
    // The palette is 800x520 DIPs. These are the two monitors that actually caught the bug.
    private const double DipWidth = 800;
    private const double DipHeight = 520;

    [Fact]
    public void CentresOnTheTargetMonitorsOwnScale()
    {
        // The known-good line from shell.log: a 1920x1128 work area at 150% put the palette at
        // 360,174 with a 1200x780 window. Measuring the window instead of deriving it produced
        // 567,824 on the same monitor whenever the rescale had not landed yet.
        var (x, y, w, h) = MainWindow.CenteredPlacement(0, 0, 1920, 1128, DipWidth, DipHeight, 1.5, 800, 520);

        Assert.Equal(1200, w);
        Assert.Equal(780, h);
        Assert.Equal(360, x);
        Assert.Equal(174, y);
    }

    [Fact]
    public void IgnoresAStaleMeasuredSizeFromTheMonitorItIsLeaving()
    {
        // The regression in one assertion: the window is still 800x520 physical because it has not
        // been rescaled for the 150% monitor yet. The placement must not believe it.
        var stale = MainWindow.CenteredPlacement(0, 0, 1920, 1128, DipWidth, DipHeight, 1.5, 800, 520);
        var settled = MainWindow.CenteredPlacement(0, 0, 1920, 1128, DipWidth, DipHeight, 1.5, 1200, 780);

        Assert.Equal(settled, stale);
    }

    [Fact]
    public void CentresOnAMonitorWithNegativeOrigin()
    {
        // The second monitor sits above and left of the primary, so its work area origin is
        // negative — the offset has to be added, not assumed to be zero.
        var (x, y, _, _) = MainWindow.CenteredPlacement(7, -1080, 1920, 1032, DipWidth, DipHeight, 1.0, 0, 0);

        Assert.Equal(7 + (1920 - 800) / 2, x);
        Assert.Equal(-1080 + (1032 - 520) / 2, y);
    }

    [Theory]
    [InlineData(1.0, 800, 520)]
    [InlineData(1.25, 1000, 650)]
    [InlineData(1.5, 1200, 780)]
    [InlineData(2.0, 1600, 1040)]
    public void SizesFromTheDipSizeTimesTheScale(double scale, int expectedWidth, int expectedHeight)
    {
        var (_, _, w, h) = MainWindow.CenteredPlacement(0, 0, 3840, 2160, DipWidth, DipHeight, scale, 1, 1);

        Assert.Equal(expectedWidth, w);
        Assert.Equal(expectedHeight, h);
    }

    [Fact]
    public void FallsBackToTheMeasuredSizeBeforeTheFirstLayout()
    {
        // Width/Height are NaN until WPF has laid the window out once, and NaN * scale is NaN.
        var (_, _, w, h) = MainWindow.CenteredPlacement(0, 0, 1920, 1128, double.NaN, double.NaN, 1.5, 1200, 780);

        Assert.Equal(1200, w);
        Assert.Equal(780, h);
    }

    [Fact]
    public void PinsToTheCornerWhenThePaletteIsWiderThanTheWorkArea()
    {
        // Half of a negative difference would hang the palette off the left edge, where the search
        // box would be unreachable.
        var (x, y, _, _) = MainWindow.CenteredPlacement(100, 50, 400, 300, DipWidth, DipHeight, 1.5, 0, 0);

        Assert.Equal(100, x);
        Assert.Equal(50, y);
    }
}
