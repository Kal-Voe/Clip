using System.Drawing;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherPreviewHelpersCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public WatcherPreviewHelpersCoverageTests()
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
        }
    }

    [Fact]
    public void IsSupportedRequiresExistingFile()
    {
        Assert.False(StaticDocumentPreviewRenderer.IsSupported(Path.Combine(_root, "missing.docx")));
    }

    [Fact]
    public void IsSupportedRecognizesOfficeExtensionsOnly()
    {
        var docx = Path.Combine(_root, "a.docx");
        var vsdx = Path.Combine(_root, "b.VSDX");
        var txt = Path.Combine(_root, "c.txt");
        File.WriteAllText(docx, "x");
        File.WriteAllText(vsdx, "x");
        File.WriteAllText(txt, "x");

        Assert.True(StaticDocumentPreviewRenderer.IsSupported(docx));
        Assert.True(StaticDocumentPreviewRenderer.IsSupported(vsdx));
        Assert.False(StaticDocumentPreviewRenderer.IsSupported(txt));
    }

    [Fact]
    public void OfficeProcessNameMapsKnownProgIds()
    {
        Assert.Equal("WINWORD", StaticDocumentPreviewRenderer.OfficeProcessName("Word.Application"));
        Assert.Equal("EXCEL", StaticDocumentPreviewRenderer.OfficeProcessName("Excel.Application"));
        Assert.Equal("POWERPNT", StaticDocumentPreviewRenderer.OfficeProcessName("PowerPoint.Application"));
        Assert.Equal("VISIO", StaticDocumentPreviewRenderer.OfficeProcessName("Visio.Application"));
        Assert.Null(StaticDocumentPreviewRenderer.OfficeProcessName("Paint.Application"));
    }

    [Fact]
    public void CreatedNewProcessDetectsOnlyGenuinelyNewIds()
    {
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess(null, [1]));
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess([1], null));
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess([1, 2], [1, 2]));
        Assert.False(StaticDocumentPreviewRenderer.CreatedNewProcess([1, 2], [1]));
        Assert.True(StaticDocumentPreviewRenderer.CreatedNewProcess([1], [1, 2]));
        Assert.True(StaticDocumentPreviewRenderer.CreatedNewProcess([], [7]));
    }

    [Fact]
    public void TryRenderFirstPageFailsSafelyForMissingOrUnsupportedFiles()
    {
        Assert.False(StaticDocumentPreviewRenderer.TryRenderFirstPage(Path.Combine(_root, "missing.docx"), out var missingImage));
        Assert.NotNull(missingImage);
        missingImage.Dispose();

        var txt = Path.Combine(_root, "plain.txt");
        File.WriteAllText(txt, "not a document");
        Assert.False(StaticDocumentPreviewRenderer.TryRenderFirstPage(txt, out var txtImage));
        Assert.NotNull(txtImage);
        txtImage.Dispose();
    }

    [Fact]
    public void TrayIconPathPrefersFilePresentUnderBaseDirectory()
    {
        var iconDir = Path.Combine(_root, "assets", "app-icons");
        Directory.CreateDirectory(iconDir);
        var expected = Path.Combine(iconDir, "clip-tile-light.ico");
        File.WriteAllBytes(expected, [1, 2, 3]);

        Assert.Equal(expected, WatcherTrayIcon.IconPath(_root));
    }

    [Fact]
    public void TrayIconLoadOwnedIconReadsRealIconFile()
    {
        var iconDir = Path.Combine(_root, "assets", "app-icons");
        Directory.CreateDirectory(iconDir);
        File.Copy(RepoPath("assets", "app-icons", "clip-tile-light.ico"), Path.Combine(iconDir, "clip-tile-light.ico"));

        using var icon = WatcherTrayIcon.LoadOwnedIcon(_root);

        Assert.NotNull(icon);
        Assert.True(icon!.Width > 0);
    }

    [Fact]
    public void TrayIconFallsBackToSystemIconWhenAssetsAreMissing()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Null(WatcherTrayIcon.LoadOwnedIcon(empty));
        Assert.Same(SystemIcons.Application, WatcherTrayIcon.LoadIcon(empty));
    }

    [Fact]
    public void ShellIconReaderReadsIconsForRealFilesAndRejectsBogusShellPaths()
    {
        var file = Path.Combine(_root, "sample.txt");
        File.WriteAllText(file, "hello");

        using var small = ShellIconReader.TryGetIcon(file, large: false);
        using var large = ShellIconReader.TryGetIcon(file, large: true);

        Assert.NotNull(small);
        Assert.NotNull(large);
        Assert.Null(ShellIconReader.TryGetIcon("shell:ClipTestsDefinitelyNotAThing", large: false));
    }

    [Fact]
    public void StartMenuIconLookupReturnsNullForUnknownApp()
    {
        Assert.Null(StartMenuIconLookup.TryGetIcon("clip-tests-no-such-app-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void PackageLogoLookupRejectsMissingAppUserModelIds()
    {
        Assert.Null(PackageLogoLookup.TryGetIcon(null));
        Assert.Null(PackageLogoLookup.TryGetIcon(string.Empty));
        Assert.Null(PackageLogoLookup.TryGetIcon("   "));
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not find repo file.", Path.Combine(parts));
    }
}
