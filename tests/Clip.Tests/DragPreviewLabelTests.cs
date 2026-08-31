using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The one decidable part of the drag preview: what a text card actually says. Everything else it
/// does needs a desktop, but the clipping rule is pure, and the ellipsis is the only thing telling
/// the user that the chip under their cursor is not the whole clip.
/// </summary>
public sealed class DragPreviewLabelTests
{
    [Fact]
    public void ShortTextIsShownWholeWithNoEllipsis()
    {
        Assert.Equal("hello there", DragPreview.CardLabel("hello there"));
    }

    [Fact]
    public void TextAtExactlyTheWordLimitIsNotClipped()
    {
        Assert.Equal("one two three four five", DragPreview.CardLabel("one two three four five"));
    }

    [Fact]
    public void AWordPastTheLimitEarnsAnEllipsis()
    {
        Assert.Equal("one two three four five…", DragPreview.CardLabel("one two three four five six"));
    }

    [Fact]
    public void FiveShortWordsOverTheCharacterLimitAreCutAtAWordBoundary()
    {
        // Five words, 33 characters: the word limit lets it through and the character limit is
        // what stops it, so the cut has to land on a space rather than mid-word.
        var label = DragPreview.CardLabel("alphabet bicycle carousel dandelion x");
        Assert.Equal("alphabet bicycle carousel…", label);
    }

    [Fact]
    public void ASingleVeryLongWordIsCutHardBecauseThereIsNoBoundaryToCutAt()
    {
        var label = DragPreview.CardLabel(new string('x', 90));
        Assert.Equal(new string('x', 28) + "…", label);
    }

    [Fact]
    public void OnlyTheFirstLineIsShown()
    {
        Assert.Equal("first line…", DragPreview.CardLabel("first line\nsecond line\nthird"));
    }

    [Fact]
    public void ALaterLineIsEnoughToEarnAnEllipsisEvenWhenTheFirstOneFits()
    {
        // Without this the two-line clip "Dear Bob\nthanks" would claim to be the whole of it.
        Assert.Equal("Dear Bob…", DragPreview.CardLabel("Dear Bob\nthanks"));
    }

    [Fact]
    public void ASingleLineWithTrailingBlankLinesIsNotTreatedAsClipped()
    {
        Assert.Equal("just this", DragPreview.CardLabel("just this\n\n   \r\n"));
    }

    [Fact]
    public void LeadingBlankLinesAreSkippedRatherThanProducingAnEmptyCard()
    {
        // Copied text routinely starts with a newline; taking line zero literally would leave the
        // drag with no preview at all.
        Assert.Equal("actual content", DragPreview.CardLabel("\n\n   \nactual content"));
    }

    [Fact]
    public void RunsOfWhitespaceInsideTheLineAreCollapsed()
    {
        // A row copied out of a table arrives tab-separated; keeping the tabs would leave a hole
        // in the middle of the chip.
        Assert.Equal("name value", DragPreview.CardLabel("name\t\t   value"));
    }

    [Fact]
    public void CarriageReturnsDoNotSurviveIntoTheLabel()
    {
        Assert.Equal("windows line…", DragPreview.CardLabel("windows line\r\nsecond"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData(" \t \r\n  \r\n")]
    public void NothingWorthShowingComesBackEmptySoTheDragGoesWithoutAPreview(string? text)
    {
        Assert.Equal(string.Empty, DragPreview.CardLabel(text));
    }
}
