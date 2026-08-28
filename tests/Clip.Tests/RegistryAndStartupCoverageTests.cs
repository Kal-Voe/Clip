using Clip.Core;
using Microsoft.Win32;

namespace Clip.Tests;

// WindowsClipboardHistory against a sandbox registry key (HKCU\Software\ClipTests\<guid>),
// plus the real-hive wrappers of WindowsClipboardHistory and StartupRegistration with
// snapshot/restore of the touched values so the machine ends up exactly as it started.
// Serialized with StartupRegistrationCoverageTests: both swap the static InfoLog/ErrorLog
// sinks, and parallel classes clobber each other's captures.
[Collection("StartupStatics")]
public sealed class RegistryAndStartupCoverageTests : IDisposable
{
    private readonly string _sandboxPath = @"Software\ClipTests\" + Guid.NewGuid().ToString("N");
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public RegistryAndStartupCoverageTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_sandboxPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        try
        {
            TestTemp.Delete(_tempRoot);
        }
        catch
        {
        }
    }

    // ---------- WindowsClipboardHistory ----------

    [Fact]
    public void EnsureEnabledWritesFlagThenReportsAlreadyOn()
    {
        var keyPath = _sandboxPath + @"\Clipboard";
        var policyPath = _sandboxPath + @"\Policy";

        Assert.True(WindowsClipboardHistory.EnsureEnabled(Registry.CurrentUser, Registry.CurrentUser, keyPath, policyPath));
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            Assert.Equal(1, key!.GetValue("EnableClipboardHistory"));
        }

        // Second call sees the value already on and leaves it alone.
        Assert.True(WindowsClipboardHistory.EnsureEnabled(Registry.CurrentUser, Registry.CurrentUser, keyPath, policyPath));
    }

    [Fact]
    public void EnsureEnabledRespectsBlockingPolicy()
    {
        var keyPath = _sandboxPath + @"\Clipboard2";
        var policyPath = _sandboxPath + @"\Policy2";
        using (var policy = Registry.CurrentUser.CreateSubKey(policyPath))
        {
            policy!.SetValue("AllowClipboardHistory", 0, RegistryValueKind.DWord);
        }

        Assert.False(WindowsClipboardHistory.EnsureEnabled(Registry.CurrentUser, Registry.CurrentUser, keyPath, policyPath));
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        Assert.Null(key?.GetValue("EnableClipboardHistory"));
    }

    [Fact]
    public void EnsureEnabledReturnsFalseWhenRegistryIsUnusable()
    {
        var closed = Registry.CurrentUser.OpenSubKey("Software")!;
        closed.Dispose();

        Assert.False(WindowsClipboardHistory.EnsureEnabled(closed, closed, _sandboxPath + @"\X", _sandboxPath + @"\Y"));
    }

    [Fact]
    public void EnsureEnabledAgainstRealHiveRestoresPriorState()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Clipboard", writable: true)!;
        var prior = key.GetValue("EnableClipboardHistory");
        try
        {
            var enabled = WindowsClipboardHistory.EnsureEnabled();
            if (enabled)
            {
                Assert.Equal(1, key.GetValue("EnableClipboardHistory"));
            }
        }
        finally
        {
            if (prior is null)
            {
                key.DeleteValue("EnableClipboardHistory", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("EnableClipboardHistory", prior, RegistryValueKind.DWord);
            }
        }
    }

    // ---------- StartupRegistration ----------

    [Fact]
    public void RemoveLegacyStartupShortcutDeletesFileAndLogs()
    {
        var shortcut = Path.Combine(_tempRoot, "Clip.lnk");
        File.WriteAllText(shortcut, "link");
        string? logged = null;
        StartupRegistration.InfoLog = message => logged = message;
        try
        {
            StartupRegistration.RemoveLegacyStartupShortcut(shortcut);
        }
        finally
        {
            StartupRegistration.InfoLog = null;
        }

        Assert.False(File.Exists(shortcut));
        Assert.Contains(shortcut, logged);
    }

    [Fact]
    public void RemoveLegacyStartupShortcutSwallowsDeleteFailure()
    {
        var shortcut = Path.Combine(_tempRoot, "Locked.lnk");
        File.WriteAllText(shortcut, "link");
        Exception? error = null;
        StartupRegistration.ErrorLog = (exception, _) => error = exception;
        try
        {
            using (File.Open(shortcut, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                StartupRegistration.RemoveLegacyStartupShortcut(shortcut);
            }
        }
        finally
        {
            StartupRegistration.ErrorLog = null;
        }

        Assert.NotNull(error);
        Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public void ParameterlessStartupWrappersRoundTripAndRestoreTheRealRunValue()
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var prior = StartupRegistration.CurrentValue();
        var startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Clip.lnk");
        var shortcutBackup = File.Exists(startupShortcut) ? File.ReadAllBytes(startupShortcut) : null;
        try
        {
            StartupRegistration.SetEnabled(true);
            Assert.True(StartupRegistration.IsEnabled());
            Assert.False(string.IsNullOrWhiteSpace(StartupRegistration.CurrentValue()));

            // The freshly written value already targets this bin folder's watcher host,
            // so the parameterless migration check runs and declines.
            _ = StartupRegistration.MigrateToLightweightHostIfNeeded();

            StartupRegistration.SetEnabled(false);
            Assert.False(StartupRegistration.IsEnabled());
        }
        finally
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)!;
            if (prior is null)
            {
                key.DeleteValue(StartupRegistration.RunValueName, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(StartupRegistration.RunValueName, prior, RegistryValueKind.String);
            }

            if (shortcutBackup is not null && !File.Exists(startupShortcut))
            {
                File.WriteAllBytes(startupShortcut, shortcutBackup);
            }
        }
    }
}
