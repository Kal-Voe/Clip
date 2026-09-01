using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The two "should this row exist at all" decisions behind the item action menu. Both matter
/// because the failure mode is a row that looks live and then does nothing.
/// </summary>
public sealed class ItemActionMenuGatingTests
{
    [Theory]
    [InlineData(ClipboardItemKind.Image, "invoice total", true, true, true)]
    [InlineData(ClipboardItemKind.Image, "invoice total", false, true, false)]  // OCR switched off
    [InlineData(ClipboardItemKind.Image, "invoice total", true, false, false)]  // no language pack
    [InlineData(ClipboardItemKind.Image, null, true, true, false)]              // not scanned yet
    [InlineData(ClipboardItemKind.Image, "", true, true, false)]                // scanned, found nothing
    [InlineData(ClipboardItemKind.Image, "   ", true, true, false)]
    [InlineData(ClipboardItemKind.Text, "invoice total", true, true, false)]
    [InlineData(ClipboardItemKind.Files, "invoice total", true, true, false)]
    public void CopyTextIsOfferedOnlyForImagesThatActuallyHaveText(
        ClipboardItemKind kind,
        string? ocrText,
        bool extractTextEnabled,
        bool ocrEngineAvailable,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.CanCopyOcrText(kind, ocrText, extractTextEnabled, ocrEngineAvailable));
    }

    [Theory]
    [InlineData("hello", "HELLO", true)]
    [InlineData("hello", "hello", false)]     // already lowercase: a no-op
    [InlineData("no links here", "", false)]  // Extract URLs found nothing
    [InlineData("", "", false)]
    public void ATransformIsOfferedOnlyWhenItChangesSomething(string source, string result, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldOfferTransform(source, result));
    }

    /// <summary>
    /// The menu now closes on any click the low-level mouse hook sees outside it, so "outside"
    /// has to mean outside the menu people can SEE. The popup window is 34 DIPs wider and taller
    /// than that, reserved for the drop shadow and fully transparent: a click there goes to the
    /// palette underneath, so counting it as inside the menu would leave a band beside every menu
    /// where clicking did nothing — the exact bug this replaces.
    /// </summary>
    [Theory]
    [InlineData(200, 200, true)]     // top-left corner of the menu
    [InlineData(300, 300, true)]     // inside
    [InlineData(377, 399, true)]     // last pixel of the menu
    [InlineData(378, 300, false)]    // the shadow gutter on the right
    [InlineData(300, 400, false)]    // the shadow gutter below
    [InlineData(199, 300, false)]    // just off the left edge
    [InlineData(600, 700, false)]    // nowhere near
    public void ClicksInTheMenusShadowGutterCountAsOutsideIt(int x, int y, bool expected)
    {
        // A 178x200 DIP menu at 100%, its window origin at 200,200.
        Assert.Equal(expected, MainWindow.PointIsOnPopupChild(200, 200, 178, 200, 1.0, 1.0, x, y));
    }

    /// <summary>
    /// The hook reports raw screen pixels, so the menu's DIP size has to be scaled by the popup
    /// window's own monitor scale — the same coordinate-space rule the palette's hit test follows.
    /// </summary>
    [Fact]
    public void TheMenuIsMeasuredInThePixelsTheHookReports()
    {
        // 178x200 DIPs on a 150% monitor spans 267x300 pixels, not 178x200.
        Assert.True(MainWindow.PointIsOnPopupChild(0, 0, 178, 200, 1.5, 1.5, 266, 299));
        Assert.False(MainWindow.PointIsOnPopupChild(0, 0, 178, 200, 1.5, 1.5, 267, 299));
        Assert.False(MainWindow.PointIsOnPopupChild(0, 0, 178, 200, 1.5, 1.5, 266, 300));
    }

    /// <summary>
    /// Ordinal, not culture-aware: a transform that only flips case must still count as a change.
    /// </summary>
    [Fact]
    public void CaseOnlyChangesStillCountAsChanges()
    {
        Assert.True(MainWindow.ShouldOfferTransform("Straße", "STRASSE"));
        Assert.True(MainWindow.ShouldOfferTransform("i", "I"));
    }
}
