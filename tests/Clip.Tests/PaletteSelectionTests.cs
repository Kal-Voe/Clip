using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

public sealed class PaletteSelectionTests
{
    private static ClipboardHistoryItem Item(string id) =>
        new() { Id = id, Kind = ClipboardItemKind.Text, Text = id };

    [Fact]
    public void SelectionStillVisibleIsKept()
    {
        var visible = new[] { Item("a"), Item("b"), Item("c") };

        var result = PaletteSelection.Reconcile("b", visible);

        Assert.Equal("b", result?.Id);
    }

    [Fact]
    public void SelectionFilteredOutFallsToFirstVisible()
    {
        // The search-narrows-the-list case: the stale selection must not survive, or Enter
        // pastes an off-screen item while the preview shows it as if it matched.
        var visible = new[] { Item("x"), Item("y") };

        var result = PaletteSelection.Reconcile("gone", visible);

        Assert.Equal("x", result?.Id);
    }

    [Fact]
    public void EmptyListClearsTheSelection()
    {
        var result = PaletteSelection.Reconcile("gone", []);

        Assert.Null(result);
    }
}
