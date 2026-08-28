using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class DeletedItemUndoBufferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clip-undo-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestTemp.Delete(_root);

    private static ClipboardHistoryItem Item(string id, string? assetPath = null) =>
        new() { Id = id, Kind = ClipboardItemKind.Text, Text = id, AssetPath = assetPath };

    [Fact]
    public void TakeRestoredHandsTheItemBackExactlyOnce()
    {
        var buffer = new DeletedItemUndoBuffer();
        buffer.Remember(Item("a"));

        Assert.True(buffer.HasItem);
        Assert.Equal("a", buffer.TakeRestored()?.Id);
        // One-item buffer, one undo: the toast promised the delete it named, nothing older.
        Assert.False(buffer.HasItem);
        Assert.Null(buffer.TakeRestored());
    }

    [Fact]
    public void ASecondDeleteReplacesTheFirst()
    {
        var buffer = new DeletedItemUndoBuffer();
        buffer.Remember(Item("first"));
        buffer.Remember(Item("second"));

        Assert.Equal("second", buffer.TakeRestored()?.Id);
        Assert.Null(buffer.TakeRestored());
    }

    [Fact]
    public void AssetBytesComeBackAfterTheStoreDeletedThem()
    {
        Directory.CreateDirectory(_root);
        var assetPath = Path.Combine(_root, "asset.png");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(assetPath, bytes);

        var buffer = new DeletedItemUndoBuffer();
        buffer.Remember(Item("a", assetPath));

        // The store's Delete removes the asset alongside the entry; the buffer must have
        // copied the bytes aside before that happened.
        File.Delete(assetPath);
        var restored = buffer.TakeRestored();

        Assert.Equal(assetPath, restored?.AssetPath);
        Assert.Equal(bytes, File.ReadAllBytes(assetPath));
    }

    [Fact]
    public void ForgetLeavesNothingToRestore()
    {
        var buffer = new DeletedItemUndoBuffer();
        buffer.Remember(Item("a"));
        buffer.Forget();

        Assert.False(buffer.HasItem);
        Assert.Null(buffer.TakeRestored());
    }

    [Fact]
    public void AMissingAssetDoesNotBlockTheDelete()
    {
        var buffer = new DeletedItemUndoBuffer();
        buffer.Remember(Item("a", Path.Combine(_root, "never-existed.png")));

        Assert.True(buffer.HasItem);
        Assert.Equal("a", buffer.TakeRestored()?.Id);
    }
}
