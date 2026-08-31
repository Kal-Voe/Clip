using System.IO.Compression;
using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExportedHistoryRestoresIntoAFreshFolderWithItsAssets()
    {
        var source = new ClipboardHistoryStore(Path.Combine(_root, "source"));
        source.AddOrUpdate(new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Preview = "note", Text = "a saved note" });
        var pinned = source.AddOrUpdate(ImageItem(source, "pinned shot", [1, 2, 3, 4]));
        source.SetPinned(pinned.Id, true);

        var zipPath = Path.Combine(_root, "backup.zip");
        var exported = ClipboardHistoryBackup.Export(source.ContentRootPath, zipPath);

        var restored = new ClipboardHistoryStore(Path.Combine(_root, "restored"));
        var count = ClipboardHistoryBackup.Restore(zipPath, restored.ContentRootPath);
        restored.ReloadFromDisk();

        Assert.Equal(2, exported);
        Assert.Equal(2, count);
        var items = restored.GetItems();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Text == "a saved note");

        var image = Assert.Single(items, item => item.Kind == ClipboardItemKind.Image);
        Assert.True(image.IsPinned);
        // The asset must have been rebased onto the folder being restored into, not left pointing
        // at the machine's original path, and the bytes must actually be there.
        Assert.StartsWith(restored.ContentRootPath, image.AssetPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(image.AssetPath!));
    }

    [Fact]
    public void RestoringIntoTheSameFolderKeepsTheItems()
    {
        var store = new ClipboardHistoryStore(Path.Combine(_root, "same"));
        store.AddOrUpdate(new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Preview = "kept", Text = "kept" });
        var zipPath = Path.Combine(_root, "same.zip");
        ClipboardHistoryBackup.Export(store.ContentRootPath, zipPath);

        store.AddOrUpdate(new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Preview = "later", Text = "later" });
        ClipboardHistoryBackup.Restore(zipPath, store.ContentRootPath);
        store.ReloadFromDisk();

        var item = Assert.Single(store.GetItems());
        Assert.Equal("kept", item.Text);
    }

    [Fact]
    public void AZipThatIsNotAClipExportIsRefusedWithoutTouchingTheHistory()
    {
        var store = new ClipboardHistoryStore(Path.Combine(_root, "guarded"));
        store.AddOrUpdate(new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Preview = "safe", Text = "safe" });

        var strangerPath = Path.Combine(_root, "stranger.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(strangerPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("holiday.jpg");
        }

        Assert.False(ClipboardHistoryBackup.IsExport(strangerPath));
        Assert.Throws<InvalidDataException>(() => ClipboardHistoryBackup.Restore(strangerPath, store.ContentRootPath));

        store.ReloadFromDisk();
        Assert.Equal("safe", Assert.Single(store.GetItems()).Text);
    }

    [Fact]
    public void ExportingAStoreThatHasNeverSavedAnythingSaysSoInsteadOfWritingAZip()
    {
        var emptyRoot = Path.Combine(_root, "empty", "Clipboard History");
        Directory.CreateDirectory(emptyRoot);
        var zipPath = Path.Combine(_root, "empty.zip");

        Assert.Throws<InvalidOperationException>(() => ClipboardHistoryBackup.Export(emptyRoot, zipPath));
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public void TheDerivedIndexesAreLeftOutSoARestoredFolderRebuildsThem()
    {
        var store = new ClipboardHistoryStore(Path.Combine(_root, "indexed"));
        store.AddOrUpdate(new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Preview = "indexed", Text = "indexed" });
        store.WarmHotIndexes();

        var zipPath = Path.Combine(_root, "indexed.zip");
        ClipboardHistoryBackup.Export(store.ContentRootPath, zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "content/history.json");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("history.index.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("history.keys.json", StringComparison.OrdinalIgnoreCase));
    }

    private static ClipboardHistoryItem ImageItem(ClipboardHistoryStore store, string preview, byte[] bytes)
    {
        var assetPath = store.NewAssetFilePath(ClipboardItemKind.Image, extension: ".png");
        File.WriteAllBytes(assetPath, bytes);
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            Preview = preview,
            AssetPath = assetPath,
            AssetSizeBytes = bytes.Length,
        };
    }

    public void Dispose() => TestTemp.Delete(_root);
}
