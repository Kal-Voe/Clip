using Clip.Core;
using System.Text.Json;

namespace Clip.Tests;

public sealed class ClipboardHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
    private readonly ClipboardHistoryStore _store;

    public ClipboardHistoryStoreTests()
    {
        _store = new ClipboardHistoryStore(_root);
    }

    [Fact]
    public void QueryItemsReturnsPinnedItemsFirstInPinOrder()
    {
        var first = _store.AddOrUpdate(TextItem("first"));
        var second = _store.AddOrUpdate(TextItem("second"));
        var third = _store.AddOrUpdate(TextItem("third"));

        _store.SetPinned(third.Id, true);
        _store.SetPinned(first.Id, true);

        var items = _store.QueryItems().ToList();

        Assert.Equal([third.Id, first.Id, second.Id], items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void QueryItemsFiltersByPreviewOrText()
    {
        _store.AddOrUpdate(TextItem("alpha invoice"));
        _store.AddOrUpdate(TextItem("beta proposal"));

        var items = _store.QueryItems("invoice").ToList();

        Assert.Single(items);
        Assert.Equal("alpha invoice", items[0].Text);
    }

    [Fact]
    public void QueryItemsMatchesEveryWordAcrossDifferentFields()
    {
        var item = _store.AddOrUpdate(TextItem("quarterly numbers attached"));
        _store.Rename(item.Id, "invoice");
        _store.AddOrUpdate(TextItem("invoice reminder"));

        // "quarterly" lives in the text, "invoice" only in the custom title - a
        // contiguous-substring match would find nothing.
        var items = _store.QueryItems("invoice quarterly").ToList();

        Assert.Single(items);
        Assert.Equal(item.Id, items[0].Id);
    }

    [Fact]
    public void QueryItemsMultiWordIsOrderIndependent()
    {
        _store.AddOrUpdate(TextItem("alpha invoice pdf attached"));

        Assert.Single(_store.QueryItems("pdf invoice"));
        Assert.Single(_store.QueryItems("invoice pdf"));
        Assert.Empty(_store.QueryItems("invoice missing"));
    }

    [Fact]
    public void QueryItemsMultiWordIgnoresExtraWhitespace()
    {
        _store.AddOrUpdate(TextItem("alpha invoice pdf"));

        Assert.Single(_store.QueryItems("  invoice \t pdf  "));
    }

    [Fact]
    public void QueryItemsMultiWordMatchesUnicodeTokens()
    {
        _store.AddOrUpdate(TextItem("Привет aus der Straße"));

        Assert.Single(_store.QueryItems("straße привет"));
    }

    [Fact]
    public void QueryItemSummariesWithLimitMatchesMultiWordQueries()
    {
        var match = TextItem("beta invoice pdf");
        var other = TextItem("beta proposal");
        _store.Save([match, other]);

        // Multi-word queries make the streaming index reader fall back to the object path;
        // both must agree with QueryItems.
        var summary = Assert.Single(_store.QueryItemSummaries("pdf invoice", 5));
        Assert.Equal(match.Id, summary.Id);

        // A trailing space must not defeat the single-word fast path either.
        var padded = Assert.Single(_store.QueryItemSummaries("invoice ", 5));
        Assert.Equal(match.Id, padded.Id);
    }

    [Fact]
    public void QueryItemSummariesKeepsSmallTextButStripsRichPayload()
    {
        var item = TextItem("alpha invoice");
        item.HtmlText = new string('h', 10_000);
        item.RtfText = new string('r', 10_000);

        _store.AddOrUpdate(item);

        var summary = Assert.Single(_store.QueryItemSummaries());
        Assert.Equal("alpha invoice", summary.Text);
        Assert.Null(summary.HtmlText);
        Assert.Null(summary.RtfText);
        Assert.Null(summary.AssetPath);
        Assert.True(summary.HasOriginalFormatting);
        Assert.True(File.Exists(_store.HistoryIndexFilePath));
        Assert.True(new FileInfo(_store.HistoryIndexFilePath).Length < new FileInfo(_store.HistoryFilePath).Length);

        var searched = Assert.Single(_store.QueryItemSummaries("invoice"));
        Assert.Equal(item.Id, searched.Id);
        Assert.Null(searched.HtmlText);
        Assert.Null(searched.RtfText);
    }

    [Fact]
    public void QueryItemSummariesCapsLargeTextPayload()
    {
        var item = TextItem(new string('a', 20_000) + "tail");

        _store.AddOrUpdate(item);

        var summary = Assert.Single(_store.QueryItemSummaries());
        Assert.NotNull(summary.Text);
        Assert.True(summary.Text!.Length < item.Text!.Length);
        Assert.Equal(16_384, summary.Text.Length);
        Assert.Equal(item.Text.Length, summary.CharacterCount);
        Assert.DoesNotContain("tail", summary.Text);
        Assert.Single(_store.QueryItems("tail"));
        Assert.Empty(_store.QueryItemSummaries("tail"));
    }

    [Fact]
    public void QueryItemSummariesWithLimitSearchesSummaryIndexBeforeReturningPage()
    {
        var older = TextItem("invoice older");
        older.LastUsedAt = DateTimeOffset.Now.AddMinutes(-10);
        var newer = TextItem("invoice newer");
        newer.LastUsedAt = DateTimeOffset.Now;
        var other = TextItem("proposal");
        other.LastUsedAt = DateTimeOffset.Now.AddMinutes(10);

        _store.Save([older, newer, other]);

        var summaries = _store.QueryItemSummaries("invoice", 1);

        var summary = Assert.Single(summaries);
        Assert.Equal(newer.Id, summary.Id);

        var caseInsensitiveSummary = Assert.Single(_store.QueryItemSummaries("INVOICE", 1));
        Assert.Equal(newer.Id, caseInsensitiveSummary.Id);
    }

    [Fact]
    public void QueryItemSummariesRebuildsOversizedExistingIndex()
    {
        var item = _store.AddOrUpdate(TextItem(new string('b', 20_000)));
        File.WriteAllText(_store.HistoryIndexFilePath, JsonSerializer.Serialize(new[] { item }));
        File.SetLastWriteTimeUtc(_store.HistoryIndexFilePath, File.GetLastWriteTimeUtc(_store.HistoryFilePath).AddSeconds(1));

        var summary = Assert.Single(_store.QueryItemSummaries());

        Assert.Equal(16_384, summary.Text?.Length);
        Assert.True(new FileInfo(_store.HistoryIndexFilePath).Length < new FileInfo(_store.HistoryFilePath).Length);
    }

    [Fact]
    public void QueryItemSummariesCompactsExistingVerboseIndex()
    {
        var item = _store.AddOrUpdate(TextItem("compact invoice"));
        item.SourceApplicationPath = @"C:\Program Files\Test App\app.exe";
        var verboseOptions = new JsonSerializerOptions(JsonSerializerDefaults.General) { WriteIndented = true };
        File.WriteAllText(_store.HistoryIndexFilePath, JsonSerializer.Serialize(new[] { item }, verboseOptions));
        File.SetLastWriteTimeUtc(_store.HistoryIndexFilePath, File.GetLastWriteTimeUtc(_store.HistoryFilePath).AddSeconds(1));

        var loaded = new ClipboardHistoryStore(_root);
        var summary = Assert.Single(loaded.QueryItemSummaries());
        var compactIndex = File.ReadAllText(loaded.HistoryIndexFilePath);

        Assert.Equal(item.Id, summary.Id);
        Assert.DoesNotContain('\n', compactIndex);
        Assert.DoesNotContain("\"HtmlText\"", compactIndex);
        Assert.DoesNotContain("\"AssetPath\"", compactIndex);

        // The source path is deliberately kept in the summary index: the list needs it to draw
        // the source app's real icon, and it is small next to the payload fields dropped above.
        Assert.Contains("\"SourceApplicationPath\"", compactIndex);
        Assert.Equal(item.SourceApplicationPath, summary.SourceApplicationPath);
    }

    [Fact]
    public void NoRetainStoreStillPersistsAndQueriesItems()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var first = store.AddOrUpdate(TextItem("first no retain"));
        var second = store.AddOrUpdate(TextItem("second no retain"));

        var summaries = store.QueryItemSummaries("second").ToList();
        var full = store.GetItem(first.Id);

        Assert.Single(summaries);
        Assert.Equal(second.Id, summaries[0].Id);
        Assert.NotNull(full);
        Assert.Equal("first no retain", full!.Text);
        Assert.True(File.Exists(store.HistoryFilePath));
        Assert.True(File.Exists(store.HistoryIndexFilePath));
        Assert.True(File.Exists(store.HistoryTopIndexFilePath));
        Assert.True(File.Exists(store.HistoryKeyIndexFilePath));
    }

    [Fact]
    public void NoRetainStoreHydratesFullTextFromAsset()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var text = new string('x', 500) + "tail";
        var saved = store.AddOrUpdate(TextItem(text));
        var historyJson = File.ReadAllText(store.HistoryFilePath);

        var loaded = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false).GetItem(saved.Id);

        Assert.DoesNotContain("tail", historyJson);
        Assert.NotNull(loaded);
        Assert.Equal(text, loaded!.Text);
    }

    [Fact]
    public void NoRetainStoreHydratesRichTextFromAsset()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var item = TextItem("rich text");
        item.HtmlText = "<b>rich text</b>";
        item.RtfText = @"{\rtf1 rich text}";
        var saved = store.AddOrUpdate(item);
        var historyJson = File.ReadAllText(store.HistoryFilePath);

        var loaded = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false).GetItem(saved.Id);

        Assert.DoesNotContain("<b>rich text</b>", historyJson);
        Assert.DoesNotContain(@"{\rtf1 rich text}", historyJson);
        Assert.Equal(item.HtmlText, loaded?.HtmlText);
        Assert.Equal(item.RtfText, loaded?.RtfText);
    }

    [Fact]
    public void NoRetainStoreAppendsNewItemsWithoutRewritingHistory()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var first = store.AddOrUpdate(TextItem("first append"), maxItems: 10);
        var secondText = new string('z', 500) + "tail";
        var second = store.AddOrUpdate(TextItem(secondText), maxItems: 10);

        var historyJson = File.ReadAllBytes(store.HistoryFilePath);
        var history = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(historyJson);
        var summaries = store.QueryItemSummaries().ToList();
        var recent = store.QueryRecentItemSummaries(10).ToList();
        var loaded = store.GetItem(second.Id);

        Assert.NotNull(history);
        Assert.Equal(2, history!.Count);
        Assert.True(store.HasCurrentRecentSummaryIndex());
        Assert.Equal(second.Id, summaries[0].Id);
        Assert.Equal(first.Id, summaries[1].Id);
        Assert.Equal(second.Id, recent[0].Id);
        Assert.Equal(first.Id, recent[1].Id);
        Assert.Equal(secondText, loaded?.Text);
    }

    [Fact]
    public void NoRetainStoreKeepsTopIndexCurrentAfterAppend()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        store.AddOrUpdate(TextItem("first top index"), maxItems: 10);
        File.SetLastWriteTimeUtc(store.HistoryTopIndexFilePath, File.GetLastWriteTimeUtc(store.HistoryFilePath).AddSeconds(1));

        var second = store.AddOrUpdate(TextItem("second top index"), maxItems: 10);

        var recent = store.QueryRecentItemSummaries(1);
        Assert.True(store.HasCurrentRecentSummaryIndex());
        Assert.Equal(second.Id, Assert.Single(recent).Id);
    }

    [Fact]
    public void NoRetainStoreTrimsNewItemsToLimit()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);

        store.AddOrUpdate(TextItem("one"), maxItems: 2);
        store.AddOrUpdate(TextItem("two"), maxItems: 2);
        store.AddOrUpdate(TextItem("three"), maxItems: 2);

        var items = store.QueryItems().ToList();

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.Text == "one");
        Assert.Contains(items, item => item.Text == "two");
        Assert.Contains(items, item => item.Text == "three");
    }

    [Fact]
    public void NoRetainStoreUpdatesDuplicateWithoutApplyingHistoryTrim()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var first = store.AddOrUpdate(TextItem("same"), maxItems: 10);
        store.AddOrUpdate(TextItem("other"), maxItems: 10);

        var duplicate = store.AddOrUpdate(TextItem("same"), maxItems: 1);

        var items = store.QueryItems().ToList();
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(2, duplicate.CopyCount);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Text == "same");
        Assert.Contains(items, item => item.Text == "other");
    }

    [Fact]
    public void NoRetainStoreKeepsPinnedItemsWhenTrimming()
    {
        var store = new ClipboardHistoryStore(_root, enableLoadMaintenance: false, retainLoadedItems: false);
        var pinned = store.AddOrUpdate(TextItem("pinned"), maxItems: 10);
        store.SetPinned(pinned.Id, true);

        store.AddOrUpdate(TextItem("one"), maxItems: 1);
        store.AddOrUpdate(TextItem("two"), maxItems: 1);

        var items = store.QueryItems().ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == pinned.Id);
        Assert.Contains(items, item => item.Text == "two");
    }

    [Fact]
    public void QueryRecentItemSummariesUsesSmallTopIndex()
    {
        var now = DateTimeOffset.Now;
        var items = Enumerable.Range(0, 90)
            .Select(index =>
            {
                var item = TextItem($"item {index:D2}");
                item.LastUsedAt = now.AddSeconds(index);
                return item;
            })
            .ToList();
        _store.Save(items);

        var recent = _store.QueryRecentItemSummaries(5);
        var topIndex = JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(_store.HistoryTopIndexFilePath));

        Assert.True(_store.HasCurrentRecentSummaryIndex());
        Assert.Equal(["item 89", "item 88", "item 87", "item 86", "item 85"], recent.Select(item => item.Text).ToArray());
        Assert.NotNull(topIndex);
        Assert.InRange(topIndex!.Count, 1, 64);
    }

    [Fact]
    public void QueryRecentItemSummariesCapsTopIndexTextForFastFirstPaint()
    {
        var item = TextItem(new string('x', 5_000));
        _store.Save([item]);
        var loaded = new ClipboardHistoryStore(_root);

        var recentSummary = Assert.Single(loaded.QueryRecentItemSummaries(1));
        var topIndex = Assert.Single(JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(File.ReadAllText(loaded.HistoryTopIndexFilePath))!);
        var fullSummary = Assert.Single(new ClipboardHistoryStore(_root).QueryItemSummaries());

        Assert.Equal(5_000, fullSummary.Text?.Length);
        Assert.Equal(1_024, recentSummary.Text?.Length);
        Assert.Equal(1_024, topIndex.Text?.Length);
        Assert.Equal(5_000, topIndex.CharacterCount);
    }

    [Fact]
    public void QueryItemSummariesBackfillsMissingTopIndex()
    {
        _store.AddOrUpdate(TextItem("existing history"));
        File.Delete(_store.HistoryTopIndexFilePath);
        var loaded = new ClipboardHistoryStore(_root);

        Assert.False(loaded.HasCurrentRecentSummaryIndex());

        var summary = Assert.Single(loaded.QueryItemSummaries());

        Assert.Equal("existing history", summary.Text);
        Assert.True(loaded.HasCurrentRecentSummaryIndex());
        Assert.True(File.Exists(loaded.HistoryTopIndexFilePath));
    }

    [Fact]
    public void EditTextUpdatesPreview()
    {
        var item = _store.AddOrUpdate(TextItem("old text"));

        Assert.True(_store.EditText(item.Id, "new text"));

        var updated = _store.GetItem(item.Id);
        Assert.Equal("new text", updated?.Text);
        Assert.Equal("new text", updated?.Preview);
    }

    [Fact]
    public void RenameStoresCustomTitleWithoutChangingPayload()
    {
        var item = _store.AddOrUpdate(TextItem("original copied text"));

        Assert.True(_store.Rename(item.Id, "Invoice note"));

        var updated = _store.GetItem(item.Id);
        Assert.Equal("Invoice note", updated?.CustomTitle);
        Assert.Equal("original copied text", updated?.Text);
        Assert.Equal("original copied text", updated?.Preview);
        Assert.Equal("Invoice note.txt", Path.GetFileName(updated?.AssetPath));
        Assert.True(File.Exists(updated?.AssetPath));
    }

    [Fact]
    public void RenameBlankTitleClearsCustomTitle()
    {
        var item = _store.AddOrUpdate(TextItem("original copied text"));
        _store.Rename(item.Id, "Temporary title");

        Assert.True(_store.Rename(item.Id, " "));

        Assert.Null(_store.GetItem(item.Id)?.CustomTitle);
    }

    [Fact]
    public void QueryItemsFiltersByCustomTitle()
    {
        var item = _store.AddOrUpdate(TextItem("alpha body"));
        _store.Rename(item.Id, "job invoice");
        _store.AddOrUpdate(TextItem("beta proposal"));

        var items = _store.QueryItems("invoice").ToList();

        Assert.Single(items);
        Assert.Equal(item.Id, items[0].Id);
    }

    [Fact]
    public void AddOrUpdateUsesContentHashToAvoidImageDuplicates()
    {
        var first = _store.AddOrUpdate(ImageItem("same-hash"));
        var second = _store.AddOrUpdate(ImageItem("same-hash"));

        var items = _store.QueryItems();

        Assert.Single(items);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, items[0].CopyCount);
    }

    [Fact]
    public void CreatesClipboardContentFolders()
    {
        Assert.Equal(Path.Combine(_root, "Clipboard History", "history.json"), _store.HistoryFilePath);
        Assert.True(Directory.Exists(Path.Combine(_root, "Clipboard History", "text")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Clipboard History", "image")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Clipboard History", "links")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Clipboard History", "color")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Clipboard History", "file")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Clipboard History", "file", "pdf")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Clipboard History", "file", "excel")));
    }

    [Fact]
    public void StartupRenamesPreviousDefaultClipboardFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(root, "Clipboard");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "history.json"), "[]");

        var store = new ClipboardHistoryStore(root);

        Assert.False(Directory.Exists(previous));
        Assert.Equal(Path.Combine(root, "Clipboard History", "history.json"), store.HistoryFilePath);
        Assert.True(File.Exists(store.HistoryFilePath));
        TestTemp.Delete(root);
    }

    [Fact]
    public void StoreStartupRemovesEmptyFileCategoryFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var pdf = Path.Combine(root, "Clipboard History", "file", "pdf");
        var excel = Path.Combine(root, "Clipboard History", "file", "excel");
        Directory.CreateDirectory(pdf);
        Directory.CreateDirectory(excel);
        File.WriteAllText(Path.Combine(pdf, "invoice.pdf"), "pdf");

        _ = new ClipboardHistoryStore(root);

        Assert.True(Directory.Exists(pdf));
        Assert.False(Directory.Exists(excel));
        TestTemp.Delete(root);
    }

    [Fact]
    public void TextItemsAreSavedUnderTextFolder()
    {
        var saved = _store.AddOrUpdate(TextItem("saved text"));

        Assert.NotNull(saved.AssetPath);
        Assert.StartsWith(Path.Combine(_root, "Clipboard History", "text"), saved.AssetPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("saved text.txt", Path.GetFileName(saved.AssetPath));
        Assert.Equal("saved text", File.ReadAllText(saved.AssetPath!));
        Assert.True(File.Exists(saved.AssetPath! + ".clip.json"));
        Assert.True(File.GetAttributes(saved.AssetPath! + ".clip.json").HasFlag(FileAttributes.Hidden));
        Assert.Contains(saved.Id, File.ReadAllText(saved.AssetPath! + ".clip.json"));
        Assert.True(File.Exists(Path.Combine(_root, "Clipboard History", "history.json")));
    }

    [Fact]
    public void RenameMovesHiddenSidecarMetadata()
    {
        var item = _store.AddOrUpdate(TextItem("original copied text"));
        var originalSidecar = item.AssetPath! + ".clip.json";

        _store.Rename(item.Id, "Invoice note");
        var updated = _store.GetItem(item.Id)!;

        Assert.False(File.Exists(originalSidecar));
        Assert.True(File.Exists(updated.AssetPath! + ".clip.json"));
        Assert.Contains(item.Id, File.ReadAllText(updated.AssetPath! + ".clip.json"));
    }

    [Fact]
    public void DeleteRemovesHiddenSidecarMetadata()
    {
        var item = _store.AddOrUpdate(TextItem("temporary text"));
        var sidecar = item.AssetPath! + ".clip.json";

        Assert.True(_store.Delete(item.Id));

        Assert.False(File.Exists(item.AssetPath));
        Assert.False(File.Exists(sidecar));
    }

    [Fact]
    public void ExternalRenameWithSidecarUpdatesCustomTitleOnLoad()
    {
        var saved = _store.AddOrUpdate(TextItem("sidecar text"));
        var renamed = Path.Combine(Path.GetDirectoryName(saved.AssetPath!)!, "Sidecar renamed.txt");
        File.Move(saved.AssetPath!, renamed);
        File.Move(saved.AssetPath! + ".clip.json", renamed + ".clip.json");

        var loaded = new ClipboardHistoryStore(_root);
        var item = loaded.GetItem(saved.Id);

        Assert.Equal("Sidecar renamed", item?.CustomTitle);
        Assert.Equal(renamed, item?.AssetPath);
    }

    [Fact]
    public void DuplicateContentTouchesExistingFileInsteadOfCreatingAnother()
    {
        var saved = _store.AddOrUpdate(TextItem("saved text"));
        var path = saved.AssetPath!;
        File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-10));
        var oldWriteTime = File.GetLastWriteTime(path);

        var duplicate = _store.AddOrUpdate(TextItem("saved text"));

        Assert.Equal(saved.Id, duplicate.Id);
        Assert.Equal(path, duplicate.AssetPath);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Clipboard History", "text")).Where(path => !path.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase)));
        Assert.True(File.GetLastWriteTime(path) > oldWriteTime);
    }

    [Fact]
    public void SameDisplayNameWithDifferentContentUsesNumberedFileName()
    {
        var first = _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "first body",
            Preview = "Note",
        });
        var second = _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = "second body",
            Preview = "Note",
        });

        Assert.Equal("Note.txt", Path.GetFileName(first.AssetPath));
        Assert.Equal("Note (2).txt", Path.GetFileName(second.AssetPath));
    }

    [Fact]
    public void ExternalFileRenameUpdatesCustomTitleOnLoad()
    {
        var saved = _store.AddOrUpdate(TextItem("saved text"));
        var renamed = Path.Combine(Path.GetDirectoryName(saved.AssetPath!)!, "Renamed note.txt");
        File.Move(saved.AssetPath!, renamed);

        var loaded = new ClipboardHistoryStore(_root);
        var item = loaded.GetItem(saved.Id);

        Assert.Equal("Renamed note", item?.CustomTitle);
        Assert.Equal(renamed, item?.AssetPath);
    }

    [Fact]
    public void LinksAreSavedAsUrlFiles()
    {
        var saved = _store.AddOrUpdate(TextItem("https://example.com"));

        Assert.Equal(ClipboardItemKind.Link, saved.Kind);
        Assert.Equal(".url", Path.GetExtension(saved.AssetPath));
        Assert.Contains("URL=https://example.com", File.ReadAllText(saved.AssetPath!));
    }

    [Fact]
    public void ColorsAreSavedAsPngSwatches()
    {
        var saved = _store.AddOrUpdate(TextItem("#5FBACA"));

        Assert.Equal(ClipboardItemKind.Color, saved.Kind);
        Assert.Equal("#5FBACA.png", Path.GetFileName(saved.AssetPath));
        Assert.True(File.Exists(saved.AssetPath));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(saved.AssetPath!)[..4]);
    }

    [Fact]
    public void ChangingClipboardFolderMovesHistoryIndexForFutureSaves()
    {
        _store.AddOrUpdate(TextItem("saved text"));
        var newRoot = Path.Combine(_root, "Custom", "Clipboard History");

        _store.SetContentRootPath(newRoot);
        _store.AddOrUpdate(TextItem("second text"));

        Assert.Equal(Path.Combine(newRoot, "history.json"), _store.HistoryFilePath);
        Assert.True(File.Exists(Path.Combine(newRoot, "history.json")));
        Assert.Contains(_store.QueryItems(), item => item.Text == "saved text");
        Assert.Contains(_store.QueryItems(), item => item.Text == "second text");
        Assert.All(_store.QueryItems(), item => Assert.StartsWith(newRoot, item.AssetPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FileItemsCopyFileUnderMatchingFileCategory()
    {
        var source = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(source, "pdf");

        var saved = _store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [source],
            Preview = "invoice.pdf",
        });

        Assert.NotNull(saved.AssetPath);
        Assert.StartsWith(Path.Combine(_root, "Clipboard History", "file", "pdf"), saved.AssetPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(saved.AssetPath));
        Assert.Equal("pdf", File.ReadAllText(saved.AssetPath!));
    }

    [Fact]
    public void LoadingExistingHistoryBackfillsContentFolders()
    {
        var source = Path.Combine(_root, "invoice.pdf");
        File.WriteAllText(source, "pdf");
        var existing = new[]
        {
            TextItem("old text"),
            new ClipboardHistoryItem
            {
                Kind = ClipboardItemKind.Files,
                FilePaths = [source],
                Preview = "invoice.pdf",
            },
        };
        Directory.CreateDirectory(Path.Combine(_root, "Clipboard History"));
        File.WriteAllText(Path.Combine(_root, "Clipboard History", "history.json"), JsonSerializer.Serialize(existing));

        var loaded = new ClipboardHistoryStore(_root);
        var items = loaded.QueryItems();

        Assert.Contains(items, item => item.Kind == ClipboardItemKind.Text && item.AssetPath?.StartsWith(Path.Combine(_root, "Clipboard History", "text"), StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(items, item => item.Kind == ClipboardItemKind.Files && item.AssetPath?.StartsWith(Path.Combine(_root, "Clipboard History", "file", "pdf"), StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(Directory.Exists(Path.Combine(_root, "Clipboard History", "file", "pdf")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Clipboard History", "file", "excel")));
    }

    [Fact]
    public void AddOrUpdateTrimsUnpinnedItemsToLimit()
    {
        _store.AddOrUpdate(TextItem("one"), maxItems: 2);
        _store.AddOrUpdate(TextItem("two"), maxItems: 2);
        _store.AddOrUpdate(TextItem("three"), maxItems: 2);

        var items = _store.QueryItems().ToList();

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, item => item.Text == "one");
    }

    [Fact]
    public void HistoryLimitKeepsPinnedItems()
    {
        var pinned = _store.AddOrUpdate(TextItem("pinned"), maxItems: 10);
        _store.SetPinned(pinned.Id, true);

        _store.AddOrUpdate(TextItem("one"), maxItems: 1);
        _store.AddOrUpdate(TextItem("two"), maxItems: 1);

        var items = _store.QueryItems().ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == pinned.Id);
        Assert.Contains(items, item => item.Text == "two");
    }

    [Fact]
    public void ApplyHistoryLimitTrimsExistingItems()
    {
        _store.AddOrUpdate(TextItem("one"), maxItems: 10);
        _store.AddOrUpdate(TextItem("two"), maxItems: 10);
        _store.AddOrUpdate(TextItem("three"), maxItems: 10);

        var removed = _store.ApplyHistoryLimit(1);

        Assert.Equal(2, removed);
        Assert.Single(_store.QueryItems());
        Assert.Equal("three", _store.QueryItems()[0].Text);
    }

    [Fact]
    public void ClearHistoryCanKeepPinnedItems()
    {
        var pinned = _store.AddOrUpdate(TextItem("pinned"), maxItems: 10);
        _store.SetPinned(pinned.Id, true);
        var pinnedPath = pinned.AssetPath!;
        var first = _store.AddOrUpdate(TextItem("one"), maxItems: 10);
        var second = _store.AddOrUpdate(TextItem("two"), maxItems: 10);

        var removed = _store.ClearHistory(includePinned: false);

        var remaining = _store.QueryItems();
        Assert.Equal(2, removed);
        Assert.Single(remaining);
        Assert.Equal(pinned.Id, remaining[0].Id);
        Assert.True(File.Exists(pinnedPath));
        Assert.True(File.Exists(pinnedPath + ".clip.json"));
        Assert.False(File.Exists(first.AssetPath));
        Assert.False(File.Exists(first.AssetPath! + ".clip.json"));
        Assert.False(File.Exists(second.AssetPath));
        Assert.False(File.Exists(second.AssetPath! + ".clip.json"));
    }

    [Fact]
    public void ClearHistoryCanRemovePinnedItems()
    {
        var pinned = _store.AddOrUpdate(TextItem("pinned"), maxItems: 10);
        _store.SetPinned(pinned.Id, true);
        var pinnedPath = pinned.AssetPath!;
        var unpinned = _store.AddOrUpdate(TextItem("one"), maxItems: 10);

        var removed = _store.ClearHistory(includePinned: true);

        Assert.Equal(2, removed);
        Assert.Empty(_store.QueryItems());
        Assert.False(File.Exists(pinnedPath));
        Assert.False(File.Exists(pinnedPath + ".clip.json"));
        Assert.False(File.Exists(unpinned.AssetPath));
        Assert.False(File.Exists(unpinned.AssetPath! + ".clip.json"));
    }

    [Fact]
    public void LoadingHistoryNormalizesPowerToysColorPickerText()
    {
        var item = TextItem("3570fc");
        item.SourceApplication = "PowerToys.ColorPickerUI";
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.HistoryFilePath, JsonSerializer.Serialize(new[] { item }));

        var loaded = _store.QueryItems().Single();

        Assert.Equal(ClipboardItemKind.Color, loaded.Kind);
        Assert.Equal("#3570FC", loaded.Text);
        Assert.Equal("#3570FC", loaded.Preview);
    }

    [Fact]
    public void LoadingHistoryNormalizesOutlookSourceName()
    {
        var item = TextItem("outlook copy");
        item.SourceApplication = "OLK";
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.HistoryFilePath, JsonSerializer.Serialize(new[] { item }));

        var loaded = _store.QueryItems().Single();

        Assert.Equal("Outlook", loaded.SourceApplication);
    }

    [Fact]
    public void LoadingHistoryDoesNotRewriteLastCopiedAt()
    {
        var copiedAt = DateTimeOffset.Now.AddDays(-1);
        var item = TextItem("yesterday");
        item.FirstCopiedAt = copiedAt;
        item.LastCopiedAt = copiedAt;
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.HistoryFilePath, JsonSerializer.Serialize(new[] { item }));

        var loaded = _store.QueryItems().Single();

        Assert.Equal(copiedAt, loaded.LastCopiedAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestTemp.Delete(_root);
        }
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
}
