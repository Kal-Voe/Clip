using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// The rebuild-from-sidecars path is the last line of defense after history.json is lost, so
/// it must cope with the sidecars themselves being damaged: unreadable json, missing files,
/// assets deleted out from under their metadata, and duplicated ids. Whatever cannot be proven
/// is skipped — never crashed on — and capture keeps working on the rebuilt store.
/// </summary>
public sealed class SidecarRebuildEdgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DamagedSidecarsAreSkippedAndIntactOnesRebuild()
    {
        var store = new ClipboardHistoryStore(_root);
        var intact = store.AddOrUpdate(TextItem("intact sidecar"));
        var corruptSidecar = store.AddOrUpdate(TextItem("corrupt sidecar"));
        var missingSidecar = store.AddOrUpdate(TextItem("missing sidecar"));
        var missingAsset = store.AddOrUpdate(TextItem("missing asset"));

        OverwriteHidden(SidecarFor(corruptSidecar), "{ not sidecar json");
        DeleteHidden(SidecarFor(missingSidecar));
        File.Delete(missingAsset.AssetPath!);
        File.WriteAllText(store.HistoryFilePath, "{ corrupted");

        var rebuilt = new ClipboardHistoryStore(_root);
        var items = rebuilt.QueryItems();

        Assert.NotNull(rebuilt.QuarantinedHistoryPath);
        Assert.Contains(items, item => item.Id == intact.Id && item.Text == "intact sidecar");
        // Nothing provable survives for the other three — but nothing throws either.
        Assert.DoesNotContain(items, item => item.Id == corruptSidecar.Id);
        Assert.DoesNotContain(items, item => item.Id == missingAsset.Id);

        var captured = rebuilt.AddOrUpdate(TextItem("captured after damaged rebuild"));
        Assert.Contains(rebuilt.QueryItems(), item => item.Id == captured.Id);
    }

    [Fact]
    public void SidecarWithoutIdOrWithUnknownKindIsSkipped()
    {
        var store = new ClipboardHistoryStore(_root);
        var anchor = store.AddOrUpdate(TextItem("anchor"));

        // Hand-write two bogus sidecars next to real payload files so the enumerator finds them.
        var idlessAsset = Path.Combine(store.AssetPath, "idless.txt");
        Directory.CreateDirectory(store.AssetPath);
        File.WriteAllText(idlessAsset, "payload");
        File.WriteAllText(idlessAsset + ".clip.json", "{ \"Kind\": \"Text\" }");
        var alienAsset = Path.Combine(store.AssetPath, "alien.txt");
        File.WriteAllText(alienAsset, "payload");
        File.WriteAllText(alienAsset + ".clip.json", "{ \"Id\": \"alien-1\", \"Kind\": \"NotAKind\" }");
        File.WriteAllText(store.HistoryFilePath, "not json at all");

        var rebuilt = new ClipboardHistoryStore(_root);
        var items = rebuilt.QueryItems();

        Assert.Contains(items, item => item.Id == anchor.Id);
        Assert.DoesNotContain(items, item => item.Id == "alien-1");
    }

    [Fact]
    public void DuplicateSidecarIdsRebuildToASingleItem()
    {
        var store = new ClipboardHistoryStore(_root);
        var original = store.AddOrUpdate(TextItem("duplicated sidecar id"));

        // A copy of asset + sidecar (a backup tool, a manual copy) must not clone the item.
        var copyAsset = original.AssetPath! + ".copy.txt";
        File.Copy(original.AssetPath!, copyAsset);
        File.Copy(SidecarFor(original), copyAsset + ".clip.json");
        File.WriteAllText(store.HistoryFilePath, "[ truncated");

        var rebuilt = new ClipboardHistoryStore(_root);
        var items = rebuilt.QueryItems();

        Assert.Single(items, item => item.Id.Equals(original.Id, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        TestTemp.Delete(_root);
    }

    private static string SidecarFor(ClipboardHistoryItem item)
    {
        return item.AssetPath! + ".clip.json";
    }

    // Sidecars are written with the Hidden attribute, which blocks a plain overwrite or delete;
    // strip it first so the tests can damage them the way an external tool would.
    private static void OverwriteHidden(string path, string content)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, content);
    }

    private static void DeleteHidden(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static ClipboardHistoryItem TextItem(string text)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
        };
    }
}
