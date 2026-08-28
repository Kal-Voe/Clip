using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// Edge cases for the token-AND search across both engines: the object path (QueryItems) and
/// the streaming summary-index path (QueryItemSummaries with a limit). Messy whitespace,
/// unicode, tokens spread across different fields, extreme query lengths, and case folding
/// must behave the same everywhere — and never throw.
/// </summary>
public sealed class SearchEdgeCaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MessyWhitespaceAroundTokensStillMatches()
    {
        var store = new ClipboardHistoryStore(_root);
        var target = store.AddOrUpdate(TextItem("quarterly invoice for the pdf export"));

        foreach (var query in new[] { "  invoice pdf", "invoice pdf  ", "invoice    pdf", " \t invoice \r\n pdf \t " })
        {
            Assert.Contains(store.QueryItems(query), item => item.Id == target.Id);
            Assert.Contains(store.QueryItemSummaries(query, limit: 10), item => item.Id == target.Id);
        }
    }

    [Fact]
    public void WhitespaceOnlyQueryReturnsEverything()
    {
        var store = new ClipboardHistoryStore(_root);
        store.AddOrUpdate(TextItem("first"));
        store.AddOrUpdate(TextItem("second"));

        Assert.Equal(2, store.QueryItems("   \t ").Count);
        Assert.Equal(2, store.QueryItemSummaries("   \t ", limit: 10).Count);
    }

    [Fact]
    public void TokensSplitAcrossTitleAndTextMatch()
    {
        var store = new ClipboardHistoryStore(_root);
        var target = store.AddOrUpdate(TextItem("the quarterly pdf export"));
        Assert.True(store.Rename(target.Id, "invoice"));
        store.AddOrUpdate(TextItem("unrelated pdf item"));

        // "invoice" only lives in the custom title, "pdf" only in the text — token-AND must
        // let each token match a different field of the same item.
        var matches = store.QueryItems("invoice pdf");
        Assert.Equal(target.Id, Assert.Single(matches).Id);
        var summaryMatches = store.QueryItemSummaries("invoice pdf", limit: 10);
        Assert.Equal(target.Id, Assert.Single(summaryMatches).Id);
    }

    [Fact]
    public void TokensSplitAcrossOcrTextAndPreviewMatch()
    {
        var store = new ClipboardHistoryStore(_root);
        var image = store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            ContentHash = "search-ocr-hash",
            Preview = "Screenshot 800 x 600",
            ImageWidth = 800,
            ImageHeight = 600,
        });
        Assert.Equal(1, store.SetOcrText(new Dictionary<string, string?> { [image.Id] = "error code 0x80070005" }));

        Assert.Contains(store.QueryItems("screenshot 0x80070005"), item => item.Id == image.Id);
        Assert.Contains(store.QueryItemSummaries("screenshot 0x80070005", limit: 10), item => item.Id == image.Id);
    }

    [Fact]
    public void EmojiAndCjkQueriesMatch()
    {
        var store = new ClipboardHistoryStore(_root);
        var emoji = store.AddOrUpdate(TextItem("deploy went great 🚀🎉 ship it"));
        var cjk = store.AddOrUpdate(TextItem("日本語のクリップボード履歴テスト"));

        Assert.Contains(store.QueryItems("🚀🎉"), item => item.Id == emoji.Id);
        Assert.Contains(store.QueryItems("日本語"), item => item.Id == cjk.Id);
        Assert.Contains(store.QueryItems("履歴 テスト"), item => item.Id == cjk.Id);
        // The summary streaming reader's ASCII fast path must hand these off to the string
        // path, not mangle them.
        Assert.Contains(store.QueryItemSummaries("🚀🎉", limit: 10), item => item.Id == emoji.Id);
        Assert.Contains(store.QueryItemSummaries("日本語", limit: 10), item => item.Id == cjk.Id);
    }

    [Fact]
    public void CombiningMarksMatchTheSameFormOnly()
    {
        var store = new ClipboardHistoryStore(_root);
        var precomposed = store.AddOrUpdate(TextItem("caf\u00e9 receipt"));
        var decomposed = store.AddOrUpdate(TextItem("cafe\u0301 order"));

        // Search is ordinal by design (no unicode normalization): each form finds itself,
        // and the two forms are distinct strings.
        Assert.Contains(store.QueryItems("caf\u00e9"), item => item.Id == precomposed.Id);
        Assert.Contains(store.QueryItems("cafe\u0301"), item => item.Id == decomposed.Id);
        Assert.DoesNotContain(store.QueryItems("caf\u00e9"), item => item.Id == decomposed.Id);
    }

    [Fact]
    public void CaseFoldingCoversAsciiAndAccentedLetters()
    {
        var store = new ClipboardHistoryStore(_root);
        var ascii = store.AddOrUpdate(TextItem("invoice from vendor"));
        var accented = store.AddOrUpdate(TextItem("caf\u00e9 receipt"));

        Assert.Contains(store.QueryItems("INVOICE"), item => item.Id == ascii.Id);
        Assert.Contains(store.QueryItems("CAF\u00c9"), item => item.Id == accented.Id);
        Assert.Contains(store.QueryItemSummaries("INVOICE", limit: 10), item => item.Id == ascii.Id);
        Assert.Contains(store.QueryItemSummaries("CAF\u00c9", limit: 10), item => item.Id == accented.Id);
    }

    [Fact]
    public void SingleCharacterQueryMatchesBothPaths()
    {
        var store = new ClipboardHistoryStore(_root);
        var target = store.AddOrUpdate(TextItem("zebra"));
        store.AddOrUpdate(TextItem("fish"));

        var matches = store.QueryItems("z");
        Assert.Equal(target.Id, Assert.Single(matches).Id);
        var summaryMatches = store.QueryItemSummaries("z", limit: 10);
        Assert.Equal(target.Id, Assert.Single(summaryMatches).Id);
    }

    [Fact]
    public void TenThousandCharacterQueryReturnsEmptyWithoutThrowing()
    {
        var store = new ClipboardHistoryStore(_root);
        store.AddOrUpdate(TextItem("short item"));
        var query = new string('q', 10_000);

        Assert.Empty(store.QueryItems(query));
        Assert.Empty(store.QueryItemSummaries(query, limit: 10));
    }

    [Fact]
    public void BackslashHeavyQueriesSurviveTheJsonEscapeFastPath()
    {
        var store = new ClipboardHistoryStore(_root);
        var target = store.AddOrUpdate(TextItem(@"copy from C:\Users\isaiah\Documents\report.pdf"));

        // Backslashes are escaped in the summary index json, which disables the raw-bytes
        // substring scan — the fallback through the unescaped string must still match.
        Assert.Contains(store.QueryItems(@"C:\Users"), item => item.Id == target.Id);
        Assert.Contains(store.QueryItemSummaries(@"C:\Users", limit: 10), item => item.Id == target.Id);
        Assert.Contains(store.QueryItemSummaries(@"c:\users report.pdf", limit: 10), item => item.Id == target.Id);
    }

    public void Dispose()
    {
        TestTemp.Delete(_root);
    }

    private static ClipboardHistoryItem TextItem(string text)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
        };
    }
}
