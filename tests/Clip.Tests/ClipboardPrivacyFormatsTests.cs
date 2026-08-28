using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardPrivacyFormatsTests
{
    // Mirrors how both capture paths call the helper: the dictionary plays the data
    // object, ContainsKey is GetDataPresent, TryGetValue is GetData.
    private static bool ShouldExclude(Dictionary<string, object?> formats) =>
        ClipboardPrivacyFormats.ShouldExcludeFromHistory(
            formats.ContainsKey,
            name => formats.TryGetValue(name, out var value) ? value : null);

    [Fact]
    public void AbsentFormatsAreNotExcluded()
    {
        Assert.False(ShouldExclude(new Dictionary<string, object?>()));
        Assert.False(ShouldExclude(new Dictionary<string, object?> { ["UnicodeText"] = "hello" }));
    }

    [Fact]
    public void ExcludeFromMonitorProcessingExcludesByPresenceAlone()
    {
        // The value is irrelevant for this format — even an explicit nonzero DWORD.
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ExcludeFromMonitorProcessing] = null,
        }));
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ExcludeFromMonitorProcessing] = new MemoryStream([1, 0, 0, 0]),
        }));
    }

    [Fact]
    public void ClipboardViewerIgnoreExcludesByPresenceAlone()
    {
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ClipboardViewerIgnore] = null,
        }));
    }

    [Fact]
    public void CanIncludeInClipboardHistoryDwordZeroExcludes()
    {
        // GetData hands custom formats back as a MemoryStream over the raw HGLOBAL, but the
        // helper also accepts pre-unpacked shapes.
        foreach (var zero in new object?[] { new MemoryStream([0, 0, 0, 0]), new byte[] { 0, 0, 0, 0 }, 0, 0u })
        {
            Assert.True(ShouldExclude(new Dictionary<string, object?>
            {
                [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = zero,
            }));
        }
    }

    [Fact]
    public void CanIncludeInClipboardHistoryNonzeroDwordAllows()
    {
        foreach (var nonzero in new object?[] { new MemoryStream([1, 0, 0, 0]), new byte[] { 1, 0, 0, 0 }, 1, 1u })
        {
            Assert.False(ShouldExclude(new Dictionary<string, object?>
            {
                [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = nonzero,
            }));
        }
    }

    [Fact]
    public void CanIncludeInClipboardHistoryUnreadableValueFailsClosed()
    {
        // An app that set the format intended an opt-out; recording a password by accident
        // is the worse failure mode, so anything we cannot parse counts as exclude.
        foreach (var unreadable in new object?[] { null, new MemoryStream([1, 0]), new byte[] { 1 }, "junk" })
        {
            Assert.True(ShouldExclude(new Dictionary<string, object?>
            {
                [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = unreadable,
            }));
        }
    }
}
