using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardTextTransformsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("MiXeD case", "MIXED CASE")]
    [InlineData("café crème", "CAFÉ CRÈME")]
    public void UpperRaisesEveryLetter(string? text, string expected)
    {
        Assert.Equal(expected, ClipboardTextTransforms.Upper(text));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("MiXeD case", "mixed case")]
    [InlineData("ÉCOLE", "école")]
    public void LowerDropsEveryLetter(string? text, string expected)
    {
        Assert.Equal(expected, ClipboardTextTransforms.Lower(text));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("hello world", "Hello World")]
    [InlineData("élan vital", "Élan Vital")]
    public void TitleCaseCapitalisesEachWord(string? text, string expected)
    {
        Assert.Equal(expected, ClipboardTextTransforms.TitleCase(text));
    }

    /// <summary>
    /// The interesting case: ToTitleCase on its own treats an all-caps run as an acronym and
    /// leaves it alone, which would make the menu hide the entry as a no-op.
    /// </summary>
    [Fact]
    public void TitleCaseRewritesShoutedText()
    {
        Assert.Equal("Hello World", ClipboardTextTransforms.TitleCase("HELLO WORLD"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("\r\n\t hi \t\r\n", "hi")]
    public void TrimRemovesSurroundingWhitespace(string? text, string expected)
    {
        Assert.Equal(expected, ClipboardTextTransforms.Trim(text));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  \r\n  ", "")]
    [InlineData("one\r\ntwo\r\nthree", "one two three")]
    [InlineData("one\ntwo\nthree", "one two three")]
    [InlineData("one\rtwo", "one two")]
    [InlineData("  spread   \t out \r\n line  ", "spread out line")]
    public void SingleLineCollapsesEveryWhitespaceRun(string? text, string expected)
    {
        Assert.Equal(expected, ClipboardTextTransforms.SingleLine(text));
    }

    /// <summary>
    /// CRLF and LF must land on the same string, or the same paragraph copied from Notepad and
    /// from a terminal would transform differently.
    /// </summary>
    [Fact]
    public void SingleLineTreatsCrlfAndLfAlike()
    {
        Assert.Equal(
            ClipboardTextTransforms.SingleLine("alpha\nbeta\ngamma"),
            ClipboardTextTransforms.SingleLine("alpha\r\nbeta\r\ngamma"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothing to see here")]
    public void ExtractUrlsReturnsNothingWhenThereAreNoLinks(string? text)
    {
        Assert.Equal(string.Empty, ClipboardTextTransforms.ExtractUrls(text));
    }

    /// <summary>
    /// A link ending a sentence must not swallow the full stop, or the extracted URL 404s.
    /// </summary>
    [Fact]
    public void ExtractUrlsDropsPunctuationTrailingALink()
    {
        Assert.Equal("https://example.com", ClipboardTextTransforms.ExtractUrls("Read it at https://example.com."));
    }

    [Fact]
    public void ExtractUrlsUnwrapsBracketedLinks()
    {
        Assert.Equal("https://example.com/docs", ClipboardTextTransforms.ExtractUrls("see (https://example.com/docs), please"));
    }

    [Fact]
    public void ExtractUrlsListsEveryLinkOnePerLineInOrder()
    {
        var text = "first https://one.com then www.two.org and mail me@example.com\nhttps://three.dev";

        var lines = ClipboardTextTransforms.ExtractUrls(text).Split(Environment.NewLine);

        Assert.Equal(
            ["https://one.com", "https://www.two.org", "mailto:me@example.com", "https://three.dev"],
            lines);
    }
}
