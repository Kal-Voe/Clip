using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// The preview pane used to render <c>item.Preview</c> whenever the row carried no full text —
/// one line, capped at 120 characters, with a literal "..." on the end. These pin down the two
/// facts <c>MainWindow.FullTextPayload</c> relies on: that a list summary really is that short
/// (so the bug would come back the moment the pane reads it again), and that
/// <see cref="ClipboardHistoryStore.GetItem"/> hands back the whole thing.
/// </summary>
public sealed class LongTextPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public LongTextPreviewTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public void RowSummaryIsTruncatedWithAnEllipsis()
    {
        var text = string.Join(" ", Enumerable.Repeat("paragraph", 200));

        var preview = ClipboardHistoryStore.PreviewText(text);

        Assert.True(text.Length > 120, "the fixture has to be longer than the summary cap to prove anything");
        Assert.Equal(120, preview.Length);
        Assert.EndsWith("...", preview);
    }

    [Fact]
    public void GetItemReturnsTheWholeTextTheSummaryTruncated()
    {
        var text = string.Join(" ", Enumerable.Repeat("paragraph", 200));
        var stored = _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
        });

        var full = _store.GetItem(stored.Id);

        Assert.NotNull(full);
        Assert.Equal(text, full!.Text);
        Assert.DoesNotContain("...", full.Text!);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // TestTemp.Delete, like every other ClipboardHistoryStore teardown in this suite.
                // This one rolled its own Directory.Delete with no retry, and caught IOException
                // but not UnauthorizedAccessException - which is what Windows throws when the
                // handle is still open rather than merely busy. Its two siblings
                // (ClipboardHistoryStoreCoverageTests, ClipboardHistoryStoreDeepCoverageTests)
                // catch both, and one of them names the cause: background sidecar writes race the
                // cleanup. An exception out of Dispose fails the class, so this was the one
                // teardown in the suite that could turn that race into a red run.
                TestTemp.Delete(_root);
            }
        }
        catch (IOException)
        {
            // Teardown file locks are a known source of red builds here; a leftover temp dir is
            // not worth failing a green test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
