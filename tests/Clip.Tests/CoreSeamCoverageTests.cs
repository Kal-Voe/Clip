using System.Diagnostics;
using Clip.Core;

namespace Clip.Tests;

// Exercises the test seams added to Clip.Core: ClipStoragePaths.RootOverride redirects the
// whole %LocalAppData%\Clip tree to a temp folder so the real load/save bodies of
// ClipSharedSettings, ClipStoragePaths and OpenWithRecentStore run hermetically, and
// FileExplorerReveal.Launch captures the ProcessStartInfo instead of opening Explorer.
public sealed class CoreSeamCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public CoreSeamCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch
        {
        }
    }

    private string Sub(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------- ClipStoragePaths under the root override ----------

    [Fact]
    public void StoragePathsDeriveFromOverriddenRoot()
    {
        var root = Sub("paths");
        ClipStoragePaths.RootOverride.Value = root;

        Assert.Equal(Path.Combine(root, "settings.json"), ClipStoragePaths.SettingsPath);
        Assert.Equal(Path.Combine(root, "Clipboard History"), ClipStoragePaths.DefaultClipboardFolderPath);
        Assert.Equal(Path.Combine(root, "WebView2"), ClipStoragePaths.WebView2UserDataFolderPath);
    }

    [Fact]
    public void EffectiveClipboardFolderPathFallsBackWhenSettingsMissing()
    {
        ClipStoragePaths.RootOverride.Value = Sub("no-settings");

        Assert.Equal(ClipStoragePaths.DefaultClipboardFolderPath, ClipStoragePaths.EffectiveClipboardFolderPath());
    }

    [Fact]
    public void EffectiveClipboardFolderPathUsesConfiguredValue()
    {
        var root = Sub("configured");
        ClipStoragePaths.RootOverride.Value = root;
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"ClipboardFolderPath\":\"D:\\\\Somewhere\\\\Clips\"}");

        Assert.Equal(@"D:\Somewhere\Clips", ClipStoragePaths.EffectiveClipboardFolderPath());
    }

    [Fact]
    public void EffectiveClipboardFolderPathIgnoresCorruptOrNonStringSettings()
    {
        var root = Sub("corrupt");
        ClipStoragePaths.RootOverride.Value = root;

        File.WriteAllText(Path.Combine(root, "settings.json"), "{{{ not json");
        Assert.Equal(ClipStoragePaths.DefaultClipboardFolderPath, ClipStoragePaths.EffectiveClipboardFolderPath());

        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"ClipboardFolderPath\":123}");
        Assert.Equal(ClipStoragePaths.DefaultClipboardFolderPath, ClipStoragePaths.EffectiveClipboardFolderPath());
    }

    // ---------- ClipSharedSettings void setters, Update and Load ----------

    [Fact]
    public void SettingsSettersRoundTripThroughOverriddenRoot()
    {
        var root = Sub("settings");
        ClipStoragePaths.RootOverride.Value = root;

        ClipSharedSettings.SetCheckForUpdatesOnStartup(false);            // creates the file
        ClipSharedSettings.SetDefaultPasteFormat(PasteFormatPreference.OriginalFormatting);
        ClipSharedSettings.SetHistoryLimit(42);                           // updates the existing file
        ClipSharedSettings.SetMaxItemSizeBytes(1234);
        var clipsFolder = Path.Combine(root, "Clips");
        ClipSharedSettings.SetClipboardFolderPath(clipsFolder);

        var snapshot = ClipSharedSettings.Load();
        Assert.False(snapshot.CheckForUpdatesOnStartup);
        Assert.Equal(PasteFormatPreference.OriginalFormatting, snapshot.DefaultPasteFormat);
        Assert.Equal(42, snapshot.HistoryLimit);
        Assert.Equal(1234, snapshot.MaxItemSizeBytes);
        Assert.Equal(clipsFolder, snapshot.ClipboardFolderPath);
        Assert.Equal(PasteFormatPreference.OriginalFormatting, ClipSharedSettings.LoadDefaultPasteFormat());

        ClipSharedSettings.SetHistoryLimit(null);
        ClipSharedSettings.SetMaxItemSizeBytes(null);
        ClipSharedSettings.SetClipboardFolderPath("   ");
        var cleared = ClipSharedSettings.Load();
        Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, cleared.HistoryLimit);
        Assert.Equal(ClipSharedSettings.DefaultMaxItemSizeBytes, cleared.MaxItemSizeBytes);
        Assert.Null(cleared.ClipboardFolderPath);
    }

    [Fact]
    public void LoadReturnsDefaultsWhenSettingsFileMissing()
    {
        ClipStoragePaths.RootOverride.Value = Sub("settings-missing");

        var snapshot = ClipSharedSettings.Load();

        Assert.True(snapshot.CheckForUpdatesOnStartup);
        Assert.Equal(ClipSharedSettings.DefaultPasteFormat, snapshot.DefaultPasteFormat);
        Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, snapshot.HistoryLimit);
        Assert.Equal(ClipSharedSettings.DefaultMaxItemSizeBytes, snapshot.MaxItemSizeBytes);
        Assert.Null(snapshot.ClipboardFolderPath);
    }

    [Fact]
    public void LoadReturnsDefaultsWhenSettingsFileIsUnreadable()
    {
        var root = Sub("settings-locked");
        ClipStoragePaths.RootOverride.Value = root;
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{\"HistoryLimit\":7}");

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var snapshot = ClipSharedSettings.Load();
            Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, snapshot.HistoryLimit);
        }
    }

    // ---------- OpenWithRecentStore save/load under the root override ----------

    [Fact]
    public void RecentStoreSaveAndLoadRoundTripWithDedupeAndTrim()
    {
        var root = Sub("recent");
        ClipStoragePaths.RootOverride.Value = root;
        var target = Path.Combine(root, "sample.txt");
        File.WriteAllText(target, "x");
        var exe = Path.Combine(root, "editor.exe");
        File.WriteAllText(exe, "MZ");

        // First save creates the store file; the second reads the existing one.
        OpenWithRecentStore.Save(target, new AppChoice("Editor", exe, "Start Menu"));
        OpenWithRecentStore.Save(target, new AppChoice("Store App", null, "Store app", AppUserModelId: "Pkg!App"));
        // Re-saving the same executable dedupes by AppKey and moves it to the front.
        OpenWithRecentStore.Save(target, new AppChoice("Editor Again", exe, "Start Menu"));

        var loaded = OpenWithRecentStore.Load(target);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Editor Again", loaded[0].Name);
        Assert.All(loaded, app => Assert.True(app.IsRecent));
        Assert.All(loaded, app => Assert.Equal("Recent", app.Source));

        // Ten more distinct packaged apps trim the recents list down to eight.
        for (var i = 0; i < 10; i++)
        {
            OpenWithRecentStore.Save(target, new AppChoice($"App {i}", null, "Store app", AppUserModelId: $"Pkg{i}!App"));
        }

        Assert.Equal(8, OpenWithRecentStore.Load(target).Count);
    }

    [Fact]
    public void RecentStoreLoadFiltersMissingExecutablesAndUnknownExtensions()
    {
        var root = Sub("recent-filter");
        ClipStoragePaths.RootOverride.Value = root;
        var target = Path.Combine(root, "doc.txt");
        File.WriteAllText(target, "x");
        var ghost = Path.Combine(root, "ghost.exe"); // never created

        OpenWithRecentStore.Save(target, new AppChoice("Ghost", ghost, "Start Menu"));

        Assert.Empty(OpenWithRecentStore.Load(target));
        Assert.Empty(OpenWithRecentStore.Load(Path.Combine(root, "other.zzz")));
    }

    [Fact]
    public void RecentStoreUsesFolderKeyForDirectories()
    {
        var root = Sub("recent-folder");
        ClipStoragePaths.RootOverride.Value = root;
        var folder = Sub("recent-folder-target");
        var exe = Path.Combine(root, "files.exe");
        File.WriteAllText(exe, "MZ");

        OpenWithRecentStore.Save(folder, new AppChoice("Files", exe, "Start Menu"));

        var loaded = Assert.Single(OpenWithRecentStore.Load(folder));
        Assert.Equal("Files", loaded.Name);
    }

    [Fact]
    public void RecentStoreSurvivesCorruptStoreFile()
    {
        var root = Sub("recent-bad");
        ClipStoragePaths.RootOverride.Value = root;
        var target = Path.Combine(root, "a.txt");
        File.WriteAllText(target, "x");
        File.WriteAllText(Path.Combine(root, "open-with-recent.json"), "garbage {{{");

        Assert.Empty(OpenWithRecentStore.Load(target));                                  // Load catch
        OpenWithRecentStore.Save(target, new AppChoice("S", null, "Store app", AppUserModelId: "P!A")); // Save catch
        Assert.Empty(OpenWithRecentStore.Load(target));
    }

    // ---------- FileExplorerReveal via the Launch seam ----------

    [Fact]
    public void TryRevealLaunchesExplorerForFilesAndFolders()
    {
        var folder = Sub("reveal");
        var file = Path.Combine(folder, "target.txt");
        File.WriteAllText(file, "x");
        var prior = FileExplorerReveal.Launch;
        ProcessStartInfo? seen = null;
        try
        {
            FileExplorerReveal.Launch = info => seen = info;

            Assert.True(FileExplorerReveal.TryReveal(file));
            Assert.NotNull(seen);
            Assert.Equal("explorer.exe", seen!.FileName);
            Assert.StartsWith("/select,", seen.Arguments);
            Assert.Contains(file, seen.Arguments);

            seen = null;
            Assert.True(FileExplorerReveal.TryReveal(folder));
            Assert.Equal($"\"{folder}\"", seen!.Arguments);
        }
        finally
        {
            FileExplorerReveal.Launch = prior;
        }
    }

    [Fact]
    public void DefaultLaunchDelegateStartsTheProcess()
    {
        // The default delegate is Process.Start; a nonexistent executable with
        // UseShellExecute=false fails inside Process.Start without showing anything.
        var ghost = Path.Combine(Sub("launch"), "ghost-" + Guid.NewGuid().ToString("N") + ".exe");
        var startInfo = new ProcessStartInfo(ghost) { UseShellExecute = false };

        Assert.ThrowsAny<Exception>(() => FileExplorerReveal.Launch(startInfo));
    }

    // ---------- BlipShareLaunchPlan search directories ----------

    [Fact]
    public void BlipSearchIncludesWindowsAppsFolderFromLocalAppData()
    {
        var localAppData = Sub("lad");
        var probed = new List<string>();

        // Returning false drives the enumeration to completion, past the WindowsApps yield.
        var found = BlipShareLaunchPlan.IsInstalled("", localAppData, path =>
        {
            probed.Add(path);
            return false;
        });

        Assert.False(found);
        Assert.Contains(
            Path.Combine(localAppData, "Microsoft", "WindowsApps", BlipShareLaunchPlan.ExecutableName),
            probed);
    }

    // ---------- ClipboardHistoryListItem open-with target edge cases ----------

    [Fact]
    public void TryGetOpenWithTargetRejectsColorItemsAndBarePathPrefixes()
    {
        var color = OpenWithListItem("Color", preview: "#aabbcc");
        Assert.False(color.TryGetOpenWithTarget(out _));

        // A non-ASCII "drive letter" passes the quick prefix check (char.IsLetter) but
        // Path.IsPathFullyQualified only accepts A-Z drives, so no line qualifies and the
        // list item is built without an "open" action.
        var fakeDrive = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "é:\\decoy",
            Preview = "é:\\decoy",
        };
        var listItem = ClipboardHistoryListItem.FromHistoryItem(fakeDrive);
        Assert.DoesNotContain(listItem.Actions, action => action.Id == "open");
    }

    private static ClipboardHistoryListItem OpenWithListItem(string kind, string preview)
    {
        var actions = new List<ClipboardHistoryListAction>
        {
            new("open", "Open", "Clip.Watcher.exe", ["open", "id"], RequiresFullItem: true),
        };

        return new ClipboardHistoryListItem(
            Id: "id",
            Kind: kind,
            Title: "title",
            Preview: preview,
            FilePaths: [],
            IsPinned: false,
            PinOrder: 0,
            HasOriginalFormatting: false,
            SourceApplication: null,
            AssetSizeBytes: null,
            CharacterCount: null,
            WordCount: null,
            LastUsedAt: DateTimeOffset.UtcNow,
            LastCopiedAt: DateTimeOffset.UtcNow,
            CopyCount: 1,
            ImageWidth: null,
            ImageHeight: null,
            DefaultActionId: "open",
            Actions: actions);
    }
}
