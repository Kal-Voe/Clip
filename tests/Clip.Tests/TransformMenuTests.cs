using Clip.Shell;

namespace Clip.Tests;

public sealed class TransformMenuTests
{
    private static string[] Labels(string text) =>
        MainWindow.TransformOffers(text).Select(o => o.Label).ToArray();

    private const string Reshaping = "UPPERCASE|lowercase|Title Case|Trim spaces and blank lines|Join into one line";

    [Theory]
    [InlineData("https://example.com/watch?v=abc")]     // tidy one-line link: every reshape is a no-op
    [InlineData("already lowercase one liner")]
    [InlineData("MIXED Case Text")]
    [InlineData("   padded   ")]
    [InlineData("two\nlines")]
    public void TheFiveReshapingRowsAreAlwaysOffered(string text)
    {
        // The bug this guards: hiding no-ops left a tidy URL showing only the three case rows, so
        // the menu looked like most of the feature was missing.
        var labels = Labels(text);
        Assert.Equal(Reshaping, string.Join("|", labels.Where(l => l != "Copy links only")));
    }

    [Fact]
    public void CopyLinksOnlyIsOfferedWhenTheTextContainsALink()
    {
        Assert.Contains("Copy links only", Labels("see https://example.com for details"));
    }

    [Fact]
    public void CopyLinksOnlyIsHiddenWhenThereAreNoLinks()
    {
        Assert.DoesNotContain("Copy links only", Labels("just a paragraph with no links at all"));
    }

    [Fact]
    public void CopyLinksOnlyIsHiddenWhenTheTextIsNothingButTheLink()
    {
        // Extracting the link out of a bare link hands back the same string, so the row would be a
        // command that visibly does nothing.
        Assert.DoesNotContain("Copy links only", Labels("https://example.com/watch?v=abc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyTextOffersNothingAtAll(string? text)
    {
        Assert.Empty(MainWindow.TransformOffers(text!));
    }

    [Fact]
    public void EachRowCarriesTheAlreadyComputedResult()
    {
        var offers = MainWindow.TransformOffers("Hello World");
        Assert.Equal("HELLO WORLD", offers.Single(o => o.Label == "UPPERCASE").Result);
        Assert.Equal("hello world", offers.Single(o => o.Label == "lowercase").Result);
    }
}
