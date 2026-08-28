using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherTrayMenuTests
{
    [Fact]
    public void DefaultTrayMenuRestoresUserActions()
    {
        var items = WatcherTrayMenu.DefaultItems;

        Assert.Contains(items, item => item.Action == WatcherTrayAction.OpenClip && item.Label == "Open Clip");
        Assert.Contains(items, item => item.Action == WatcherTrayAction.PasteLatest && item.Label == "Paste latest item");
        Assert.Contains(items, item => item.Action == WatcherTrayAction.TogglePauseCapture && item.Label == "Pause capture");
        Assert.Contains(items, item => item.Action == WatcherTrayAction.CheckForUpdates && item.Label == "Check for updates");
        Assert.Contains(items, item => item.Action == WatcherTrayAction.SaveLogSnapshot && item.Label == "Save log snapshot");
        Assert.Contains(items, item => item.Action == WatcherTrayAction.OpenSettings && item.Label == "Settings");
        Assert.Contains(items, item => item.Action == WatcherTrayAction.Exit && item.Label == "Exit");
    }

    [Fact]
    public void RichPaletteStartInfoUsesPaletteSessionAndTrayAction()
    {
        var startInfo = Program.CreateRichPaletteStartInfo(@"C:\Tools\Clip.exe", WatcherTrayAction.OpenSettings);

        Assert.Equal(@"C:\Tools\Clip.exe", startInfo.FileName);
        Assert.Contains("--palette-session", startInfo.ArgumentList);
        Assert.Contains("--tray-action=settings", startInfo.ArgumentList);
    }

    [Fact]
    public void OpenClipLaunchCarriesExplicitOpenTrayAction()
    {
        var startInfo = Program.CreateRichPaletteStartInfo(@"C:\Tools\Clip.exe", WatcherTrayAction.OpenClip);

        Assert.Equal(["--palette-session", "--tray-action=open"], startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void TrayMenuDefersActionsUntilAfterContextMenuClick()
    {
        var source = File.ReadAllText(RepoPath("src", "Clip.Watcher", "Program.cs"));
        var createTrayMenu = source[
            source.IndexOf("    private ContextMenuStrip CreateTrayMenu()", StringComparison.Ordinal)..
            source.IndexOf("    private void RunTrayAction", StringComparison.Ordinal)];

        Assert.Contains("BeginInvokeIfAlive(() => RunTrayAction(item.Action))", createTrayMenu);
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not find repo file.", Path.Combine(parts));
    }
}
