using Clip.Core;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherSettingsJsonCoverageTests
{
    [Fact]
    public void SettingsPathPointsAtClipSettingsJson()
    {
        var path = WatcherSettings.SettingsPath;

        Assert.EndsWith(Path.Combine("Clip", "settings.json"), path);
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void EffectiveClipboardFolderPathUsesDefaultWhenUnset()
    {
        Assert.EndsWith("Clipboard History", new WatcherSettings().EffectiveClipboardFolderPath());
        Assert.EndsWith("Clipboard History", new WatcherSettings { ClipboardFolderPath = "   " }.EffectiveClipboardFolderPath());
        Assert.Equal(@"D:\Elsewhere", new WatcherSettings { ClipboardFolderPath = @"D:\Elsewhere" }.EffectiveClipboardFolderPath());
    }

    [Fact]
    public void EffectiveHistoryLimitClampsAndHandlesUnlimited()
    {
        Assert.Equal(int.MaxValue, new WatcherSettings { HistoryLimit = null }.EffectiveHistoryLimit());
        Assert.Equal(0, new WatcherSettings { HistoryLimit = -5 }.EffectiveHistoryLimit());
        Assert.Equal(10, new WatcherSettings { HistoryLimit = 10 }.EffectiveHistoryLimit());
    }

    [Fact]
    public void CapturePausedReadsFromJsonAndDefaultsToOff()
    {
        Assert.False(WatcherSettings.LoadFromJson("{}").CapturePaused);
        Assert.True(WatcherSettings.LoadFromJson("""{ "CapturePaused": true }""").CapturePaused);
        // Only a literal true pauses; a string or number is not a request to stop capturing.
        Assert.False(WatcherSettings.LoadFromJson("""{ "CapturePaused": "true" }""").CapturePaused);
    }

    [Fact]
    public void LoadReturnsSettingsEvenWithoutFileAccess()
    {
        var settings = WatcherSettings.Load();

        Assert.NotNull(settings);
        Assert.True(settings.EffectiveHistoryLimit() >= 0);
    }

    [Fact]
    public void MalformedJsonFallsBackToDefaults()
    {
        var settings = WatcherSettings.LoadFromJson("{ this is not json");

        Assert.Equal(500, settings.HistoryLimit);
        Assert.Equal("Alt+V", settings.OpenHotkey);
        Assert.Equal(PasteFormatPreference.PlainText, settings.DefaultPasteFormat);
        Assert.Null(settings.ClipboardFolderPath);
    }

    [Fact]
    public void NullNumericPropertiesUseFallbacks()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "HistoryLimit": null, "MaxItemSizeBytes": null }""");

        Assert.Equal(500, settings.HistoryLimit);
        Assert.Equal(50L * 1024 * 1024, settings.MaxItemSizeBytes);
    }

    [Fact]
    public void NonIntegerNumbersUseFallbacks()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "HistoryLimit": 2.5, "MaxItemSizeBytes": 1.25 }""");

        Assert.Equal(500, settings.HistoryLimit);
        Assert.Equal(50L * 1024 * 1024, settings.MaxItemSizeBytes);
    }

    [Fact]
    public void ExplicitNumbersAreRead()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "HistoryLimit": 42, "MaxItemSizeBytes": 1024, "ClipboardFolderPath": "D:\\Clips" }""");

        Assert.Equal(42, settings.HistoryLimit);
        Assert.Equal(1024L, settings.MaxItemSizeBytes);
        Assert.Equal(@"D:\Clips", settings.ClipboardFolderPath);
    }

    [Fact]
    public void AppIconAcceptsStringsAndRejectsGarbage()
    {
        Assert.Equal(WatcherAppIconPreference.Dark, WatcherSettings.LoadFromJson("""{ "AppIcon": "dark" }""").AppIcon);
        Assert.Equal(WatcherAppIconPreference.Light, WatcherSettings.LoadFromJson("""{ "AppIcon": "banana" }""").AppIcon);
        Assert.Equal(WatcherAppIconPreference.Light, WatcherSettings.LoadFromJson("""{ "AppIcon": 0 }""").AppIcon);
    }

    [Fact]
    public void OpenHotkeyComesFromNestedHotkeysObject()
    {
        Assert.Equal("Ctrl+Space", WatcherSettings.LoadFromJson("""{ "Hotkeys": { "OpenClip": "Ctrl+Space" } }""").OpenHotkey);
        Assert.Equal("Alt+V", WatcherSettings.LoadFromJson("""{ "Hotkeys": "nope" }""").OpenHotkey);
        Assert.Equal("Alt+V", WatcherSettings.LoadFromJson("""{ "Hotkeys": { "OpenClip": 5 } }""").OpenHotkey);
    }

    [Fact]
    public void DefaultPasteFormatParsesNumbersAndStrings()
    {
        Assert.Equal(PasteFormatPreference.OriginalFormatting, WatcherSettings.LoadFromJson("""{ "DefaultPasteFormat": 1 }""").DefaultPasteFormat);
        Assert.Equal(PasteFormatPreference.PlainText, WatcherSettings.LoadFromJson("""{ "DefaultPasteFormat": 99 }""").DefaultPasteFormat);
        Assert.Equal(PasteFormatPreference.OriginalFormatting, WatcherSettings.LoadFromJson("""{ "DefaultPasteFormat": "originalformatting" }""").DefaultPasteFormat);
        Assert.Equal(PasteFormatPreference.PlainText, WatcherSettings.LoadFromJson("""{ "DefaultPasteFormat": "bogus" }""").DefaultPasteFormat);
    }

    [Fact]
    public void PrivacyStringEntriesAreParsedAndDeduplicated()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "Privacy": { "ExcludedApps": ["KeePass", "keepass"] } }""");

        Assert.Single(settings.Privacy.ExcludedApps);
        Assert.False(settings.Privacy.RequiresSourcePath);
        Assert.True(settings.Privacy.IsExcluded("KeePass", null));
        Assert.True(settings.Privacy.IsExcluded("keepass.exe", null));
        Assert.False(settings.Privacy.IsExcluded("chrome", null));
    }

    [Fact]
    public void PrivacyObjectEntriesMatchByExecutablePath()
    {
        var settings = WatcherSettings.LoadFromJson(
            """{ "Privacy": { "ExcludedApps": [ { "Name": "Signal", "ExecutablePath": "C:\\Apps\\Signal\\signal.exe" } ] } }""");

        Assert.Single(settings.Privacy.ExcludedApps);
        Assert.True(settings.Privacy.RequiresSourcePath);
        Assert.True(settings.Privacy.IsExcluded(null, @"C:\Apps\Signal\signal.exe"));
        Assert.True(settings.Privacy.IsExcluded(null, @"C:\APPS\SIGNAL\SIGNAL.EXE"));
        Assert.True(settings.Privacy.IsExcluded("Signal", null));
        Assert.False(settings.Privacy.IsExcluded("chrome", @"C:\Apps\Chrome\chrome.exe"));
    }

    [Fact]
    public void PrivacyIgnoresMalformedShapes()
    {
        Assert.Empty(WatcherSettings.LoadFromJson("""{ "Privacy": 123 }""").Privacy.ExcludedApps);
        Assert.Empty(WatcherSettings.LoadFromJson("""{ "Privacy": { "ExcludedApps": "nope" } }""").Privacy.ExcludedApps);
        Assert.Empty(WatcherSettings.LoadFromJson("""{ "Privacy": { "ExcludedApps": [5, true] } }""").Privacy.ExcludedApps);
    }

    [Fact]
    public void ExcludedAppCreateRejectsEmptyEntries()
    {
        Assert.Null(WatcherExcludedApp.Create(null, null));
        Assert.Null(WatcherExcludedApp.Create("   ", "\"\""));
    }

    [Fact]
    public void ExcludedAppCreateDerivesNameFromPath()
    {
        var app = WatcherExcludedApp.Create(null, @"C:\Apps\Foo\foo.exe");

        Assert.NotNull(app);
        Assert.Equal("foo", app!.Name);
        Assert.Equal(@"C:\Apps\Foo\foo.exe", app.ExecutablePath);
        Assert.True(app.RequiresSourcePath);
    }

    [Fact]
    public void MatchesEntryComparesNormalizedKeys()
    {
        var byName = WatcherExcludedApp.Create("Notepad", null)!;
        var byNameUpper = WatcherExcludedApp.Create("NOTEPAD", null)!;
        var byPath = WatcherExcludedApp.Create(null, @"C:\Windows\notepad.exe")!;

        Assert.True(byName.MatchesEntry(byNameUpper));
        Assert.False(byName.MatchesEntry(byPath));
    }

    [Fact]
    public void MatchesSourceCrossMatchesNamesAndPaths()
    {
        var byName = WatcherExcludedApp.Create("notepad", null)!;
        var byPath = WatcherExcludedApp.Create(null, @"C:\Apps\Foo\foo.exe")!;

        // Name entry matches the file name of a source path.
        Assert.True(byName.MatchesSource(null, @"C:\Windows\notepad.exe"));
        // Path entry matches a bare source process name.
        Assert.True(byPath.MatchesSource("foo", null));
        Assert.False(byPath.MatchesSource("bar", null));
        Assert.False(byName.MatchesSource(null, null));
    }
}
