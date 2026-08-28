using Clip.Core;
using System.Text.Json;

namespace Clip.Tests;

/// <summary>
/// Covers the store's crash-resilience: a corrupted/truncated/empty history.json must be
/// quarantined and rebuilt from the per-asset sidecars instead of silently killing capture,
/// and concurrent mutators (capture, the OCR worker, UI actions) must never lose each
/// other's writes.
/// </summary>
public sealed class ClipboardHistoryStoreRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CorruptHistoryIsQuarantinedAndRebuiltFromSidecars()
    {
        var store = new ClipboardHistoryStore(_root);
        var saved = store.AddOrUpdate(TextItem("survives corruption"));
        File.WriteAllText(store.HistoryFilePath, "{ this is not json");

        var loaded = new ClipboardHistoryStore(_root);
        var items = loaded.QueryItems().ToList();

        Assert.NotNull(loaded.QuarantinedHistoryPath);
        Assert.StartsWith(loaded.HistoryFilePath + ".corrupt-", loaded.QuarantinedHistoryPath);
        Assert.True(File.Exists(loaded.QuarantinedHistoryPath));
        Assert.Equal("{ this is not json", File.ReadAllText(loaded.QuarantinedHistoryPath!));

        var item = Assert.Single(items);
        Assert.Equal(saved.Id, item.Id);
        Assert.Equal("survives corruption", item.Text);

        // The forced save after recovery must leave a history.json that parses again.
        Assert.NotNull(JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllBytes(loaded.HistoryFilePath)));
    }

    [Fact]
    public void TruncatedHistoryRecoversAndCaptureKeepsWorking()
    {
        var store = new ClipboardHistoryStore(_root);
        store.AddOrUpdate(TextItem("first item"));
        var json = File.ReadAllText(store.HistoryFilePath);
        File.WriteAllText(store.HistoryFilePath, json[..(json.Length / 2)]);

        var loaded = new ClipboardHistoryStore(_root);
        var captured = loaded.AddOrUpdate(TextItem("captured after recovery"));

        Assert.NotNull(loaded.QuarantinedHistoryPath);
        var items = loaded.QueryItems();
        Assert.Contains(items, item => item.Id == captured.Id);
        Assert.Contains(items, item => item.Text == "first item");
    }

    [Fact]
    public void EmptyHistoryFileRecoversAndCaptureKeepsWorking()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Clipboard History"));
        File.WriteAllText(Path.Combine(_root, "Clipboard History", "history.json"), string.Empty);

        var store = new ClipboardHistoryStore(_root);
        var captured = store.AddOrUpdate(TextItem("captured after empty file"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        Assert.Equal(captured.Id, Assert.Single(store.QueryItems()).Id);
    }

    [Fact]
    public void NoRetainCaptureRecoversFromCorruptHistory()
    {
        var seeded = new ClipboardHistoryStore(_root);
        seeded.AddOrUpdate(TextItem("seeded before corruption"));
        File.WriteAllText(seeded.HistoryFilePath, "[ { \"Id\": \"truncated");

        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var captured = store.AddOrUpdate(TextItem("captured on no-retain store"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        var items = store.QueryItems();
        Assert.Contains(items, item => item.Id == captured.Id);
        Assert.Contains(items, item => item.Preview == "seeded before corruption");
    }

    [Fact]
    public async Task ConcurrentMutatorsDoNotLoseWrites()
    {
        var store = new ClipboardHistoryStore(_root);
        var images = Enumerable.Range(0, 8).Select(index => store.AddOrUpdate(ImageItem($"hash-{index}"))).ToList();
        var victims = Enumerable.Range(0, 8).Select(index => store.AddOrUpdate(TextItem($"victim {index}"))).ToList();

        // The Shell's three writers: capture on the thread pool, the OCR worker, and UI actions.
        // Before mutators held one lock across their whole read+mutate+save, any of these could
        // save a stale snapshot over another's items.
        var capture = Task.Run(() =>
        {
            for (var index = 0; index < 16; index++)
            {
                store.AddOrUpdate(TextItem($"racing add {index}"));
            }
        });
        var ocr = Task.Run(() =>
        {
            foreach (var image in images)
            {
                store.SetOcrText(new Dictionary<string, string?> { [image.Id] = $"ocr for {image.Id}" });
            }
        });
        var actions = Task.Run(() =>
        {
            foreach (var victim in victims)
            {
                store.Delete(victim.Id);
            }
        });
        await Task.WhenAll(capture, ocr, actions);

        // Assert against a fresh store so the disk state is what is checked, not the cache.
        var persisted = new ClipboardHistoryStore(_root).QueryItems();

        for (var index = 0; index < 16; index++)
        {
            Assert.Contains(persisted, item => item.Text == $"racing add {index}");
        }

        foreach (var image in images)
        {
            Assert.Equal($"ocr for {image.Id}", persisted.Single(item => item.Id == image.Id).OcrText);
        }

        foreach (var victim in victims)
        {
            Assert.DoesNotContain(persisted, item => item.Id == victim.Id);
        }

        // The atomic-write pattern must clean up after itself.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.HistoryFilePath)!, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestTemp.Delete(_root);
        }
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

    private static ClipboardHistoryItem ImageItem(string hash)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            ContentHash = hash,
            Preview = "Image 10 x 10",
            ImageWidth = 10,
            ImageHeight = 10,
        };
    }
}
