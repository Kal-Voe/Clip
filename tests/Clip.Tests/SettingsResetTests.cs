using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class SettingsResetTests
{
    [Fact]
    public void ResetToDefaultsRestoresPublicSettings()
    {
        var settings = new ClipShellSettings
        {
            DefaultPasteFormat = PasteFormatPreference.OriginalFormatting,
            HistoryLimit = 100,
            MaxItemSizeBytes = 10 * 1024 * 1024,
            CheckForUpdatesOnStartup = false,
            InstallUpdatesAutomatically = true,
            TranslucentBackground = false,
            Hotkeys = new ClipHotkeySettings
            {
                OpenClip = "Ctrl+Space",
                SaveDebugLog = "Ctrl+Alt+L",
            },
            Privacy = new ClipPrivacySettings(),
        };
        settings.Privacy.AddExcludedApp("Notepad", @"C:\Windows\System32\notepad.exe");

        settings.ResetToDefaults();

        Assert.Equal(PasteFormatPreference.PlainText, settings.DefaultPasteFormat);
        Assert.Equal(500, settings.HistoryLimit);
        Assert.Equal(50L * 1024 * 1024, settings.MaxItemSizeBytes);
        Assert.True(settings.CheckForUpdatesOnStartup);
        Assert.True(settings.InstallUpdatesAutomatically);
        Assert.True(settings.TranslucentBackground);
        Assert.Equal("Alt+V", settings.Hotkeys.OpenClip);
        Assert.Equal("Ctrl+Shift+L", settings.Hotkeys.SaveDebugLog);
        Assert.Empty(settings.Privacy.ExcludedApps);
    }
}
