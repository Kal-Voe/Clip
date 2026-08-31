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
    /// Ordinal, not culture-aware: a transform that only flips case must still count as a change.
    /// </summary>
    [Fact]
    public void CaseOnlyChangesStillCountAsChanges()
    {
        Assert.True(MainWindow.ShouldOfferTransform("Straße", "STRASSE"));
        Assert.True(MainWindow.ShouldOfferTransform("i", "I"));
    }
}
