using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherAppIconTests
{
    // "AppIcon" is a retired settings key — a file still carrying it must load like any other.
    [Fact]
    public void WatcherSettingsIgnoresTheRetiredAppIconKey()
    {
        var settings = WatcherSettings.LoadFromJson("""{ "AppIcon": 1 }""");

        Assert.Equal("Alt+V", settings.OpenHotkey);
    }

    [Fact]
    public void WatcherTrayIconAlwaysUsesTheLightTile()
    {
        Assert.EndsWith(@"assets\app-icons\clip-tile-light.ico", WatcherTrayIcon.IconPath(@"C:\Clip"));
    }
}
