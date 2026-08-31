using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class RowDragTests
{
    // The Windows defaults, which is what SystemParameters reports on the machines Clip runs on.
    private const double MinX = 4;
    private const double MinY = 4;

    private static bool Start(double x, double y) =>
        MainWindow.ShouldStartRowDrag(x, y, MinX, MinY);

    [Theory]
    [InlineData(0, 0)]      // a click that never moved
    [InlineData(3, 0)]      // a twitch sideways
    [InlineData(0, -3)]     // a twitch upward
    [InlineData(-3, 3)]     // a wobble in both axes, neither far enough
    public void AClickThatBarelyMovesIsStillAClick(double x, double y)
    {
        // This is the half that must not regress: below the threshold nothing happens, so the
        // press stays a selection and a second one inside the double-click time still pastes.
        Assert.False(Start(x, y));
    }

    [Theory]
    [InlineData(4, 0)]      // exactly the horizontal threshold
    [InlineData(0, 4)]      // exactly the vertical threshold
    [InlineData(-40, 0)]    // pulled left, out of the palette
    [InlineData(0, 200)]    // pulled straight down the list
    [InlineData(30, 30)]    // away at an angle
    public void TravellingPastTheThresholdInEitherAxisStartsTheDrag(double x, double y)
    {
        Assert.True(Start(x, y));
    }

    [Fact]
    public void EitherAxisIsEnoughOnItsOwn()
    {
        // Horizontal alone has to count, or dragging sideways out of a narrow palette would need
        // a pointless vertical detour first.
        Assert.True(Start(10, 0));
        Assert.True(Start(0, 10));
    }
}

public sealed class ClipboardDragDataTests
{
    private static ClipboardHistoryItem Text(string text, string? html = null, string? rtf = null) => new()
    {
        Kind = ClipboardItemKind.Text,
        Text = text,
        Preview = text,
        HtmlText = html,
        RtfText = rtf,
        HasOriginalFormatting = html is not null || rtf is not null,
    };

    [Fact]
    public void TextDragsAsText()
    {
        var payload = ClipboardDragData.Create(Text("hello"), PasteFormatPreference.PlainText);

        Assert.Equal("hello", payload.Text?.Text);
        Assert.Empty(payload.FilePaths);
        Assert.Null(payload.BitmapPath);
        Assert.False(payload.IsEmpty);
    }

    [Fact]
    public void ALinkDragsAsTextToo()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Link,
            Text = "https://example.com",
            Preview = "https://example.com",
        };

        Assert.Equal("https://example.com", ClipboardDragData.Create(item, PasteFormatPreference.PlainText).Text?.Text);
    }

    [Fact]
    public void PlainTextIsThePreferenceSoNoRichFormatsRideAlong()
    {
        var payload = ClipboardDragData.Create(
            Text("hello", html: "Version:0.9\r\n<b>hello</b>", rtf: @"{\rtf1 hello}"),
            PasteFormatPreference.PlainText);

        // The point of running through ClipboardPasteData: a drag and a paste of the same row
        // must agree on plain-versus-rich, and the plain preference drops both rich formats.
        Assert.Null(payload.Text?.Html);
        Assert.Null(payload.Text?.Rtf);
    }

    [Fact]
    public void TheOriginalFormattingPreferenceCarriesTheRichFormats()
    {
        var payload = ClipboardDragData.Create(
            Text("hello", html: "Version:0.9\r\n<b>hello</b>", rtf: @"{\rtf1 hello}"),
            PasteFormatPreference.OriginalFormatting);

        Assert.Equal("hello", payload.Text?.Text);
        Assert.Equal("Version:0.9\r\n<b>hello</b>", payload.Text?.Html);
        Assert.Equal(@"{\rtf1 hello}", payload.Text?.Rtf);
    }

    [Fact]
    public void FilesDragAsPathsAndAsText()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [@"C:\a.txt", @"C:\b.txt"],
        };

        var payload = ClipboardDragData.Create(item, PasteFormatPreference.PlainText);

        // FileDrop is what Explorer and file pickers take; the text is the fallback that stops a
        // plain field refusing the drag outright.
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], payload.FilePaths);
        Assert.Equal(@"C:\a.txt" + Environment.NewLine + @"C:\b.txt", payload.Text?.Text);
        Assert.Null(payload.BitmapPath);
    }

    [Fact]
    public void AnImageDragsAsPixelsAndAsAFile()
    {
        var item = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = @"C:\assets\shot.png",
        };

        var payload = ClipboardDragData.Create(item, PasteFormatPreference.PlainText);

        Assert.Equal(@"C:\assets\shot.png", payload.BitmapPath);
        // The file half is what makes an image droppable into apps that only accept files.
        Assert.Equal([@"C:\assets\shot.png"], payload.FilePaths);
        // No text: a text box that took the drag would insert the asset's path, which is never
        // what dragging a screenshot was meant to mean.
        Assert.Null(payload.Text);
    }

    [Fact]
    public void AnImageWithNoAssetOnDiskHasNothingToDrag()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, AssetPath = null };

        Assert.True(ClipboardDragData.Create(item, PasteFormatPreference.PlainText).IsEmpty);
    }

    [Fact]
    public void AFilesItemWithNoPathsHasNothingToDrag()
    {
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Files };

        Assert.True(ClipboardDragData.Create(item, PasteFormatPreference.PlainText).IsEmpty);
    }

    [Fact]
    public void EmptyTextHasNothingToDrag()
    {
        // An empty data object gives every target the no-drop cursor, which reads as a bug. The
        // caller uses IsEmpty to not start the drag at all.
        Assert.True(ClipboardDragData.Create(Text(string.Empty), PasteFormatPreference.PlainText).IsEmpty);
    }

    [Fact]
    public void ADragNeverOffersTheStoredPathsAsSomethingToMove()
    {
        // The payload is a description of what to hand a target; it holds no mutation of its own.
        // Copying the list is what keeps a target that edits its FileDrop array from reaching
        // back into the stored item.
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Files, FilePaths = [@"C:\a.txt"] };

        var payload = ClipboardDragData.Create(item, PasteFormatPreference.PlainText);

        Assert.NotSame(item.FilePaths, payload.FilePaths);
    }
}
