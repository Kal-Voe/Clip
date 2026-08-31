using Clip.Shell;

namespace Clip.Tests;

public sealed class ChromeDragTests
{
    // The palette's own layout: a 53 DIP top bar and a 34 DIP bottom bar around a 520 DIP window.
    private const double Header = 53;
    private const double Footer = 34;
    private const double Root = 520;

    private static bool Arm(double y, bool overSearch = false, int textLength = 0) =>
        MainWindow.ShouldArmChromeDrag(y, Header, Root, Footer, overSearch, textLength);

    [Theory]
    [InlineData(0)]      // the very top edge
    [InlineData(6)]      // the strip above the search field
    [InlineData(26)]     // level with the chips
    [InlineData(53)]     // the last row of the top bar
    public void TheTopBarIsAGrabHandle(double y)
    {
        Assert.True(Arm(y));
    }

    [Theory]
    [InlineData(486)]    // the first row of the bottom bar
    [InlineData(505)]    // level with the keycaps
    [InlineData(520)]    // the very bottom edge
    public void TheBottomBarIsAGrabHandle(double y)
    {
        Assert.True(Arm(y));
    }

    [Theory]
    [InlineData(54)]     // one below the top bar
    [InlineData(260)]    // the middle of the list
    [InlineData(485)]    // one above the bottom bar
    public void TheContentBetweenThemIsNot(double y)
    {
        Assert.False(Arm(y));
    }

    [Fact]
    public void PressingTheSearchFieldWithTextInItSelectsRatherThanDrags()
    {
        Assert.False(Arm(26, overSearch: true, textLength: 5));
    }

    [Fact]
    public void PressingTheEmptySearchFieldDragsBecauseThereIsNothingToSelect()
    {
        Assert.True(Arm(26, overSearch: true, textLength: 0));
    }

    [Fact]
    public void ButtonsInTheBarsStillArm()
    {
        // The whole point of arming on the tunnel pass: a chip or a footer key is inside the bar,
        // so a press on one can still turn into a drag. The press itself is never handled, so a
        // click that does not travel far enough reaches the button as normal.
        Assert.True(Arm(26));
        Assert.True(Arm(505));
    }

    [Fact]
    public void ADegenerateLayoutDoesNotMakeTheWholeWindowAGrabHandle()
    {
        // Before the first layout every ActualHeight is 0. y >= 0 - 0 would otherwise arm on any
        // press anywhere, which would eat the first click of the session.
        Assert.True(MainWindow.ShouldArmChromeDrag(0, 0, 0, 0, false, 0));
        Assert.False(MainWindow.ShouldArmChromeDrag(200, 0, 0, 0, false, 0));
    }
}
