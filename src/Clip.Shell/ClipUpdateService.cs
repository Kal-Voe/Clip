using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clip.Core;

namespace Clip.Shell;

internal sealed record ClipUpdateStatus(
    string State,
    string Message,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? DownloadUrl = null)
{
    public static ClipUpdateStatus NotChecked(string currentVersion) =>
        new("Not checked", "Update check has not run yet.", currentVersion);
}

internal sealed class ClipUpdateService
{
    public const string DefaultLatestReleaseUrl = "https://api.github.com/repos/Kal-Voe/Clip/releases/latest";

    private readonly HttpClient _http;
    private readonly string _latestReleaseUrl;

    public ClipUpdateService(HttpClient? http = null, string latestReleaseUrl = DefaultLatestReleaseUrl)
    {
        _http = http ?? new HttpClient();
        _latestReleaseUrl = latestReleaseUrl;
    }

    public static string CurrentVersion =>
        CleanVersion(
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            "0.0.0");

    public async Task<ClipUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("ClipUpdateChecker/1.0");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ClipUpdateStatus("Check failed", $"Could not check for updates. GitHub returned {(int)response.StatusCode}.", CurrentVersion);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var latest = release?.TagName ?? release?.Name;
            if (string.IsNullOrWhiteSpace(latest))
            {
                return new ClipUpdateStatus("Check failed", "Could not read the latest release version.", CurrentVersion);
            }

            var download = release?.Assets?
                .FirstOrDefault(asset => asset.BrowserDownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                         asset.BrowserDownloadUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                         asset.BrowserDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;

            if (IsNewerVersion(latest, CurrentVersion))
            {
                return new ClipUpdateStatus("Update available", $"Version {CleanVersion(latest)} is available.", CurrentVersion, CleanVersion(latest), release?.HtmlUrl, download);
            }

            return new ClipUpdateStatus("Up to date", "Clip is up to date.", CurrentVersion, CleanVersion(latest), release?.HtmlUrl, download);
        }
        catch (Exception ex)
        {
            return new ClipUpdateStatus("Check failed", $"Could not check for updates: {ex.Message}", CurrentVersion);
        }
    }

    public async Task<string?> DownloadUpdateAsync(ClipUpdateStatus status, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(status.DownloadUrl))
        {
            return null;
        }

        var extension = Path.GetExtension(new Uri(status.DownloadUrl).AbsolutePath);
        var folder = Path.Combine(Path.GetTempPath(), "Clip", "updates");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"Clip-{status.LatestVersion ?? "update"}{extension}");
        using var stream = await _http.GetStreamAsync(status.DownloadUrl, cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(target);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public static bool LaunchInstaller(string path, string installDirectory, int processId)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractFolder = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            if (Directory.Exists(extractFolder))
            {
                Directory.Delete(extractFolder, recursive: true);
            }

            ZipFile.ExtractToDirectory(path, extractFolder);
            var scriptPath = Path.Combine(Path.GetDirectoryName(path)!, "Install-ClipUpdate.ps1");
            File.WriteAllText(scriptPath, BuildInstallScript(
                extractFolder, installDirectory, processId, ClipStoragePaths.WebView2UserDataFolderPath));
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }

        Process.Start(new ProcessStartInfo(path)
        {
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        });
        return false;
    }

    /// <summary>
    /// Builds the PowerShell that swaps the install in place after this process exits. The script
    /// outlives the process that wrote it, so everything it needs is baked into the text here.
    /// Split out from <see cref="LaunchInstaller"/> so tests can inspect the generated script
    /// without spawning a real installer.
    /// </summary>
    internal static string BuildInstallScript(string extractFolder, string installDirectory, int processId, string webView2UserDataFolder)
    {
        return $$"""
$ErrorActionPreference = 'Stop'
$source = '{{PowerShellString(extractFolder)}}'
$target = '{{PowerShellString(installDirectory)}}'
$pidToWait = {{processId}}
$webViewData = '{{PowerShellString(webView2UserDataFolder)}}'
Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
# Clip's WebView2 children can outlive the shell for a moment and keep DLLs in the install
# folder locked, so they have to go before the copy. But msedgewebview2.exe is shared
# infrastructure - Outlook, Teams and Windows widgets all run under the same name - so kill
# only the instances launched against Clip's own user-data-folder, which WebView2 always puts
# on the child's command line.
Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($webViewData, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force -Path $target | Out-Null
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        break
    } catch {
        if ($attempt -lt 3) { Start-Sleep -Milliseconds 750 }
    }
}
# Relaunch whether the copy landed or not. A failed copy leaves the old (possibly partially
# overwritten) install behind, and Clip dead-with-no-restart is strictly worse than Clip
# running on a mixed install - the next update attempt repairs it. try/catch rather than
# -ErrorAction: Start-Process throws a terminating error for a missing exe, and nothing is
# left running to catch a throw from this script.
try { Start-Process -FilePath (Join-Path $target 'Clip.exe') } catch { }
""";
    }

    public static bool IsNewerVersion(string latest, string current)
    {
        return TryParseVersion(latest, out var latestVersion) &&
            TryParseVersion(current, out var currentVersion) &&
            latestVersion > currentVersion;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        value = CleanVersion(value);
        return Version.TryParse(value, out version!);
    }

    private static string CleanVersion(string value)
    {
        value = value.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            value = value[..dash];
        }

        return value;
    }

    private static string PowerShellString(string value) => value.Replace("'", "''");

    private sealed class GitHubRelease
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
