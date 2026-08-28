using Clip.Core;

namespace Clip.Tests;

/// <summary>
/// Adversarial history.json contents beyond the plain-truncation cases in
/// ClipboardHistoryStoreRecoveryTests: valid JSON of the wrong shape, a UTF-8 BOM, a literal
/// JSON null, whitespace, 100MB of garbage, colliding quarantine backups, and a save that
/// fails against a read-only file. In every case the store must not throw out of a load, and
/// the very next capture must land.
/// </summary>
public sealed class StoreCorruptionEdgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"Items\": [] }")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("   \r\n\t  ")]
    public void WrongShapeJsonIsQuarantinedAndCaptureKeepsWorking(string content)
    {
        var seeded = new ClipboardHistoryStore(_root);
        seeded.AddOrUpdate(TextItem("seeded before wrong shape"));
        File.WriteAllText(seeded.HistoryFilePath, content);

        var store = new ClipboardHistoryStore(_root);
        var captured = store.AddOrUpdate(TextItem("captured after wrong shape"));

        // Wrong-shape content is corruption like any other: quarantined, never silently
        // treated as an empty history. "null" in particular used to deserialize cleanly to
        // null and wipe everything without a trace.
        Assert.NotNull(store.QuarantinedHistoryPath);
        var items = store.QueryItems();
        Assert.Contains(items, item => item.Id == captured.Id);
        Assert.Contains(items, item => item.Text == "seeded before wrong shape");
    }

    [Fact]
    public void WrongShapeJsonRecoversOnTheNoRetainCapturePath()
    {
        var seeded = new ClipboardHistoryStore(_root);
        seeded.AddOrUpdate(TextItem("seeded before wrong shape"));
        File.WriteAllText(seeded.HistoryFilePath, "{ \"not\": \"an array\" }");

        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var captured = store.AddOrUpdate(TextItem("captured on no-retain store"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        var items = store.QueryItems();
        Assert.Contains(items, item => item.Id == captured.Id);
        Assert.Contains(items, item => item.Preview == "seeded before wrong shape");
    }

    [Fact]
    public void BomPrefixedHistoryIsQuarantinedAndRebuilt()
    {
        // A hand edit in Notepad prepends a UTF-8 BOM, which Utf8JsonReader rejects outright.
        // That must route through the same quarantine-and-rebuild as any other corruption.
        var seeded = new ClipboardHistoryStore(_root);
        seeded.AddOrUpdate(TextItem("survives a BOM"));
        var json = File.ReadAllBytes(seeded.HistoryFilePath);
        File.WriteAllBytes(seeded.HistoryFilePath, [0xEF, 0xBB, 0xBF, .. json]);

        var store = new ClipboardHistoryStore(_root);
        var items = store.QueryItems();

        Assert.NotNull(store.QuarantinedHistoryPath);
        Assert.Contains(items, item => item.Text == "survives a BOM");

        var captured = store.AddOrUpdate(TextItem("captured after BOM"));
        Assert.Contains(store.QueryItems(), item => item.Id == captured.Id);
    }

    [Fact]
    public void HundredMegabytesOfGarbageRecovers()
    {
        var seeded = new ClipboardHistoryStore(_root);
        seeded.AddOrUpdate(TextItem("survives the garbage flood"));
        var garbage = new byte[100 * 1024 * 1024];
        Array.Fill(garbage, (byte)'x');
        File.WriteAllBytes(seeded.HistoryFilePath, garbage);

        var store = new ClipboardHistoryStore(_root);
        var captured = store.AddOrUpdate(TextItem("captured after the flood"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        var items = store.QueryItems();
        Assert.Contains(items, item => item.Id == captured.Id);
        Assert.Contains(items, item => item.Text == "survives the garbage flood");
    }

    [Fact]
    public void RepeatedQuarantinesKeepEveryBackupDistinct()
    {
        // Three corruption cycles back to back — fast enough that timestamps can collide down
        // to the millisecond. Each quarantine must still land in its own backup file with the
        // original corrupt payload intact, never Move onto an existing backup and lose it.
        var store = new ClipboardHistoryStore(_root);
        store.AddOrUpdate(TextItem("anchor item"));

        var backups = new List<string>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            File.WriteAllText(store.HistoryFilePath, $"{{ corrupt payload {cycle}");
            var reloaded = new ClipboardHistoryStore(_root);
            reloaded.QueryItems();

            Assert.NotNull(reloaded.QuarantinedHistoryPath);
            Assert.True(File.Exists(reloaded.QuarantinedHistoryPath));
            Assert.Equal($"{{ corrupt payload {cycle}", File.ReadAllText(reloaded.QuarantinedHistoryPath!));
            backups.Add(reloaded.QuarantinedHistoryPath!);
        }

        Assert.Equal(3, backups.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var survivor = new ClipboardHistoryStore(_root);
        var captured = survivor.AddOrUpdate(TextItem("captured after three quarantines"));
        Assert.Contains(survivor.QueryItems(), item => item.Id == captured.Id);
    }

    [Fact]
    public void ReadOnlyHistoryFileFailsTheSaveButNotTheStore()
    {
        // Stand-in for a full disk / locked file: the atomic Move cannot replace the target.
        // The write must fail without leaving a temp file behind or wedging the store — once
        // the file is writable again the next capture has to succeed with nothing lost.
        var store = new ClipboardHistoryStore(_root);
        var seeded = store.AddOrUpdate(TextItem("seeded before lockout"));
        File.SetAttributes(store.HistoryFilePath, FileAttributes.ReadOnly);

        try
        {
            Assert.NotNull(Record.Exception(() => store.AddOrUpdate(TextItem("blocked capture"))));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.HistoryFilePath)!, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(store.HistoryFilePath, FileAttributes.Normal);
        }

        var captured = store.AddOrUpdate(TextItem("captured after lockout"));
        var persisted = new ClipboardHistoryStore(_root).QueryItems();
        Assert.Contains(persisted, item => item.Id == captured.Id);
        Assert.Contains(persisted, item => item.Id == seeded.Id);
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
}
