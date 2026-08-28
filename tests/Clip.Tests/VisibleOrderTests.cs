using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The order keyboard navigation and Ctrl+digit paste walk must be the order the rows are
/// drawn in — pinned first, then the date groups newest-first — not the store's filtered
/// order, or "the third item" acts on something other than the third row.
/// </summary>
public sealed class VisibleOrderTests
{
    private static ClipboardHistoryItem Item(string id, bool pinned = false, int pinOrder = 0, double hoursAgo = 0) =>
        new()
        {
            Id = id,
            Kind = ClipboardItemKind.Text,
            Text = id,
            IsPinned = pinned,
            PinOrder = pinOrder,
            LastCopiedAt = DateTimeOffset.Now.AddHours(-hoursAgo),
        };

    [Fact]
    public void PinnedRowsComeBeforeEverythingElse()
    {
        var filtered = new[]
        {
            Item("newest", hoursAgo: 0.1),
            Item("pinned", pinned: true),
            Item("older", hoursAgo: 1),
        };

        var order = MainWindow.VisibleOrder(filtered);

        Assert.Equal(["pinned", "newest", "older"], order.Select(item => item.Id));
    }

    [Fact]
    public void PinnedRowsFollowTheirPinOrder()
    {
        var filtered = new[]
        {
            Item("second-pin", pinned: true, pinOrder: 1),
            Item("first-pin", pinned: true, pinOrder: 0),
        };

        var order = MainWindow.VisibleOrder(filtered);

        Assert.Equal(["first-pin", "second-pin"], order.Select(item => item.Id));
    }

    [Fact]
    public void UnpinnedRowsSortNewestFirstWithinTheirGroup()
    {
        var filtered = new[]
        {
            Item("older", hoursAgo: 2),
            Item("newest", hoursAgo: 0.1),
        };

        var order = MainWindow.VisibleOrder(filtered);

        Assert.Equal(["newest", "older"], order.Select(item => item.Id));
    }
}
