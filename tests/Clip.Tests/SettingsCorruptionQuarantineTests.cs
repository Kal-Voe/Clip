using Clip.Core;
using Clip.Shell;
using Clip.Watcher;

namespace Clip.Tests;

// A truncated or hand-mangled settings.json must never cost the user their real settings.
// The failure that motivated these: a corrupt file loads as defaults (correct), and the next
// save then flattened those defaults over the user's only copy of Privacy.ExcludedApps and
// hotkeys (the bug). The fix quarantines the unreadable bytes to settings.json.corrupt-* and
// writes through a temp file + rename, so the file on disk is always whole. Exercised through
// the real load/save bodies via the ClipStoragePaths.RootOverride seam.
public sealed class SettingsCorruptionQuarantineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public SettingsCorruptionQuarantineTests()
    {
        Directory.CreateDirectory(_root);
        ClipStoragePaths.RootOverride.Value = _root;
    }

    public void Dispose()
    {
        ClipStoragePaths.RootOverride.Value = null;
        TestTemp.Delete(_root);
    }

    private string SettingsPath => Path.Combine(_root, "settings.json");

    private string[] QuarantineFiles => Directory.GetFiles(_root, "settings.json.corrupt-*");

    private const string TruncatedSettings =
        """{ "Hotkeys": { "CloseClip": "Esc" }, "Privacy": { "ExcludedApps": [ { "Name": "KeeP""";

    [Theory]
    [InlineData(TruncatedSettings)]
    [InlineData("[1, 2, 3]")]
    [InlineData("garbage!!")]
    public void SharedSettingsUpdateQuarantinesAnUnreadableFileInsteadOfReplacingIt(string corrupt)
    {
        File.WriteAllText(SettingsPath, corrupt);

        // The watcher's tray toggle path: the write must succeed even over a corrupt file,
        // but the user's original bytes must survive next door, not vanish under one key.
        ClipSharedSettings.SetCapturePaused(true);

        Assert.True(ClipSharedSettings.Load().CapturePaused);
        var quarantine = Assert.Single(QuarantineFiles);
        Assert.Equal(corrupt, File.ReadAllText(quarantine));
    }

    [Fact]
    public void SharedSettingsUpdatePreservesEveryOtherKeyAndLeavesNoTempFiles()
    {
        File.WriteAllText(
            SettingsPath,
            """{ "Privacy": { "ExcludedApps": [ { "Name": "KeePass" } ] }, "Hotkeys": { "CloseClip": "Esc" } }""");

        ClipSharedSettings.SetCapturePaused(true);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("KeePass", text);
        Assert.Contains("Esc", text);
        Assert.True(ClipSharedSettings.Load().CapturePaused);
        Assert.Empty(QuarantineFiles);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SharedSettingsToggleIsReadableByTheWatcherParser()
    {
        // The pause flag is a cross-process switch: written by one settings writer, read by
        // WatcherSettings' own parser. Both sides must agree on the key through the real file.
        ClipSharedSettings.SetCapturePaused(true);

        Assert.True(WatcherSettings.LoadFromJson(File.ReadAllText(SettingsPath)).CapturePaused);
    }

    [Fact]
    public void ShellLoadQuarantinesATruncatedFileSoTheNextSaveCannotWipeIt()
    {
        File.WriteAllText(SettingsPath, TruncatedSettings);

        var loaded = ClipShellSettings.Load();

        // Defaults are the right fallback — but only alongside the parked original.
        Assert.Empty(loaded.Privacy.ExcludedApps);
        var quarantine = Assert.Single(QuarantineFiles);
        Assert.Equal(TruncatedSettings, File.ReadAllText(quarantine));

        // This save used to be the destructive step: defaults straight over the user's file.
        // Now it writes a fresh file while the quarantined bytes stay recoverable.
        loaded.Save();
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(TruncatedSettings, File.ReadAllText(quarantine));
        Assert.NotNull(ClipShellSettings.Load());
    }

    [Fact]
    public void ShellLoadWithNoFileUsesDefaultsWithoutInventingAQuarantine()
    {
        var loaded = ClipShellSettings.Load();

        Assert.Empty(loaded.Privacy.ExcludedApps);
        Assert.Empty(QuarantineFiles);
    }

    [Fact]
    public void ShellSettingsRoundTripKeepsHotkeyAliasesAndExcludedApps()
    {
        var settings = new ClipShellSettings();
        settings.Privacy.AddExcludedApp("KeePass", @"C:\Tools\KeePass.exe");
        // "del" is the user-typed alias form; Load's Normalize must settle it on the display
        // text without touching "Esc", which is both the default and what keyboards print.
        settings.Hotkeys.DeleteSelected = "del";

        settings.Save();
        var loaded = ClipShellSettings.Load();

        var app = Assert.Single(loaded.Privacy.ExcludedApps);
        Assert.Equal("KeePass", app.Name);
        Assert.Equal(@"C:\Tools\KeePass.exe", app.ExecutablePath);
        Assert.Equal("Esc", loaded.Hotkeys.CloseClip);
        Assert.Equal("Delete", loaded.Hotkeys.DeleteSelected);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
