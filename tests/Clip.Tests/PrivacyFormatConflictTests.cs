using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// Conflicting and degenerate combinations of the three password-manager exclusion formats.
/// The presence-only formats are absolute vetoes, so they must win over an explicit
/// CanIncludeInClipboardHistory "yes" — an app that set both clearly wanted the copy hidden
/// somewhere, and recording a secret is the worse failure mode.
/// </summary>
public sealed class PrivacyFormatConflictTests
{
    // Mirrors how both capture paths call the helper: the dictionary plays the data
    // object, ContainsKey is GetDataPresent, TryGetValue is GetData.
    private static bool ShouldExclude(Dictionary<string, object?> formats) =>
        ClipboardPrivacyFormats.ShouldExcludeFromHistory(
            formats.ContainsKey,
            name => formats.TryGetValue(name, out var value) ? value : null);

    [Fact]
    public void PresenceFormatsVetoAnExplicitCanIncludeYes()
    {
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ExcludeFromMonitorProcessing] = new MemoryStream([0, 0, 0, 0]),
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = new MemoryStream([1, 0, 0, 0]),
        }));
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ClipboardViewerIgnore] = null,
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = 1u,
        }));
    }

    [Fact]
    public void BothPresenceFormatsTogetherExclude()
    {
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ExcludeFromMonitorProcessing] = null,
            [ClipboardPrivacyFormats.ClipboardViewerIgnore] = null,
        }));
    }

    [Fact]
    public void AllThreeFormatsWithConflictingValuesExclude()
    {
        // The most contradictory data object a capture will ever see: two vetoes plus an
        // explicit allow. Vetoes win.
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.ExcludeFromMonitorProcessing] = 1,
            [ClipboardPrivacyFormats.ClipboardViewerIgnore] = "ignored value",
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = new byte[] { 1, 0, 0, 0 },
        }));
    }

    [Fact]
    public void CanIncludeAloneFollowsDwordSemanticsNotPresence()
    {
        // Unlike the presence formats, this one is a real boolean: nonzero means the app
        // explicitly allowed history, and only zero (or unreadable) opts out.
        Assert.False(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]),
        }));
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = new MemoryStream([0, 0, 0, 0]),
        }));
    }

    [Fact]
    public void DwordReadsFromTheStreamCurrentPosition()
    {
        // A stream someone already consumed reads zero bytes — unreadable, so it fails
        // closed as an opt-out.
        var consumed = new MemoryStream([1, 0, 0, 0]);
        consumed.Seek(0, SeekOrigin.End);
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = consumed,
        }));
    }

    [Fact]
    public void OversizedStreamOnlyTheFirstDwordCounts()
    {
        // Some producers hand back the whole HGLOBAL with trailing padding; only the first
        // four little-endian bytes are the DWORD.
        Assert.False(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = new MemoryStream([1, 0, 0, 0, 0, 0, 0, 0]),
        }));
        Assert.True(ShouldExclude(new Dictionary<string, object?>
        {
            [ClipboardPrivacyFormats.CanIncludeInClipboardHistory] = new MemoryStream([0, 0, 0, 0, 1, 1, 1, 1]),
        }));
    }
}
