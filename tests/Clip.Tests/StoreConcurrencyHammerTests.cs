using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// Hammers the store's public mutators from parallel tasks well past what the app produces in
/// practice. Every task works a disjoint set of items so the expected final state is exact:
/// nothing added may be dropped, nothing deleted may resurrect, and the in-memory view must
/// match what a fresh store reads back from disk.
/// </summary>
public sealed class StoreConcurrencyHammerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FiveWayHammerConvergesToTheExactExpectedState()
    {
        var store = new ClipboardHistoryStore(_root);
        var pinTargets = Enumerable.Range(0, 8).Select(index => store.AddOrUpdate(TextItem($"pin target {index}"))).ToList();
        var victims = Enumerable.Range(0, 8).Select(index => store.AddOrUpdate(TextItem($"victim {index}"))).ToList();
        var images = Enumerable.Range(0, 6).Select(index => store.AddOrUpdate(ImageItem($"hammer-hash-{index}"))).ToList();

        var adderA = Task.Run(() =>
        {
            for (var index = 0; index < 20; index++)
            {
                store.AddOrUpdate(TextItem($"adder A {index}"));
            }
        });
        var adderB = Task.Run(() =>
        {
            for (var index = 0; index < 20; index++)
            {
                store.AddOrUpdate(TextItem($"adder B {index}"));
            }
        });
        var pinner = Task.Run(() =>
        {
            foreach (var target in pinTargets)
            {
                Assert.True(store.SetPinned(target.Id, true));
            }
        });
        var deleter = Task.Run(() =>
        {
            foreach (var victim in victims)
            {
                Assert.True(store.Delete(victim.Id));
            }
        });
        var ocr = Task.Run(() =>
        {
            foreach (var image in images)
            {
                Assert.Equal(1, store.SetOcrText(new Dictionary<string, string?> { [image.Id] = $"ocr {image.Id}" }));
            }
        });
        await Task.WhenAll(adderA, adderB, pinner, deleter, ocr);

        var live = store.QueryItems();
        var persisted = new ClipboardHistoryStore(_root).QueryItems();

        foreach (var view in new[] { live, persisted })
        {
            for (var index = 0; index < 20; index++)
            {
                Assert.Contains(view, item => item.Text == $"adder A {index}");
                Assert.Contains(view, item => item.Text == $"adder B {index}");
            }

            foreach (var victim in victims)
            {
                Assert.DoesNotContain(view, item => item.Id == victim.Id);
            }

            foreach (var image in images)
            {
                Assert.Equal($"ocr {image.Id}", view.Single(item => item.Id == image.Id).OcrText);
            }

            var pins = view.Where(item => item.IsPinned).ToList();
            Assert.Equal(pinTargets.Count, pins.Count);
            // Pin orders are assigned as max+1 under the store lock, so racing pins must still
            // come out strictly unique — a duplicate here means two pins read the same max.
            Assert.Equal(pins.Count, pins.Select(item => item.PinOrder).Distinct().Count());
            Assert.All(pins, item => Assert.True(item.PinOrder > 0));
        }

        // The cached view and the on-disk state must be the same history, item for item.
        Assert.Equal(
            live.Select(item => item.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            persisted.Select(item => item.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.HistoryFilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task DeleteRacingPinNeverResurrectsTheItem()
    {
        var store = new ClipboardHistoryStore(_root);
        var targets = Enumerable.Range(0, 12).Select(index => store.AddOrUpdate(TextItem($"race target {index}"))).ToList();

        // Delete and SetPinned race on the same id. Whichever order the lock serializes them
        // in, the delete always runs to completion, so the item must be gone — a pinned ghost
        // coming back from a stale snapshot is exactly the resurrection bug this guards.
        var tasks = new List<Task>();
        foreach (var target in targets)
        {
            tasks.Add(Task.Run(() => store.Delete(target.Id)));
            tasks.Add(Task.Run(() => store.SetPinned(target.Id, true)));
        }

        await Task.WhenAll(tasks);

        var persisted = new ClipboardHistoryStore(_root).QueryItems();
        foreach (var target in targets)
        {
            Assert.DoesNotContain(persisted, item => item.Id == target.Id);
        }
    }

    public void Dispose()
    {
        TestTemp.Delete(_root);
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
