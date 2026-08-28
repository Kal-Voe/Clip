using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Clip.Shell;

namespace Clip.Tests;

public sealed class ClipUpdateServiceCoverageTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private static ClipUpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)), "https://example.invalid/releases/latest");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void CurrentVersionIsCleanAndParseable()
    {
        var version = ClipUpdateService.CurrentVersion;

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain("+", version);
        Assert.False(version.StartsWith('v'));
        Assert.True(Version.TryParse(version, out _));
    }

    [Fact]
    public void NotCheckedStatusCarriesCurrentVersion()
    {
        var status = ClipUpdateStatus.NotChecked("1.2.3");

        Assert.Equal("Not checked", status.State);
        Assert.Equal("1.2.3", status.CurrentVersion);
        Assert.Null(status.LatestVersion);
        Assert.Null(status.DownloadUrl);
    }

    [Fact]
    public async Task CheckReportsFailureStatusCode()
    {
        var service = Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var status = await service.CheckAsync();

        Assert.Equal("Check failed", status.State);
        Assert.Contains("500", status.Message);
    }

    [Fact]
    public async Task CheckReportsMissingVersion()
    {
        var service = Service(_ => Json("{}"));

        var status = await service.CheckAsync();

        Assert.Equal("Check failed", status.State);
        Assert.Contains("Could not read the latest release version", status.Message);
    }

    [Fact]
    public async Task CheckReportsExceptionsAsFailure()
    {
        var service = new ClipUpdateService(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("boom"))),
            "https://example.invalid/releases/latest");

        var status = await service.CheckAsync();

        Assert.Equal("Check failed", status.State);
        Assert.Contains("boom", status.Message);
    }

    [Fact]
    public async Task CheckFindsNewerReleaseAndPicksInstallerAsset()
    {
        var service = Service(_ => Json("""
            {
              "tag_name": "v999.0.0",
              "html_url": "https://example.invalid/rel",
              "assets": [
                { "browser_download_url": "https://example.invalid/readme.txt" },
                { "browser_download_url": "https://example.invalid/Clip.zip" }
              ]
            }
            """));

        var status = await service.CheckAsync();

        Assert.Equal("Update available", status.State);
        Assert.Equal("999.0.0", status.LatestVersion);
        Assert.Equal("https://example.invalid/rel", status.ReleaseUrl);
        Assert.Equal("https://example.invalid/Clip.zip", status.DownloadUrl);
    }

    [Fact]
    public async Task CheckFallsBackToReleaseNameWhenTagMissing()
    {
        var service = Service(_ => Json("""{ "name": "v999.0.0" }"""));

        var status = await service.CheckAsync();

        Assert.Equal("Update available", status.State);
        Assert.Equal("999.0.0", status.LatestVersion);
        Assert.Null(status.DownloadUrl);
    }

    [Fact]
    public async Task CheckReportsUpToDateForOldRelease()
    {
        var service = Service(_ => Json("""{ "tag_name": "v0.0.0" }"""));

        var status = await service.CheckAsync();

        Assert.Equal("Up to date", status.State);
        Assert.Equal("0.0.0", status.LatestVersion);
    }

    [Fact]
    public async Task DownloadReturnsNullWithoutDownloadUrl()
    {
        var service = Service(_ => throw new InvalidOperationException("must not be called"));
        var status = ClipUpdateStatus.NotChecked("1.0.0");

        Assert.Null(await service.DownloadUpdateAsync(status));
    }

    [Fact]
    public async Task DownloadWritesAssetToTempUpdateFolder()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var service = Service(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var latest = $"9.9.{Random.Shared.Next(1000, 9999)}";
        var status = new ClipUpdateStatus(
            "Update available",
            "msg",
            "1.0.0",
            latest,
            DownloadUrl: "https://example.invalid/Clip-setup.zip");

        var target = await service.DownloadUpdateAsync(status);

        try
        {
            Assert.NotNull(target);
            Assert.EndsWith($"Clip-{latest}.zip", target);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target!));
        }
        finally
        {
            if (target is not null && File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    [Fact]
    public void LaunchInstallerReturnsFalseForMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Clip.Tests", $"{Guid.NewGuid():N}.exe");

        Assert.False(ClipUpdateService.LaunchInstaller(missing, Path.GetTempPath(), processId: 0));
    }

    [Fact]
    public void InstallScriptKillsOnlyClipsOwnWebViewProcesses()
    {
        var script = ClipUpdateService.BuildInstallScript(
            @"C:\extract",
            @"C:\install",
            processId: 42,
            webView2UserDataFolder: @"C:\Users\o'brien\AppData\Local\Clip\WebView2");

        // The old form was an unfiltered "Get-Process msedgewebview2 | Stop-Process" that took
        // down Outlook/Teams/widgets webviews machine-wide. The kill must be scoped by Clip's
        // own user-data-folder on the child's command line, with quotes in the path surviving
        // the trip into single-quoted PowerShell.
        Assert.DoesNotContain("Get-Process msedgewebview2", script);
        Assert.Contains(@"$webViewData = 'C:\Users\o''brien\AppData\Local\Clip\WebView2'", script);
        Assert.Contains("Get-CimInstance Win32_Process", script);
        Assert.Contains("$_.CommandLine.IndexOf($webViewData", script);
        Assert.Contains("Stop-Process -Id $_.ProcessId", script);
        foreach (var line in script.Split('\n'))
        {
            if (line.Contains("Stop-Process"))
            {
                // Never a bare Stop-Process by name - only the filtered process ids.
                Assert.Contains("-Id", line);
            }
        }
    }

    [Fact]
    public void InstallScriptCopiesIntoUnicodeAndSpacedInstallPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "Clip.Tests", $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            probe.WaitForExit();

            var source = Path.Combine(root, "extract");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "payload.txt"), "new build");

            // The hostile-but-real shapes an install folder can take: spaces, an apostrophe,
            // and characters outside every ANSI codepage. Windows PowerShell reads a BOM-less
            // .ps1 as ANSI, so before WriteInstallScript stamped the BOM these paths decoded
            // as mojibake and the copy landed in a garbage-named folder.
            var target = Path.Combine(root, "Program Files", "o'brien の Clip");
            var script = ClipUpdateService.BuildInstallScript(
                source, target, probe.Id, Path.Combine(root, "webview-data-that-matches-no-process"));
            var scriptPath = Path.Combine(root, "Install-ClipUpdate.ps1");
            ClipUpdateService.WriteInstallScript(scriptPath, script);

            // The BOM is the load-bearing byte sequence — without it the whole unicode-path
            // guarantee silently evaporates on machines whose ANSI codepage is not UTF-8.
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(scriptPath).Take(3).ToArray());

            using var powershell = Process.Start(new ProcessStartInfo(
                "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            Assert.True(powershell.WaitForExit(60_000), "install script did not finish");
            Assert.Equal(0, powershell.ExitCode);
            Assert.Equal("new build", File.ReadAllText(Path.Combine(target, "payload.txt")));
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }

    [Fact]
    public void InstallScriptRelaunchesClipWhenEveryCopyAttemptFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "Clip.Tests", $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // A pid that has already exited, so the script's Wait-Process returns immediately.
            using var probe = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            probe.WaitForExit();

            var target = Path.Combine(root, "install");
            var script = ClipUpdateService.BuildInstallScript(
                Path.Combine(root, "missing-source"),
                target,
                probe.Id,
                Path.Combine(root, "webview-data-that-matches-no-process"));
            var scriptPath = Path.Combine(root, "Install-ClipUpdate.ps1");
            File.WriteAllText(scriptPath, script);

            using var powershell = Process.Start(new ProcessStartInfo(
                "powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            // The source folder never exists, so all three copy attempts fail. The old script
            // threw at that point (exit 1) and left Clip dead with no restart; the fixed one
            // must run to completion and reach the relaunch line - its try/catch swallows the
            // missing Clip.exe here, so a clean exit proves the whole failure path.
            Assert.True(powershell.WaitForExit(60_000), "install script did not finish");
            Assert.Equal(0, powershell.ExitCode);
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }
}
