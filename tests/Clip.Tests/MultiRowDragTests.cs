using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The selection rules behind Ctrl+click and Shift+click. Pure, so the arguing happens here
/// rather than against a running palette: which rows a press leaves selected, where the anchor
/// lands, which row the preview follows, and the one press that defers its own collapse so a
/// multi-selection can be dragged at all.
/// </summary>
public sealed class PaletteMultiSelectionTests
{
    private static readonly string[] Order = ["a", "b", "c", "d", "e"];

    private static PaletteMultiSelection.Result Press(
        string[] selected,
        string? anchor,
        string? preview,
        string pressed,
        bool ctrl = false,
        bool shift = false) =>
        PaletteMultiSelection.Press(Order, selected, anchor, preview, pressed, ctrl, shift);

    [Fact]
    public void APlainPressSelectsThatRowAlone()
    {
        var result = Press(["a", "b", "c"], anchor: "a", preview: "a", pressed: "e");

        Assert.Equal(["e"], result.Ids);
        Assert.Equal("e", result.AnchorId);
        Assert.Equal("e", result.PreviewId);
        Assert.False(result.CollapseOnRelease);
    }

    [Fact]
    public void CtrlAddsARowAndMovesThePreviewToIt()
    {
        var result = Press(["b"], anchor: "b", preview: "b", pressed: "d", ctrl: true);

        Assert.Equal(["b", "d"], result.Ids);
        Assert.Equal("d", result.AnchorId);
        Assert.Equal("d", result.PreviewId);
    }

    [Fact]
    public void CtrlRemovesARowThatIsNotTheOneBeingPreviewed()
    {
        var result = Press(["b", "c", "d"], anchor: "b", preview: "d", pressed: "c", ctrl: true);

        Assert.Equal(["b", "d"], result.Ids);
        // The preview stays where it was: the row it is showing is still selected.
        Assert.Equal("d", result.PreviewId);
    }

    [Fact]
    public void CtrlCannotDeselectTheRowBeingPreviewed()
    {
        // Everything downstream follows one item, so a set that does not contain the previewed
        // row would leave the pane showing something the list says is not selected.
        var result = Press(["b", "c", "d"], anchor: "b", preview: "c", pressed: "c", ctrl: true);

        Assert.Equal(["b", "c", "d"], result.Ids);
        Assert.Equal("c", result.PreviewId);
    }

    [Fact]
    public void ShiftExtendsARangeFromTheAnchor()
    {
        var result = Press(["b"], anchor: "b", preview: "b", pressed: "d", shift: true);

        Assert.Equal(["b", "c", "d"], result.Ids);
        // The anchor stays put, so a second Shift+click re-measures from the same end.
        Assert.Equal("b", result.AnchorId);
        Assert.Equal("d", result.PreviewId);
    }

    [Fact]
    public void ShiftExtendsUpwardsTooAndStillReportsOnScreenOrder()
    {
        var result = Press(["d"], anchor: "d", preview: "d", pressed: "b", shift: true);

        Assert.Equal(["b", "c", "d"], result.Ids);
        Assert.Equal("d", result.AnchorId);
        Assert.Equal("b", result.PreviewId);
    }

    [Fact]
    public void ASecondShiftClickReplacesTheRangeRatherThanGrowingIt()
    {
        var first = Press(["b"], anchor: "b", preview: "b", pressed: "e", shift: true);
        var second = PaletteMultiSelection.Press(
            Order, first.Ids, first.AnchorId, first.PreviewId, "c", ctrl: false, shift: true);

        Assert.Equal(["b", "c"], second.Ids);
    }

    [Fact]
    public void ShiftWithNoAnchorIsJustAPress()
    {
        var result = Press(["b"], anchor: null, preview: "b", pressed: "d", shift: true);

        Assert.Equal(["d"], result.Ids);
    }

    [Fact]
    public void APlainPressOnAMemberOfTheSelectionKeepsItAndDefersTheCollapse()
    {
        // The press that starts a drag must not throw the selection away before the drag begins,
        // so the collapse waits for a release that turns out to be a click.
        var result = Press(["b", "c", "d"], anchor: "b", preview: "d", pressed: "c");

        Assert.Equal(["b", "c", "d"], result.Ids);
        Assert.True(result.CollapseOnRelease);
        Assert.Equal("c", result.PreviewId);
    }

    [Fact]
    public void APressOnASingleSelectedRowHasNothingToCollapse()
    {
        var result = Press(["c"], anchor: "c", preview: "c", pressed: "c");

        Assert.Equal(["c"], result.Ids);
        Assert.False(result.CollapseOnRelease);
    }

