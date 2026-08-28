using Clip.Core;
using Microsoft.Win32;

namespace Clip.Tests;

// Follows the sandboxing convention of StartupRegistrationTests: every registry write uses a
// unique GUID value name inside HKCU\...\Run and is removed in Dispose, so the user's real
// "Clip" startup value is never touched. The no-arg SetEnabled/Migrate overloads (which target
// the real "Clip" value) are exercised only through their read-only counterparts.
// Serialized with RegistryAndStartupCoverageTests: both swap the static InfoLog/ErrorLog
// sinks, and parallel classes clobber each other's captures.
[Collection("StartupStatics")]
public sealed class StartupRegistrationCoverageTests : IDisposable
{
    private readonly string _valueName = "Clip.Tests." + Guid.NewGuid().ToString("N");
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void NoArgIsEnabledAgreesWithNoArgCurrentValue()
    {
        // Read-only against the real "Clip" value: IsEnabled() must be exactly
        // "CurrentValue() is a non-whitespace string", whatever the machine state is.
        var expected = StartupRegistration.CurrentValue() is string value && !string.IsNullOrWhiteSpace(value);

        Assert.Equal(expected, StartupRegistration.IsEnabled());
    }

    [Fact]
    public void SetEnabledFallsBackToQuotedPathWhenExecutableHasNoDirectory()
    {
        // A bare file name has no directory, so no sibling watcher can be probed
        // and the raw (quoted) path is registered.
        StartupRegistration.SetEnabled(true, _valueName, "SomeApp.exe");

        Assert.Equal("\"SomeApp.exe\"", StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateReturnsFalseWhenNoWatcherExistsNextToLegacyShell()
    {
        var shellExe = Path.Combine(_tempRoot, "Clip.exe");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(shellExe, "");
        WriteStartupValue($"\"{shellExe}\"");

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, shellExe);

        // Without Clip.Watcher.exe the desired command cannot target the watcher,
        // so migration bails and the legacy value stays intact.
        Assert.False(migrated);
        Assert.Equal($"\"{shellExe}\"", StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateHandlesUnquotedLegacyStartupValue()
    {
        var shellExe = Path.Combine(_tempRoot, "Clip.exe");
        var watcherExe = Path.Combine(_tempRoot, "Clip.Watcher.exe");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(shellExe, "");
        File.WriteAllText(watcherExe, "");
        WriteStartupValue(shellExe); // legacy value without surrounding quotes

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, shellExe);

        Assert.True(migrated);
        Assert.Equal($"\"{watcherExe}\" watch", StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateReportsThroughInfoLogSink()
    {
        var shellExe = Path.Combine(_tempRoot, "Clip.exe");
        var watcherExe = Path.Combine(_tempRoot, "Clip.Watcher.exe");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(shellExe, "");
        File.WriteAllText(watcherExe, "");
        WriteStartupValue($"\"{shellExe}\"");

        var messages = new List<string>();
        Action<Exception, string> errorSink = (_, _) => { };
        try
        {
            StartupRegistration.InfoLog = messages.Add;
            StartupRegistration.ErrorLog = errorSink;

            Assert.True(StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, shellExe));

            Assert.Contains(messages, m => m.Contains("startup migrated"));
            Assert.Same(errorSink, StartupRegistration.ErrorLog);
        }
        finally
        {
            StartupRegistration.InfoLog = null;
            StartupRegistration.ErrorLog = null;
        }
    }

    private void WriteStartupValue(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key!.SetValue(_valueName, value, RegistryValueKind.String);
    }

    public void Dispose()
    {
        try
        {
            StartupRegistration.SetEnabled(false, _valueName, "unused");
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
}
