using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// Concealing the palette after a deep scroll must mark the items dirty so the next open
/// rebuilds just the initial batch, while an ordinary open's row count is left alone —
/// re-rendering those rows would throw away exactly what makes a warm reopen instant.
/// </summary>
public sealed class RowReclamationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(12)]   // initial batch only
    [InlineData(48)]   // initial batch plus one deferred batch — a typical open at rest
    [InlineData(120)]  // the threshold itself: still kept
    public void OrdinaryRowCountsAreKeptAcrossConceal(int rows)
    {
        Assert.False(MainWindow.ShouldReclaimRowsOnConceal(rows));
    }

    [Theory]
    [InlineData(121)]
    [InlineData(500)]
    public void DeepScrollRowCountsAreReclaimed(int rows)
    {
        Assert.True(MainWindow.ShouldReclaimRowsOnConceal(rows));
    }
}