    [Fact]
    public void RowsTheListNoLongerShowsAreDroppedFromTheSelection()
    {
        // A search that narrows the list must not leave a drag carrying rows the user cannot see.
        var result = Press(["b", "gone"], anchor: "b", preview: "b", pressed: "c", ctrl: true);

        Assert.Equal(["b", "c"], result.Ids);
    }

    [Fact]
    public void TheSetComesBackInOnScreenOrderWhateverOrderItWasClickedIn()
    {
        var result = Press(["e", "a"], anchor: "a", preview: "a", pressed: "c", ctrl: true);

        Assert.Equal(["a", "c", "e"], result.Ids);
    }
}

/// <summary>
/// What a drag of several rows actually hands the target. One data object carries the lot, so the
/// question is which formats survive the fold.
/// </summary>
public sealed class MultiItemDragDataTests
{
    private static ClipboardHistoryItem Text(string text, string? html = null) => new()
    {
        Kind = ClipboardItemKind.Text,
        Text = text,
        Preview = text,
        HtmlText = html,
        HasOriginalFormatting = html is not null,
    };

    private static ClipboardHistoryItem Image(string path) => new()
    {
        Kind = ClipboardItemKind.Image,
        AssetPath = path,
    };

    private static ClipboardDragPayload Many(params ClipboardHistoryItem[] items) =>
        ClipboardDragData.CreateMany(items, PasteFormatPreference.PlainText);

    [Fact]
    public void SeveralImagesBecomeOneFileDropOfEveryPath()
    {
        var payload = Many(Image(@"C:\assets\one.png"), Image(@"C:\assets\two.png"));

        Assert.Equal([@"C:\assets\one.png", @"C:\assets\two.png"], payload.FilePaths);
        // CF_BITMAP holds one image, and picking which of the two it should be would be a guess.
        Assert.Null(payload.BitmapPath);
        Assert.Null(payload.Text);
    }

    [Fact]
    public void SeveralTextClipsBecomeOneStringJoinedByNewlines()
    {
        var payload = Many(Text("one"), Text("two"), Text("three"));

        Assert.Equal("one" + Environment.NewLine + "two" + Environment.NewLine + "three", payload.Text?.Text);
        Assert.Empty(payload.FilePaths);
    }

    [Fact]
    public void AMultiTextDragIsPlainEvenWhenTheClipsAreRich()
    {
        var payload = ClipboardDragData.CreateMany(
            [Text("one", html: "Version:0.9\r\n<b>one</b>"), Text("two", html: "Version:0.9\r\n<b>two</b>")],
            PasteFormatPreference.OriginalFormatting);

        // Two HTML documents glued together are a third that is neither, so neither travels.
        Assert.Null(payload.Text?.Html);
        Assert.Null(payload.Text?.Rtf);
        Assert.Equal("one" + Environment.NewLine + "two", payload.Text?.Text);
    }

    [Fact]
    public void AMixedSelectionGivesEachTargetThePartItCanTake()
    {
        var payload = Many(Text("hello"), Image(@"C:\assets\shot.png"));

        // A text field gets the text clip; a folder gets the screenshot. Neither refuses the drag.
        Assert.Equal("hello", payload.Text?.Text);
        Assert.Equal([@"C:\assets\shot.png"], payload.FilePaths);
        Assert.Null(payload.BitmapPath);
    }

    [Fact]
    public void FileClipsContributeEveryPathTheyHold()
    {
        var files = new ClipboardHistoryItem { Kind = ClipboardItemKind.Files, FilePaths = [@"C:\a.txt", @"C:\b.txt"] };
        var more = new ClipboardHistoryItem { Kind = ClipboardItemKind.Files, FilePaths = [@"C:\c.txt"] };

        var payload = Many(files, more);

        Assert.Equal([@"C:\a.txt", @"C:\b.txt", @"C:\c.txt"], payload.FilePaths);
    }

    [Fact]
    public void OneItemIsStillTheOrdinaryDrag()
    {
        // Bitmap and rich formats are only dropped because several items cannot agree on them.
        var payload = Many(Image(@"C:\assets\shot.png"));

        Assert.Equal(@"C:\assets\shot.png", payload.BitmapPath);
    }

    [Fact]
    public void ASelectionWithNothingToOfferHasNoDrag()
    {
        var payload = Many(Text(string.Empty), new ClipboardHistoryItem { Kind = ClipboardItemKind.Image });

        Assert.True(payload.IsEmpty);
        Assert.True(ClipboardDragData.CreateMany([], PasteFormatPreference.PlainText).IsEmpty);
    }
}
