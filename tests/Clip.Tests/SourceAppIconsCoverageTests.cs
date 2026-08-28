using System.IO;
using System.Reflection;
using System.Windows.Media;
using Clip.Shell;

namespace Clip.Tests;

public sealed class SourceAppIconsCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public SourceAppIconsCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private static MethodInfo Private(string name) =>
        typeof(SourceAppIcons).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SourceAppIcons.{name} not found");

    private string CreateTextFile()
    {
        var path = Path.Combine(_root, "sample.txt");
        File.WriteAllText(path, "hello");
        return path;
    }

    private string CreatePngFile()
    {
        var path = Path.Combine(_root, "sample.png");
        File.WriteAllBytes(path, FaviconCacheCoverageTests.PngBytes(8, 8));
        return path;
    }

    /// <summary>Shell image extraction wants STA; xUnit runs MTA, so hop threads.</summary>
    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    [Theory]
    [InlineData(16, 1.0, 32)]
    [InlineData(16, 1.5, 48)]
    [InlineData(20, 1.0, 40)]
    [InlineData(24, 2.0, 96)]
    [InlineData(16, 0.0, 32)]
    [InlineData(200, 1.0, 256)]
    public void NativeSizeForPicksNextRungUp(int logical, double dpi, int expected)
    {
        Assert.Equal(expected, (int)Private("NativeSizeFor").Invoke(null, new object[] { logical, dpi })!);
    }

    [Fact]
    public void ParsingNameForPrefersAumid()
    {
        var name = (string?)Private("ParsingNameFor").Invoke(null, new object?[] { "Some.App!Id", null });

        Assert.Equal(@"shell:AppsFolder\Some.App!Id", name);
    }

    [Fact]
    public void ParsingNameForFallsBackToExistingExePath()
    {
        var file = CreateTextFile();

        Assert.Equal(file, (string?)Private("ParsingNameFor").Invoke(null, new object?[] { null, file }));
        Assert.Null((string?)Private("ParsingNameFor").Invoke(null, new object?[] { null, Path.Combine(_root, "gone.exe") }));
        Assert.Null((string?)Private("ParsingNameFor").Invoke(null, new object?[] { null, null }));
        Assert.Null((string?)Private("ParsingNameFor").Invoke(null, new object?[] { "", "" }));
    }

    [Fact]
    public void ResolveReturnsNullForUnresolvableIdentity()
    {
        Assert.Null(SourceAppIcons.Resolve(null, null, 16, 1.0));
    }

    [Fact]
    public void ResolveLoadsAndCachesFileIcon()
    {
        var file = CreateTextFile();

        var first = RunSta(() => SourceAppIcons.Resolve(null, file, 16, 1.0));
        Assert.NotNull(first);
        Assert.True(first!.IsFrozen);

        // Second call must come from the cache: same instance, no shell round trip needed.
        var second = SourceAppIcons.Resolve(null, file, 16, 1.0);
        Assert.Same(first, second);

        Assert.True(SourceAppIcons.TryGetCached(null, file, 16, 1.0, out var cached));
        Assert.Same(first, cached);
    }

    [Fact]
    public void TryGetCachedMissesBeforeResolveAndForBadIdentity()
    {
        Assert.False(SourceAppIcons.TryGetCached(null, null, 16, 1.0, out var icon));
        Assert.Null(icon);

        var file = CreateTextFile();
        Assert.False(SourceAppIcons.TryGetCached(null, file, 96, 1.0, out icon));
        Assert.Null(icon);
    }

    [Fact]
    public void ThumbnailReturnsNullForMissingFile()
    {
        Assert.Null(SourceAppIcons.Thumbnail(Path.Combine(_root, "gone.png"), 32, 1.0));
        Assert.Null(SourceAppIcons.Thumbnail("", 32, 1.0));
    }

    [Fact]
    public void ThumbnailRendersImageFile()
    {
        var png = CreatePngFile();

        var first = RunSta(() => SourceAppIcons.Thumbnail(png, 32, 1.0));
        Assert.NotNull(first);

        var second = SourceAppIcons.Thumbnail(png, 32, 1.0);
        Assert.Same(first, second);
    }

    [Fact]
    public void TryGetCachedThumbnailMissesBeforeExtractionAndHitsAfter()
    {
        var png = CreatePngFile();

        Assert.False(SourceAppIcons.TryGetCachedThumbnail(png, 96, 1.0, out var miss));
        Assert.Null(miss);
        Assert.False(SourceAppIcons.TryGetCachedThumbnail("", 96, 1.0, out _));

        var extracted = RunSta(() => SourceAppIcons.Thumbnail(png, 96, 1.0));
        Assert.NotNull(extracted);

        // Must come from the cache: same instance, no shell round trip needed.
        Assert.True(SourceAppIcons.TryGetCachedThumbnail(png, 96, 1.0, out var cached));
        Assert.Same(extracted, cached);
    }

    [Fact]
    public void ThumbnailAsyncInvokesCallbackOnWorker()
    {
        var png = CreatePngFile();
        using var done = new ManualResetEventSlim(false);
        ImageSource? resolved = null;

        SourceAppIcons.ThumbnailAsync(png, 48, 1.0, thumbnail =>
        {
            resolved = thumbnail;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(resolved);
    }

    [Fact]
    public void ThumbnailAsyncIgnoresEmptyPath()
    {
        using var done = new ManualResetEventSlim(false);

        SourceAppIcons.ThumbnailAsync("", 48, 1.0, _ => done.Set());

        Assert.False(done.Wait(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void ResolveAsyncInvokesCallbackOnWorker()
    {
        var file = CreateTextFile();
        using var done = new ManualResetEventSlim(false);
        ImageSource? resolved = null;

        SourceAppIcons.ResolveAsync(null, file, 16, 1.0, icon =>
        {
            resolved = icon;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(resolved);
    }

    [Fact]
    public void ResolveAsyncIgnoresUnresolvableIdentity()
    {
        using var done = new ManualResetEventSlim(false);

        SourceAppIcons.ResolveAsync(null, null, 16, 1.0, _ => done.Set());

        Assert.False(done.Wait(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void ClearEmptiesTheCache()
    {
        var file = CreateTextFile();
        var icon = RunSta(() => SourceAppIcons.Resolve(null, file, 16, 1.0));
        Assert.NotNull(icon);

        SourceAppIcons.Clear();

        Assert.False(SourceAppIcons.TryGetCached(null, file, 16, 1.0, out _));
    }
}
