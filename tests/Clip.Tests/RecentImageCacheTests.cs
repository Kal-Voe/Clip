using System.Windows.Media;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The raster caches must forget only their least recently used entry when full. They used to
/// clear themselves completely at the cap, which destroyed the neighbour prefetch mid-arrow-run:
/// every ~12 steps through a run of screenshots, every image decoded from disk again.
/// </summary>
public sealed class RecentImageCacheTests
{
    private static ImageSource NewSource() => new DrawingImage();

    [Fact]
    public void RememberPastCapacityEvictsOnlyTheOldest()
    {
        var cache = new RecentImageCache(3);
        var a = NewSource();
        cache.Remember("a", a);
        cache.Remember("b", NewSource());
        cache.Remember("c", NewSource());
        cache.Remember("d", NewSource());

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void TryGetRefreshesRecencySoAReadEntrySurvivesEviction()
    {
        var cache = new RecentImageCache(3);
        var a = NewSource();
        cache.Remember("a", a);
        cache.Remember("b", NewSource());
        cache.Remember("c", NewSource());

        // Reading "a" makes "b" the oldest, so the next insert must evict "b", not "a".
        Assert.True(cache.TryGet("a", out var read));
        Assert.Same(a, read);

        cache.Remember("d", NewSource());

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void RememberingAnExistingKeyReplacesWithoutEvicting()
    {
        var cache = new RecentImageCache(2);
        cache.Remember("a", NewSource());
        cache.Remember("b", NewSource());

        var replacement = NewSource();
        cache.Remember("a", replacement);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("a", out var read));
        Assert.Same(replacement, read);
        Assert.True(cache.TryGet("b", out _));
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        var cache = new RecentImageCache(2);
        cache.Remember("a", NewSource());

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));

        // Cleared, not broken: the recency list restarts empty too, so eviction still works.
        cache.Remember("b", NewSource());
        cache.Remember("c", NewSource());
        cache.Remember("d", NewSource());
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("b", out _));
    }
}
