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

    [Fact]
    public void ReconcileWithNoSelectionLandsOnFirstVisible()
    {
        // The query-empties-then-refills case: the empty result set cleared the selection, so
        // when results come back nothing is selected and Enter has no target. Reconcile with a
        // null id must land on the first result, not leave the palette dead.
        var refilled = new[] { Item("a"), Item("b") };

        Assert.Equal("a", PaletteSelection.Reconcile(null, refilled)?.Id);
        Assert.Null(PaletteSelection.Reconcile(null, []));
    }

    [Fact]
    public void SingleItemListSurvivesEveryKindOfStep()
    {
        var only = new[] { Item("solo") };

        // Every navigation key on a one-item list must land on that item — including page and
        // whole-list jumps, which overshoot in both directions.
        Assert.Equal("solo", PaletteSelection.Step(only, "solo", 1)?.Id);
        Assert.Equal("solo", PaletteSelection.Step(only, "solo", -1)?.Id);
        Assert.Equal("solo", PaletteSelection.Step(only, "solo", PaletteSelection.PageStep)?.Id);
        Assert.Equal("solo", PaletteSelection.Step(only, "solo", -PaletteSelection.PageStep)?.Id);
        Assert.Equal("solo", PaletteSelection.Step(only, null, 1)?.Id);
        Assert.Equal("solo", PaletteSelection.Reconcile("solo", only)?.Id);
    }

    [Fact]
    public void DigitPickReturnsTheDigitThRow()
    {
        var visible = new[] { Item("a"), Item("b"), Item("c") };

        Assert.Equal("a", PaletteSelection.DigitPick(visible, 1)?.Id);
        Assert.Equal("c", PaletteSelection.DigitPick(visible, 3)?.Id);
    }

    [Fact]
    public void DigitPickBeyondTheVisibleCountDoesNothing()
    {
        // Ctrl+9 on a three-row list must not paste the nearest row — the user named a row
        // that is not on screen, and pasting anything else is worse than pasting nothing.
        var visible = new[] { Item("a"), Item("b"), Item("c") };

        Assert.Null(PaletteSelection.DigitPick(visible, 4));
        Assert.Null(PaletteSelection.DigitPick(visible, 9));
        Assert.Null(PaletteSelection.DigitPick([], 1));
    }
}
