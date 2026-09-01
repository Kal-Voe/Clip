using Clip.Core;

namespace Clip.Tests;

// Coverage-focused tests for ClipSharedSettings: corrupt/empty JSON handling, the
// boolean coercion branches, and the pure Set*Json writers. The void Set* overloads
// and Update() write the user's real %LocalAppData%\Clip\settings.json, so only the
// read-only Load path is exercised.
public sealed class ClipSharedSettingsCoverageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all {{{")]
    [InlineData("[1, 2, 3]")]
    public void LoadFromJsonFallsBackToDefaultsForEmptyOrCorruptJson(string json)
    {
        var settings = ClipSharedSettings.LoadFromJson(json);

        Assert.True(settings.CheckForUpdatesOnStartup);
        Assert.Equal(ClipSharedSettings.DefaultPasteFormat, settings.DefaultPasteFormat);
        Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, settings.HistoryLimit);
        Assert.Equal(ClipSharedSettings.DefaultMaxItemSizeBytes, settings.MaxItemSizeBytes);
        Assert.Null(settings.ClipboardFolderPath);
    }

    [Theory]
    [InlineData("""{ "CheckForUpdatesOnStartup": true }""", true)]
    [InlineData("""{ "CheckForUpdatesOnStartup": false }""", false)]
    [InlineData("""{ "CheckForUpdatesOnStartup": "yes" }""", true)]
    [InlineData("""{ "CheckForUpdatesOnStartup": 1 }""", true)]
    [InlineData("""{ "CheckForUpdatesOnStartup": null }""", true)]
    public void LoadFromJsonCoercesCheckForUpdatesBoolean(string json, bool expected)
    {
        Assert.Equal(expected, ClipSharedSettings.LoadFromJson(json).CheckForUpdatesOnStartup);
    }

    [Fact]
    public void SetCheckForUpdatesOnStartupJsonRoundTrips()
    {
        var disabled = ClipSharedSettings.SetCheckForUpdatesOnStartupJson("{}", false);
        Assert.False(ClipSharedSettings.LoadFromJson(disabled).CheckForUpdatesOnStartup);

        var enabled = ClipSharedSettings.SetCheckForUpdatesOnStartupJson(disabled, true);
        Assert.True(ClipSharedSettings.LoadFromJson(enabled).CheckForUpdatesOnStartup);
    }

    [Fact]
    public void SetHistoryLimitJsonWritesExplicitNull()
    {
        var json = ClipSharedSettings.SetHistoryLimitJson("""{ "HistoryLimit": 250 }""", null);

        // Explicit null falls back to the canonical default on read.
        Assert.Contains("HistoryLimit", json);
        Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, ClipSharedSettings.LoadFromJson(json).HistoryLimit);
    }

    [Fact]
    public void SetMaxItemSizeBytesJsonWritesExplicitNull()
    {
        var json = ClipSharedSettings.SetMaxItemSizeBytesJson("""{ "MaxItemSizeBytes": 123 }""", null);

        Assert.Contains("MaxItemSizeBytes", json);
        Assert.Equal(ClipSharedSettings.DefaultMaxItemSizeBytes, ClipSharedSettings.LoadFromJson(json).MaxItemSizeBytes);
    }

    // Load() reads the real settings.json (read-only). Its contract is: never throw,
    // always yield a snapshot whose enums are defined and whose limits are defaulted or set.
    [Fact]
    public void LoadNeverThrowsAndYieldsWellFormedSnapshot()
    {
        var settings = ClipSharedSettings.Load();

        Assert.True(Enum.IsDefined(settings.DefaultPasteFormat));
    }

    [Fact]
    public void LoadDefaultPasteFormatReturnsDefinedValue()
    {
        Assert.True(Enum.IsDefined(ClipSharedSettings.LoadDefaultPasteFormat()));
    }
}
