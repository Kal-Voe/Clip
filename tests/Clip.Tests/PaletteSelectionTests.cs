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

    [Fact]
    public void StepMovesThroughTheVisibleOrder()
    {
        var visible = new[] { Item("a"), Item("b"), Item("c") };

        Assert.Equal("c", PaletteSelection.Step(visible, "b", 1)?.Id);
        Assert.Equal("a", PaletteSelection.Step(visible, "b", -1)?.Id);
    }

    [Fact]
    public void StepClampsAtBothEnds()
    {
        var visible = new[] { Item("a"), Item("b"), Item("c") };

        Assert.Equal("a", PaletteSelection.Step(visible, "a", -1)?.Id);
        Assert.Equal("c", PaletteSelection.Step(visible, "c", 1)?.Id);
        // PageUp/PageDown overshooting a short list must land on the ends, not throw.
        Assert.Equal("a", PaletteSelection.Step(visible, "b", -PaletteSelection.PageStep)?.Id);
        Assert.Equal("c", PaletteSelection.Step(visible, "b", PaletteSelection.PageStep)?.Id);
    }

    [Fact]
    public void HomeAndEndAreDeltasOfTheWholeList()
    {
        var visible = new[] { Item("a"), Item("b"), Item("c") };

        Assert.Equal("a", PaletteSelection.Step(visible, "c", -visible.Length)?.Id);
        Assert.Equal("c", PaletteSelection.Step(visible, "a", visible.Length)?.Id);
    }

    [Fact]
    public void StepWithNoSelectionStartsAtTheTop()
    {
        var visible = new[] { Item("a"), Item("b") };

        // Arrowing into an unselected list should start at the top, whichever way was pressed.
        Assert.Equal("a", PaletteSelection.Step(visible, null, 1)?.Id);
        Assert.Equal("a", PaletteSelection.Step(visible, "gone", -1)?.Id);
    }

    [Fact]
    public void StepOnAnEmptyListReturnsNull()
    {
        Assert.Null(PaletteSelection.Step([], "a", 1));
    }
}
