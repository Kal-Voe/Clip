using Clip.Core;
using System.Text.Json;

namespace Clip.Tests;

/// <summary>
/// Coverage-focused tests for ClipboardHistoryStore: the no-retention (command surface)
/// write paths, index staleness/corruption fallbacks, the summary-index byte scanner,
/// asset backfill/reconcile maintenance, and CRUD edge cases.
/// </summary>
public sealed class ClipboardHistoryStoreCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public ClipboardHistoryStoreCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    // ---------- command-surface store & no-retention write paths ----------

    [Fact]
    public void OpenForCommandSurfaceUsesProvidedContentRootAndStartsEmpty()
    {
        var contentRoot = Sub("surface");
        var store = ClipboardHistoryStore.OpenForCommandSurface(contentRoot);

        Assert.Equal(contentRoot, store.ContentRootPath);
        Assert.Empty(store.GetItems());

        var added = store.AddOrUpdate(TextItem("surface item"));

        var reopened = ClipboardHistoryStore.OpenForCommandSurface(contentRoot);
        Assert.NotNull(reopened.GetItem(added.Id));
    }

    [Fact]
    public void NoRetainStoreAppendsViaKeyIndexAndMergesDuplicatesPerKind()
    {
        var store = NoRetainStore(Sub("append"));

        var textA = store.AddOrUpdate(TextItem("first text"));
        store.AddOrUpdate(TextItem("second text"));
        store.AddOrUpdate(TextItem("first text"));

        var color = store.AddOrUpdate(ColorItem("#AABBCC"));
        store.AddOrUpdate(ColorItem("#AABBCC"));

        var image = store.AddOrUpdate(ImageItem("CAFEBABE01"));
        store.AddOrUpdate(ImageItem("CAFEBABE01"));

        var file = WriteFile(Sub("append-src"), "payload.txt", "payload");
        store.AddOrUpdate(FilesItem(file));
        store.AddOrUpdate(FilesItem(file));

        var items = store.GetItems();
        Assert.Equal(5, items.Count);
        Assert.Equal(2, items.Single(i => i.Id == textA.Id).CopyCount);
        Assert.Equal(2, items.Single(i => i.Id == color.Id).CopyCount);
        Assert.Equal(2, items.Single(i => i.Id == image.Id).CopyCount);
        Assert.Single(items.Where(i => i.Kind == ClipboardItemKind.Files).ToList());
    }

    [Fact]
    public void NoRetainStoreFindsDuplicateByScanningHistoryWhenIndexesAreGone()
    {
        var root = Sub("scan-dup");
        var store = NoRetainStore(root);
        var original = store.AddOrUpdate(TextItem("dup target"));
        store.AddOrUpdate(TextItem("bystander"));

        File.Delete(store.HistoryIndexFilePath);
        File.Delete(store.HistoryKeyIndexFilePath);

        store.AddOrUpdate(TextItem("dup target"));

        var items = store.GetItems();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Single(i => i.Id == original.Id).CopyCount);
    }

    [Fact]
    public void NoRetainStoreScansHistoryForTrimWhenIndexesAreGone()
    {
        var root = Sub("scan-trim");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("old one"));
        store.AddOrUpdate(TextItem("old two"));

        File.Delete(store.HistoryIndexFilePath);
        var kept = store.AddOrUpdate(TextItem("newest"), maxItems: 1);

        var items = store.GetItems();
        var single = Assert.Single(items);
        Assert.Equal(kept.Id, single.Id);
    }

    [Fact]
    public void NoRetainStoreTrimsIncomingItemWhenItIsOldest()
    {
        var root = Sub("trim-self");
        var store = NoRetainStore(root);
        var newest = store.AddOrUpdate(TextItem("stays"));

        File.Delete(store.HistoryIndexFilePath);
        var stale = TextItem("goes away");
        stale.LastUsedAt = DateTimeOffset.Now.AddHours(-2);
        store.AddOrUpdate(stale, maxItems: 1);

        var items = store.GetItems();
        var single = Assert.Single(items);
        Assert.Equal(newest.Id, single.Id);
    }

    [Fact]
    public void NoRetainStoreUsesSummaryIndexForDuplicatesAndTrimWhenKeysMissing()
    {
        var root = Sub("summary-paths");
        var store = NoRetainStore(root);
        var original = store.AddOrUpdate(TextItem("summary dup"));
        store.AddOrUpdate(TextItem("summary other"));

        // Duplicate found via the summary index (keys deleted, summary still current).
        File.Delete(store.HistoryKeyIndexFilePath);
        store.AddOrUpdate(TextItem("summary dup"));
        Assert.Equal(2, store.GetItems().Single(i => i.Id == original.Id).CopyCount);

        // Unlimited history skips trim planning entirely.
        File.Delete(store.HistoryKeyIndexFilePath);
        store.AddOrUpdate(TextItem("kept forever"), maxItems: -1);
        Assert.Equal(3, store.GetItems().Count);

        // Trim decision computed from the summary index.
        File.Delete(store.HistoryKeyIndexFilePath);
        store.AddOrUpdate(TextItem("trim trigger"), maxItems: 2);
        Assert.Equal(2, store.GetItems().Count);
    }

    [Fact]
    public void NoRetainStoreSurvivesCorruptKeyIndex()
    {
        var root = Sub("bad-keys");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("keys one"));

        File.WriteAllText(store.HistoryKeyIndexFilePath, "definitely not json");
        TouchNewerThanHistory(store, store.HistoryKeyIndexFilePath);

        store.AddOrUpdate(TextItem("keys two"));

        Assert.Equal(2, store.GetItems().Count);
    }

    [Fact]
    public void NoRetainStoreSurvivesCorruptSummaryIndex()
    {
        var root = Sub("bad-summary");
        var store = NoRetainStore(root);
        var original = store.AddOrUpdate(TextItem("summary corrupt dup"));

        File.Delete(store.HistoryKeyIndexFilePath);
        File.WriteAllText(store.HistoryIndexFilePath, "junk not json");
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);

        // Duplicate lookup falls through the corrupt summary to a raw history scan.
        store.AddOrUpdate(TextItem("summary corrupt dup"));
        Assert.Equal(2, store.GetItems().Single(i => i.Id == original.Id).CopyCount);

        // New-item trim planning also hits the corrupt summary and falls back.
        File.Delete(store.HistoryKeyIndexFilePath);
        File.WriteAllText(store.HistoryIndexFilePath, "junk not json");
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);
        store.AddOrUpdate(TextItem("fresh entry"));
        Assert.Equal(2, store.GetItems().Count);
    }

    [Fact]
    public void NoRetainStoreRecoversWhenHistoryIsNotAJsonArray()
    {
        var root = Sub("bad-history");
        var store = NoRetainStore(root);
        File.WriteAllText(store.HistoryFilePath, "{\"not\":\"an array\"}");

        // A malformed history file used to throw the capture away; now it is quarantined
        // and the capture lands anyway.
        var captured = store.AddOrUpdate(TextItem("boom"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        Assert.Contains(store.GetItems(), item => item.Id == captured.Id);
    }

    [Fact]
    public void AppendToleratesTrailingWhitespaceInHistoryFile()
    {
        var root = Sub("trailing-ws");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("padded one"));

        File.AppendAllText(store.HistoryFilePath, "   ");
        TouchIndexesNewerThanHistory(store);

        store.AddOrUpdate(TextItem("padded two"));

        Assert.Equal(2, store.GetItems().Count);
    }

    [Fact]
    public void AppendRecoversWhenHistoryTrailerIsNotAnArray()
    {
        var root = Sub("broken-trailer");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("trailer one"));

        var text = File.ReadAllText(store.HistoryFilePath);
        var lastBracket = text.LastIndexOf(']');
        File.WriteAllText(store.HistoryFilePath, text.Remove(lastBracket, 1).Insert(lastBracket, " "));
        TouchIndexesNewerThanHistory(store);

        // A broken trailer used to abort the append; now the file is quarantined and the
        // new capture still lands. (Prior-item rebuild from sidecars is covered
        // deterministically in ClipboardHistoryStoreRecoveryTests - the no-retain seed
        // path queues its sidecar write on the thread pool, so asserting it here races.)
        var captured = store.AddOrUpdate(TextItem("trailer two"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        Assert.Contains(store.GetItems(), item => item.Id == captured.Id);
    }

    [Fact]
    public void AppendRecoversWhenHistoryIsAllWhitespace()
    {
        var root = Sub("empty-trailer");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("blank one"));

        File.WriteAllText(store.HistoryFilePath, "      ");
        TouchIndexesNewerThanHistory(store);

        var captured = store.AddOrUpdate(TextItem("blank two"));

        Assert.NotNull(store.QuarantinedHistoryPath);
        Assert.Contains(store.GetItems(), item => item.Id == captured.Id);
    }

    // ---------- top summary index maintenance ----------

    [Fact]
    public void AppendSkipsTopIndexUpdateWhenTopIndexIsMissing()
    {
        var root = Sub("top-missing");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("top one"));

        File.Delete(store.HistoryTopIndexFilePath);
        store.AddOrUpdate(TextItem("top two"));

        Assert.Equal(2, store.QueryRecentItemSummaries(10).Count);
    }

    [Fact]
    public void AppendToleratesCorruptTopIndex()
    {
        var root = Sub("top-corrupt");
        var store = NoRetainStore(root);
        store.AddOrUpdate(TextItem("corrupt top one"));

        File.WriteAllText(store.HistoryTopIndexFilePath, "not json at all");
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);

        store.AddOrUpdate(TextItem("corrupt top two"));

        Assert.Equal(2, store.GetItems().Count);
    }

    [Fact]
    public void QueryRecentItemSummariesCompactsVerboseTopIndex()
    {
        var root = Sub("top-verbose");
        var seeded = new ClipboardHistoryStore(root);
        seeded.AddOrUpdate(TextItem("verbose one"));
        seeded.AddOrUpdate(TextItem("verbose two"));

        MakeTopIndexVerbose(seeded);

        var store = NoRetainStore(root);
        var recent = store.QueryRecentItemSummaries(5);

        Assert.Equal(2, recent.Count);
        Assert.DoesNotContain('\n', File.ReadAllText(store.HistoryTopIndexFilePath));
    }

    [Fact]
    public void QueryRecentItemSummariesFallsBackWhenTopIndexNeedsRebuild()
    {
        var root = Sub("top-rebuild");
        var seeded = new ClipboardHistoryStore(root);
        var real = seeded.AddOrUpdate(TextItem("real item"));

        var huge = "[{\"Id\":\"ghost\",\"Kind\":0,\"Preview\":\"g\",\"Text\":\"" + new string('x', 20_000) + "\"}]";
        File.WriteAllText(seeded.HistoryTopIndexFilePath, huge);
        TouchNewerThanHistory(seeded, seeded.HistoryTopIndexFilePath);

        var store = NoRetainStore(root);
        var recent = store.QueryRecentItemSummaries(5);

        var item = Assert.Single(recent);
        Assert.Equal(real.Id, item.Id);
    }

    [Fact]
    public void WarmHotIndexesCompactsVerboseTopIndexAndSurvivesCorruption()
    {
        var root = Sub("warm");
        var seeded = new ClipboardHistoryStore(root);
        seeded.AddOrUpdate(TextItem("warm one"));
        MakeTopIndexVerbose(seeded);

        var store = NoRetainStore(root);
        store.WarmHotIndexes();
        Assert.DoesNotContain('\n', File.ReadAllText(store.HistoryTopIndexFilePath));

        File.WriteAllText(store.HistoryTopIndexFilePath, "corrupt");
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);
        store.WarmHotIndexes();
    }

    // ---------- query limits & index currency surface ----------

    [Fact]
    public void ZeroLimitsReturnEmptyAndIndexCurrencyTracksHistoryFile()
    {
        var root = Sub("currency");
        var store = new ClipboardHistoryStore(root);

        Assert.Empty(store.QueryItemSummaries("anything", 0));
        Assert.Empty(store.QueryRecentItemSummaries(0));
        Assert.False(store.HasCurrentSummaryIndex());

        store.AddOrUpdate(TextItem("currency item"));
        Assert.True(store.HasCurrentSummaryIndex());
        Assert.True(store.HasCurrentRecentSummaryIndex());

        File.Delete(store.HistoryFilePath);
        Assert.True(store.HasCurrentSummaryIndex());
        Assert.True(store.HasCurrentRecentSummaryIndex());
    }

    // ---------- summary index byte scanner ----------

    [Fact]
    public void SummaryIndexQueryMatchesFilePathsExactTextAndNonAscii()
    {
        var root = Sub("scanner");
        var store = new ClipboardHistoryStore(root);
        var realFile = WriteFile(Sub("scanner-src"), "needlereport.txt", "content");
        var files = store.AddOrUpdate(FilesItem(realFile, preview: "files entry"));
        store.AddOrUpdate(TextItem("ab"));
        var exact = store.AddOrUpdate(TextItem("exactneedle"));
        var unicode = store.AddOrUpdate(TextItem("café latte"));

        File.Delete(store.HistoryTopIndexFilePath);

        var byPath = Assert.Single(store.QueryItemSummaries("needlereport", 10));
        Assert.Equal(files.Id, byPath.Id);

        var byExact = Assert.Single(store.QueryItemSummaries("exactneedle", 10));
        Assert.Equal(exact.Id, byExact.Id);

        var byUnicode = Assert.Single(store.QueryItemSummaries("café", 10));
        Assert.Equal(unicode.Id, byUnicode.Id);
    }

    [Fact]
    public void SummaryIndexQueryCompactsVerboseIndexWhileSearching()
    {
        var root = Sub("scanner-verbose");
        var store = new ClipboardHistoryStore(root);
        var match = store.AddOrUpdate(TextItem("verbose needle"));
        store.AddOrUpdate(TextItem("verbose other"));

        var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(store.HistoryIndexFilePath))!;
        File.WriteAllText(
            store.HistoryIndexFilePath,
            JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);
        File.Delete(store.HistoryTopIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 10));
        Assert.Equal(match.Id, found.Id);
        Assert.DoesNotContain('\n', File.ReadAllText(store.HistoryIndexFilePath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[{\"Id\":\"q1\"")]
    [InlineData("[\n1]")] // newline forces the compaction path, whose deserialize then fails
    public void SummaryIndexQueryFallsBackWhenIndexBytesAreUnusable(string indexJson)
    {
        var root = Sub("scanner-bad-" + indexJson.Length);
        var store = new ClipboardHistoryStore(root);
        var match = store.AddOrUpdate(TextItem("fallback needle"));

        File.WriteAllText(store.HistoryIndexFilePath, indexJson);
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);
        File.Delete(store.HistoryTopIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 10));
        Assert.Equal(match.Id, found.Id);
    }

    [Fact]
    public void SummaryIndexQueryHandlesNullFilePathsProperty()
    {
        var root = Sub("scanner-nullpaths");
        var store = new ClipboardHistoryStore(root);
        store.AddOrUpdate(TextItem("placeholder"));

        File.WriteAllText(
            store.HistoryIndexFilePath,
            "[{\"Id\":\"n1\",\"Kind\":0,\"Preview\":\"needle one\",\"FilePaths\":null}," +
            "{\"Id\":\"n2\",\"Kind\":0,\"Preview\":\"needle two\"}]");
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);
        File.Delete(store.HistoryTopIndexFilePath);

        var found = store.QueryItemSummaries("needle", 10);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, i => i.Id == "n1");
        Assert.Contains(found, i => i.Id == "n2");
    }

    [Fact]
    public void SummaryIndexQuerySkipsNonStringFilePathEntries()
    {
        var root = Sub("scanner-mixedpaths");
        var store = new ClipboardHistoryStore(root);
        store.AddOrUpdate(TextItem("placeholder"));

        File.WriteAllText(
            store.HistoryIndexFilePath,
            "[{\"Id\":\"m1\",\"Kind\":2,\"Preview\":\"stub\",\"FilePaths\":[123,\"C:\\\\other\\\\path.txt\"]}," +
            "{\"Id\":\"m2\",\"Kind\":0,\"Preview\":\"needle two\"}]");
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);
        File.Delete(store.HistoryTopIndexFilePath);

        var found = Assert.Single(store.QueryItemSummaries("needle", 10));
        Assert.Equal("m2", found.Id);
    }

    [Fact]
    public void GetSummaryItemsRebuildsWhenCurrentIndexIsCorrupt()
    {
        var root = Sub("rebuild");
        var seeded = new ClipboardHistoryStore(root);
        var item = seeded.AddOrUpdate(TextItem("rebuild me"));

        File.WriteAllText(seeded.HistoryIndexFilePath, "corrupt");
        TouchNewerThanHistory(seeded, seeded.HistoryIndexFilePath);
        var retained = new ClipboardHistoryStore(root);
        var summary = Assert.Single(retained.QueryItemSummaries());
        Assert.Equal(item.Id, summary.Id);

        File.WriteAllText(seeded.HistoryIndexFilePath, "corrupt again");
        TouchNewerThanHistory(seeded, seeded.HistoryIndexFilePath);
        var noRetain = NoRetainStore(root);
        var summary2 = Assert.Single(noRetain.QueryItemSummaries());
        Assert.Equal(item.Id, summary2.Id);
    }

    // ---------- CRUD edge cases ----------

    [Fact]
    public void CrudEdgeCasesReturnFalseOrZeroForMissingItems()
    {
        var store = new ClipboardHistoryStore(Sub("crud"));
        var a = store.AddOrUpdate(TextItem("crud a"));
        var b = store.AddOrUpdate(TextItem("crud b"));

        Assert.Equal(0, store.ApplyHistoryLimit(100));
        Assert.False(store.SetPinned("missing", true));
        Assert.False(store.EditText("missing", "x"));
        Assert.False(store.Rename("missing", "x"));
        Assert.Equal(0, store.SetOcrText(new Dictionary<string, string?>()));
        Assert.Equal(0, store.SetOcrText(new Dictionary<string, string?> { ["missing"] = "text" }));

        // Pin both, reorder, then unpin.
        Assert.True(store.SetPinned(a.Id, true));
        Assert.True(store.SetPinned(b.Id, true));
        Assert.False(store.MovePinned("missing", 1));
        Assert.False(store.MovePinned(a.Id, -1));
        Assert.True(store.MovePinned(b.Id, -1));
        Assert.Equal(b.Id, store.QueryItems()[0].Id);

        Assert.True(store.SetPinned(a.Id, false));
        Assert.Equal(0, store.GetItems().Single(i => i.Id == a.Id).PinOrder);

        // Clearing unpinned only, then nothing left to clear.
        Assert.Equal(1, store.ClearHistory(includePinned: false));
        Assert.Equal(0, store.ClearHistory(includePinned: false));
    }

    [Fact]
    public void RenameTruncatesLongTitles()
    {
        var store = new ClipboardHistoryStore(Sub("rename-long"));
        var item = store.AddOrUpdate(TextItem("short"));

        Assert.True(store.Rename(item.Id, new string('t', 200)));

        var renamed = store.GetItems().Single(i => i.Id == item.Id);
        Assert.Equal(120, renamed.CustomTitle!.Length);
        Assert.True(Path.GetFileNameWithoutExtension(renamed.AssetPath!).Length <= 90);
    }

    [Fact]
    public void RenameToDotsFallsBackToDefaultAssetName()
    {
        var store = new ClipboardHistoryStore(Sub("rename-dots"));
        var item = store.AddOrUpdate(TextItem("dotty"));

        Assert.True(store.Rename(item.Id, "..."));

        var renamed = store.GetItems().Single(i => i.Id == item.Id);
        Assert.Equal("Clipboard item.txt", Path.GetFileName(renamed.AssetPath!));
    }

    [Fact]
    public void SaveAsFileWritesTextCopiesImagesAndRejectsOthers()
    {
        var root = Sub("save-as");
        var store = new ClipboardHistoryStore(root);
        var output = Sub("save-as-out");

        var text = store.AddOrUpdate(TextItem("save me"));
        var textTarget = Path.Combine(output, "saved.txt");
        Assert.Equal(textTarget, store.SaveAsFile(text.Id, textTarget));
        Assert.Equal("save me", File.ReadAllText(textTarget));

        var imageSource = WriteFile(output, "src.png", "PNGBYTES");
        var image = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            Preview = "pic",
            AssetPath = imageSource,
        };
        store.AddOrUpdate(image);
        var imageTarget = Path.Combine(output, "saved.png");
        Assert.Equal(imageTarget, store.SaveAsFile(image.Id, imageTarget));
        Assert.Equal("PNGBYTES", File.ReadAllText(imageTarget));

        Assert.Throws<InvalidOperationException>(() => store.SaveAsFile("missing", Path.Combine(output, "x.txt")));

        var files = store.AddOrUpdate(FilesItem(WriteFile(output, "f.txt", "f")));
        Assert.Throws<InvalidOperationException>(() => store.SaveAsFile(files.Id, Path.Combine(output, "y.txt")));
    }

    // ---------- asset paths, folders, categories ----------

    [Fact]
    public void NewAssetFilePathMapsKindsToContentFolders()
    {
        var store = new ClipboardHistoryStore(Sub("folders"));

        Assert.Contains($"{Path.DirectorySeparatorChar}image{Path.DirectorySeparatorChar}", store.NewAssetFilePath(".png"));
        Assert.Contains($"{Path.DirectorySeparatorChar}image{Path.DirectorySeparatorChar}", store.NewAssetFilePath(ClipboardItemKind.Image, extension: ".png"));
        Assert.Equal(store.ContentRootPath, Path.GetDirectoryName(store.NewAssetFilePath((ClipboardItemKind)99, extension: ".bin")));

        var folderSource = Sub("folders-src");
        var folderPath = store.NewAssetFilePath(ClipboardItemKind.Files, sourcePath: folderSource, extension: null);
        Assert.Contains($"{Path.DirectorySeparatorChar}file{Path.DirectorySeparatorChar}folder{Path.DirectorySeparatorChar}", folderPath);
    }

    [Theory]
    [InlineData(".pdf", "pdf")]
    [InlineData(".xlsx", "excel")]
    [InlineData(".vsdx", "visio")]
    [InlineData(".htm", "html")]
    [InlineData(".docx", "word")]
    [InlineData(".pptx", "powerpoint")]
    [InlineData(".webp", "image")]
    [InlineData(".cs", "text")]
    [InlineData(".zip", "other")]
    public void NewAssetFilePathCategorizesFileExtensions(string extension, string category)
    {
        var store = new ClipboardHistoryStore(Sub("categories" + category));

        var path = store.NewAssetFilePath(ClipboardItemKind.Files, sourcePath: @"C:\nowhere\sample" + extension, extension: extension);

        Assert.Contains($"{Path.DirectorySeparatorChar}file{Path.DirectorySeparatorChar}{category}{Path.DirectorySeparatorChar}", path);
    }

    [Fact]
    public void SetContentRootPathRejectsWhitespace()
    {
        var store = new ClipboardHistoryStore(Sub("set-root"));

        Assert.Throws<ArgumentException>(() => store.SetContentRootPath("   "));
    }

    [Fact]
    public void FilesBundleLifecycleCreatesTouchesRenamesAndDeletesFolder()
    {
        var src = Sub("bundle-src");
        var f1 = WriteFile(src, "alpha.txt", "a");
        var f2 = WriteFile(src, "beta.txt", "b");
        var store = new ClipboardHistoryStore(Sub("bundle"));

        var item = store.AddOrUpdate(FilesBundle(f1, f2));
        Assert.True(Directory.Exists(item.AssetPath));
        Assert.True(File.Exists(Path.Combine(item.AssetPath!, "original-paths.txt")));

        // Re-copying the same set merges and touches the existing bundle folder.
        store.AddOrUpdate(FilesBundle(f1, f2));
        Assert.Equal(2, store.GetItems().Single(i => i.Id == item.Id).CopyCount);

        // Renaming a multi-file bundle moves the directory (no extension involved).
        Assert.True(store.Rename(item.Id, "Bundle Stuff"));
        var renamed = store.GetItems().Single(i => i.Id == item.Id);
        Assert.Equal("Bundle Stuff", Path.GetFileName(renamed.AssetPath!));
        Assert.True(Directory.Exists(renamed.AssetPath));

        Assert.True(store.Delete(item.Id));
        Assert.False(Directory.Exists(renamed.AssetPath));
    }

    [Fact]
    public void UnknownKindNeverMatchesExistingItemsAsDuplicate()
    {
        var store = new ClipboardHistoryStore(Sub("unknown-kind"));
        store.AddOrUpdate(TextItem("existing"));

        var weird = new ClipboardHistoryItem
        {
            Kind = (ClipboardItemKind)99,
            Preview = "weird",
            ContentHash = "FFFF",
        };
        store.AddOrUpdate(weird);

        Assert.Equal(2, store.GetItems().Count);
    }

    [Fact]
    public void ExtensionOnlyFileNameFallsBackToClipboardItemBaseName()
    {
        var src = Sub("extonly-src");
        var dotFile = WriteFile(src, ".testrc", "config");
        var store = new ClipboardHistoryStore(Sub("extonly"));

        var item = store.AddOrUpdate(FilesItem(dotFile));

        Assert.Equal("Clipboard item.testrc", Path.GetFileName(item.AssetPath!));
    }

    [Fact]
    public void ImageItemsGetFriendlyDimensionNames()
    {
        var store = new ClipboardHistoryStore(Sub("img-names"));

        var noExt = Path.Combine(store.AssetPath, "raw123");
        File.WriteAllBytes(noExt, [1, 2, 3]);
        var a = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            Preview = string.Empty,
            AssetPath = noExt,
            ImageWidth = 12,
            ImageHeight = 34,
        };
        store.AddOrUpdate(a);
        Assert.Equal("Image 12 x 34.png", Path.GetFileName(a.AssetPath!));

        var withExt = Path.Combine(store.AssetPath, "raw456.jpg");
        File.WriteAllBytes(withExt, [4, 5, 6]);
        var b = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            Preview = string.Empty,
            AssetPath = withExt,
            ImageWidth = 56,
            ImageHeight = 78,
        };
        store.AddOrUpdate(b);
        Assert.Equal("Image 56 x 78.jpg", Path.GetFileName(b.AssetPath!));
    }

    [Fact]
    public void TrimIsSkippedForUnlimitedHistory()
    {
        var store = new ClipboardHistoryStore(Sub("unlimited"));
        store.AddOrUpdate(TextItem("u1"), maxItems: -1);
        store.AddOrUpdate(TextItem("u2"), maxItems: -1);
        store.AddOrUpdate(TextItem("u3"), maxItems: -1);

        Assert.Equal(3, store.GetItems().Count);
    }

    [Fact]
    public void RenameCollisionWithReservedPathPicksNumberedName()
    {
        var store = new ClipboardHistoryStore(Sub("collide"));
        var a = store.AddOrUpdate(TextItem("Same"));
        var b = store.AddOrUpdate(TextItem("other content"));

        // A's asset vanishes from disk but its path stays reserved by the item list.
        File.Delete(a.AssetPath!);
        File.Delete(a.AssetPath + ".clip.json");

        Assert.True(store.Rename(b.Id, "Same"));
        var renamed = store.GetItems().Single(i => i.Id == b.Id);
        Assert.Equal("Same (2).txt", Path.GetFileName(renamed.AssetPath!));

        // Renaming the item whose asset is gone keeps the title but not the file.
        Assert.True(store.Rename(a.Id, "New Title"));
        Assert.Equal("New Title", store.GetItems().Single(i => i.Id == a.Id).CustomTitle);
    }

    [Fact]
    public void RenameHandlesMissingAndPreexistingSidecars()
    {
        var store = new ClipboardHistoryStore(Sub("sidecars"));

        var c = store.AddOrUpdate(TextItem("sidecar gone"));
        File.Delete(c.AssetPath + ".clip.json");
        Assert.True(store.Rename(c.Id, "Moved One"));
        Assert.True(File.Exists(store.GetItems().Single(i => i.Id == c.Id).AssetPath));

        var d = store.AddOrUpdate(TextItem("sidecar target"));
        var textFolder = Path.GetDirectoryName(d.AssetPath!)!;
        File.WriteAllText(Path.Combine(textFolder, "Fresh Name.txt.clip.json"), "stale");
        Assert.True(store.Rename(d.Id, "Fresh Name"));
        var moved = store.GetItems().Single(i => i.Id == d.Id);
        Assert.Equal("Fresh Name.txt", Path.GetFileName(moved.AssetPath!));
        Assert.Contains(d.Id, File.ReadAllText(moved.AssetPath + ".clip.json"));
    }

    [Fact]
    public void DeleteWorksWhenFileCategoryRootIsGone()
    {
        var store = new ClipboardHistoryStore(Sub("no-file-root"));
        var item = store.AddOrUpdate(TextItem("cleanup me"));

        Directory.Delete(Path.Combine(store.ContentRootPath, "file"), recursive: true);

        Assert.True(store.Delete(item.Id));
    }

    // ---------- rich payload externalization & hydration ----------

    [Fact]
    public void RichPayloadIsExternalizedAndDeletedWithAsset()
    {
        var store = new ClipboardHistoryStore(Sub("rich"));
        var item = TextItem("formatted text");
        item.HtmlText = "<b>formatted</b>";
        store.AddOrUpdate(item);

        var htmlPath = item.AssetPath + ".html";
        Assert.True(File.Exists(htmlPath));

        Assert.True(store.Delete(item.Id));
        Assert.False(File.Exists(htmlPath));
    }

    [Fact]
    public void RichPayloadStaysInlineWhenAssetFolderIsUnwritable()
    {
        var root = Sub("rich-inline");
        var store = new ClipboardHistoryStore(root);
        var item = TextItem("inline rich");
        item.AssetPath = Path.Combine(root, "no-such-folder", "inline rich.txt");
        item.HtmlText = "<i>kept inline</i>";

        store.Save([item]);

        var reader = NoRetainStore(root);
        var loaded = reader.GetItem(item.Id);

        Assert.NotNull(loaded);
        Assert.Equal("<i>kept inline</i>", loaded!.HtmlText);
    }

    // ---------- load-time maintenance: normalization, backfill, migration ----------

    [Fact]
    public void LoadNormalizesBackfillsAndDeduplicatesHandcraftedHistory()
    {
        var root = Sub("load-fixups");
        Directory.CreateDirectory(root);
        var external = Sub("load-fixups-src");
        var externalPng = WriteFile(external, "shot.png", "fake png bytes");
        var realDoc = WriteFile(external, "doc.txt", "doc content");
        var sourceExe = WriteFile(external, "CoolApp.exe", "MZ");
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var badColor = new ClipboardHistoryItem { Kind = ClipboardItemKind.Color, Text = "definitely-not-color", Preview = "definitely-not-color" };
        var goodColor = new ClipboardHistoryItem { Kind = ClipboardItemKind.Color, Text = "#AABBCC", Preview = "#AABBCC" };
        var savedImage = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "saved screenshot note", Preview = "saved screenshot note", AssetPath = externalPng };
        // No content hash: exercises the dedup skip and the hash-less reconcile early-out.
        var missingImage = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, Preview = "missing", AssetPath = Path.Combine(external, "gone.png") };
        var badPathImage = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, Preview = "badpath", AssetPath = "bad\0path", ContentHash = "DEF456" };
        var filesItem = new ClipboardHistoryItem { Kind = ClipboardItemKind.Files, Preview = "doc.txt", FilePaths = [realDoc] };
        var dupA = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "dup text", Preview = "dup text" };
        var dupB = new ClipboardHistoryItem { Kind = ClipboardItemKind.Text, Text = "dup text", Preview = "dup text" };
        var repair = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "repair me",
            Preview = "repair me",
            CreatedAt = t0,
            FirstCopiedAt = t0,
            LastUsedAt = t0,
            LastCopiedAt = t0.AddDays(1),
        };
        var defaultCopied = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "default copied",
            Preview = "default copied",
            LastCopiedAt = default,
        };
        var noBaseline = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "no baseline",
            Preview = "no baseline",
            CreatedAt = default,
            FirstCopiedAt = default,
            LastCopiedAt = t0,
        };
        var sourced = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "sourced text",
            Preview = "sourced text",
            SourceApplicationPath = sourceExe,
        };

        List<ClipboardHistoryItem> handcrafted =
            [badColor, goodColor, savedImage, missingImage, badPathImage, filesItem, dupA, dupB, repair, defaultCopied, noBaseline, sourced];
        File.WriteAllText(Path.Combine(root, "history.json"), JsonSerializer.Serialize(handcrafted));

        var store = new ClipboardHistoryStore(root);
        var items = store.GetItems();

        var loadedBadColor = items.Single(i => i.Id == badColor.Id);
        Assert.EndsWith(".png", loadedBadColor.AssetPath!);
        Assert.True(File.Exists(loadedBadColor.AssetPath));

        var loadedGoodColor = items.Single(i => i.Id == goodColor.Id);
        Assert.True(File.Exists(loadedGoodColor.AssetPath));

        var loadedSavedImage = items.Single(i => i.Id == savedImage.Id);
        Assert.Equal(ClipboardItemKind.Image, loadedSavedImage.Kind);
        Assert.StartsWith(store.ContentRootPath, loadedSavedImage.AssetPath!);

        var loadedFiles = items.Single(i => i.Id == filesItem.Id);
        Assert.StartsWith(store.ContentRootPath, loadedFiles.AssetPath!);

        var dup = Assert.Single(items.Where(i => i.Text == "dup text").ToList());
        Assert.Equal(2, dup.CopyCount);

        Assert.Equal(t0, items.Single(i => i.Id == repair.Id).LastCopiedAt);
        Assert.Equal("CoolApp", items.Single(i => i.Id == sourced.Id).SourceApplication);
        Assert.Contains(items, i => i.Id == missingImage.Id);
        Assert.Contains(items, i => i.Id == badPathImage.Id);
    }

    [Fact]
    public void ReconcileFindsExternallyRenamedAssets()
    {
        var root = Sub("reconcile");
        var external = Sub("reconcile-src");
        var store = new ClipboardHistoryStore(root);

        // (a) asset + sidecar renamed together -> matched via sidecar id.
        var textItem = store.AddOrUpdate(TextItem("recon text"));
        var renamedText = Path.Combine(Path.GetDirectoryName(textItem.AssetPath!)!, "Renamed Note.txt");
        File.Move(textItem.AssetPath!, renamedText);
        File.Move(textItem.AssetPath + ".clip.json", renamedText + ".clip.json");

        // (b) link asset renamed, sidecar deleted -> matched by content hash; junk sidecar ignored.
        var linkItem = store.AddOrUpdate(TextItem("https://example.com/xyz"));
        Assert.Equal(ClipboardItemKind.Link, linkItem.Kind);
        var linkFolder = Path.GetDirectoryName(linkItem.AssetPath!)!;
        var renamedLink = Path.Combine(linkFolder, "Cool Site.url");
        File.Delete(linkItem.AssetPath + ".clip.json");
        File.Move(linkItem.AssetPath!, renamedLink);
        File.WriteAllText(Path.Combine(linkFolder, "junk.clip.json"), "garbage sidecar");

        // (d) bundle folder renamed, inner directory sidecar intact.
        var b1 = WriteFile(external, "one.txt", "1");
        var b2 = WriteFile(external, "two.txt", "2");
        var bundle = store.AddOrUpdate(FilesBundle(b1, b2));
        var renamedBundle = Path.Combine(Path.GetDirectoryName(bundle.AssetPath!)!, "My Bundle");
        Directory.Move(bundle.AssetPath!, renamedBundle);

        // (e) second bundle renamed with sidecar removed -> matched via original-paths manifest hash.
        var c1 = WriteFile(external, "three.txt", "3");
        var c2 = WriteFile(external, "four.txt", "4");
        var manifestBundle = store.AddOrUpdate(FilesBundle(c1, c2));
        File.Delete(Path.Combine(manifestBundle.AssetPath!, ".clip.json"));
        var renamedManifest = Path.Combine(Path.GetDirectoryName(manifestBundle.AssetPath!)!, "Manifest Bundle");
        Directory.Move(manifestBundle.AssetPath!, renamedManifest);

        // (f) color swatch renamed with sidecar removed -> scan runs even though nothing matches.
        var colorItem = store.AddOrUpdate(ColorItem("#112233"));
        File.Delete(colorItem.AssetPath + ".clip.json");
        File.Move(colorItem.AssetPath!, Path.Combine(Path.GetDirectoryName(colorItem.AssetPath!)!, "Different.png"));

        var reloaded = new ClipboardHistoryStore(root);
        var items = reloaded.GetItems();

        var text = items.Single(i => i.Id == textItem.Id);
        Assert.Equal(renamedText, text.AssetPath);
        Assert.Equal("Renamed Note", text.CustomTitle);

        var link = items.Single(i => i.Id == linkItem.Id);
        Assert.Equal(renamedLink, link.AssetPath);
        Assert.Equal("Cool Site", link.CustomTitle);

        var bundleLoaded = items.Single(i => i.Id == bundle.Id);
        Assert.Equal(renamedBundle, bundleLoaded.AssetPath);
        Assert.Equal("My Bundle", bundleLoaded.CustomTitle);

        var manifestLoaded = items.Single(i => i.Id == manifestBundle.Id);
        Assert.Equal(renamedManifest, manifestLoaded.AssetPath);

        Assert.Contains(items, i => i.Id == colorItem.Id);
    }

    [Fact]
    public void ReconcileFindsRenamedImageByFileHash()
    {
        var root = Sub("reconcile-img");
        var external = Sub("reconcile-img-src");
        var externalPng = WriteFile(external, "orig.png", "image-ish bytes");

        var store = new ClipboardHistoryStore(root);
        var item = new ClipboardHistoryItem { Kind = ClipboardItemKind.Image, Preview = "shot", AssetPath = externalPng };
        store.AddOrUpdate(item);

        // First reload copies the external asset into the content root.
        var second = new ClipboardHistoryStore(root);
        var copied = second.GetItems().Single(i => i.Id == item.Id);
        Assert.StartsWith(second.ContentRootPath, copied.AssetPath!);

        // Rename the managed copy and drop its sidecar; reconcile matches by hash.
        var renamed = Path.Combine(Path.GetDirectoryName(copied.AssetPath!)!, "renamed-shot.png");
        File.Delete(copied.AssetPath + ".clip.json");
        File.Move(copied.AssetPath!, renamed);

        var third = new ClipboardHistoryStore(root);
        var reconciled = third.GetItems().Single(i => i.Id == item.Id);
        Assert.Equal(renamed, reconciled.AssetPath);
    }

    [Fact]
    public void ConstructorCopiesRootHistoryFileIntoContentRoot()
    {
        var root = Sub("copy-history");
        Directory.CreateDirectory(root);
        var item = TextItem("moved into content root");
        File.WriteAllText(Path.Combine(root, "history.json"), JsonSerializer.Serialize(new List<ClipboardHistoryItem> { item }));

        var store = new ClipboardHistoryStore(root);

        Assert.True(File.Exists(store.HistoryFilePath));
        Assert.Contains(store.GetItems(), i => i.Id == item.Id);
    }

    [Fact]
    public void PreviousContentFolderMigratesEvenWithLockedFileInside()
    {
        var root = Sub("migrate-locked");
        var previous = Path.Combine(root, "Clipboard", "text");
        Directory.CreateDirectory(previous);
        var lockedPath = Path.Combine(previous, "note.txt");
        File.WriteAllText(lockedPath, "held open");

        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var store = new ClipboardHistoryStore(root);
            Assert.True(File.Exists(Path.Combine(store.ContentRootPath, "text", "note.txt")));
        }
    }

    [Fact]
    public void GetItemSkipsHydrationWhenAssetFileIsMissing()
    {
        var root = Sub("hydrate-missing");
        // The no-retention writer stores only the preview inline and keeps the full text in the asset.
        var creator = NoRetainStore(root);
        var longText = new string('z', 200) + " searchable tail";
        var item = creator.AddOrUpdate(TextItem(longText));

        File.Delete(item.AssetPath!);
        if (File.Exists(item.AssetPath + ".clip.json"))
        {
            File.Delete(item.AssetPath + ".clip.json");
        }

        var reader = NoRetainStore(root);
        var loaded = reader.GetItem(item.Id);

        Assert.NotNull(loaded);
        // Stored text is the compact preview; with the asset gone it cannot be re-hydrated.
        Assert.True(loaded!.Text!.Length < longText.Length);
        Assert.Equal(longText.Length, loaded.CharacterCount);
    }

    // ---------- helpers ----------

    private string Sub(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ClipboardHistoryStore NoRetainStore(string root) =>
        new(root, enableLoadMaintenance: false, retainLoadedItems: false);

    private static void TouchNewerThanHistory(ClipboardHistoryStore store, string path)
    {
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(store.HistoryFilePath).AddSeconds(5));
    }

    private static void TouchIndexesNewerThanHistory(ClipboardHistoryStore store)
    {
        TouchNewerThanHistory(store, store.HistoryIndexFilePath);
        TouchNewerThanHistory(store, store.HistoryKeyIndexFilePath);
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);
    }

    private static void MakeTopIndexVerbose(ClipboardHistoryStore store)
    {
        var items = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(store.HistoryTopIndexFilePath))!;
        File.WriteAllText(
            store.HistoryTopIndexFilePath,
            JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        TouchNewerThanHistory(store, store.HistoryTopIndexFilePath);
    }

    private static string WriteFile(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
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

    private static ClipboardHistoryItem ColorItem(string hex)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Color,
            Text = hex,
            Preview = hex,
        };
    }

    private static ClipboardHistoryItem ImageItem(string hash)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            ContentHash = hash,
            Preview = "Image 10 x 10",
            ImageWidth = 10,
            ImageHeight = 10,
        };
    }

    private static ClipboardHistoryItem FilesItem(string path, string? preview = null)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            Preview = preview ?? Path.GetFileName(path),
            FilePaths = [path],
        };
    }

    private static ClipboardHistoryItem FilesBundle(params string[] paths)
    {
        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            Preview = Path.GetFileName(paths[0]),
            FilePaths = [.. paths],
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                TestTemp.Delete(_root);
            }
        }
        catch (IOException)
        {
            // Background sidecar writes can race the cleanup; leaving temp files is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
