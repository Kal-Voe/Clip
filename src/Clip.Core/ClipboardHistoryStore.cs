using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Clip.Core;

public sealed class ClipboardHistoryStore
{
    private const string AppDataFolderName = "Clip";
    private const string ContentFolderName = "Clipboard History";
    private const string PreviousContentFolderName = "Clipboard";
    private static readonly string LegacyAppDataFolderName = "Ray" + "Clipboard";

    // Test seam for MigrateLegacyStore; see its use there.
    internal static readonly AsyncLocal<string?> LegacyRootOverrideForTests = new();
    private const string SidecarExtension = ".clip.json";
    private const string DirectorySidecarName = ".clip.json";
    private const string HistoryIndexFileName = "history.index.json";
    private const string HistoryTopIndexFileName = "history.top.index.json";
    private const string HistoryKeyIndexFileName = "history.keys.json";
    private const int SummaryTextCharacterLimit = 16_384;
    private const int TopSummaryTextCharacterLimit = 1_024;
    private const int TopSummaryIndexItemLimit = 64;

    // Guards every read and write of history.json and the in-memory caches. Public mutators must
    // hold it across their whole read+mutate+save: capture (thread pool), the OCR worker, and UI
    // actions all write concurrently, and taking the lock separately for the read and the save
    // let a slow writer overwrite a faster one's items with a stale snapshot. Monitor is
    // re-entrant, so the nested GetItems/Save locks inside a locked mutator are free.
    private readonly object _sync = new();
    private readonly bool _enableLoadMaintenance;
    private readonly bool _retainLoadedItems;
    private List<ClipboardHistoryItem>? _itemsCache;
    private List<ClipboardHistoryItem>? _summaryItemsCache;
    private List<ClipboardHistoryItem>? _topSummaryItemsCache;
    private DateTime _topSummaryItemsCacheStampUtc;
    private List<ClipboardHistoryKeyItem>? _keyItemsCache;
    private DateTime _keyItemsCacheStampUtc;

    public ClipboardHistoryStore(string? rootPath = null, string? contentRootPath = null, bool enableLoadMaintenance = true, bool retainLoadedItems = true)
    {
        _enableLoadMaintenance = enableLoadMaintenance;
        _retainLoadedItems = retainLoadedItems;
        RootPath = rootPath ?? ClipStoragePaths.Root;
        ContentRootPath = string.IsNullOrWhiteSpace(contentRootPath)
            ? Path.Combine(RootPath, ContentFolderName)
            : contentRootPath;
        AssetPath = Path.Combine(ContentRootPath, "image");
        HistoryFilePath = Path.Combine(ContentRootPath, "history.json");
        HistoryIndexFilePath = Path.Combine(ContentRootPath, HistoryIndexFileName);
        HistoryTopIndexFilePath = Path.Combine(ContentRootPath, HistoryTopIndexFileName);
        HistoryKeyIndexFilePath = Path.Combine(ContentRootPath, HistoryKeyIndexFileName);
        if (rootPath is null)
        {
            MigrateLegacyStore(RootPath);
        }

        MigratePreviousDefaultContentFolder();
        EnsureContentFolders();
        EnsureHistoryFileInContentRoot();
        CleanupEmptyFileCategoryFolders();
    }

    public static ClipboardHistoryStore OpenForCommandSurface(string? contentRootPath = null) => new(
        contentRootPath: string.IsNullOrWhiteSpace(contentRootPath)
            ? ClipStoragePaths.EffectiveClipboardFolderPath()
            : contentRootPath,
        enableLoadMaintenance: false,
        retainLoadedItems: false);

    public string RootPath { get; }

    public string ContentRootPath { get; private set; }

    public string AssetPath { get; private set; }

    public string HistoryFilePath { get; private set; }

    public string HistoryIndexFilePath { get; private set; }

    public string HistoryTopIndexFilePath { get; private set; }

    public string HistoryKeyIndexFilePath { get; private set; }

    /// <summary>
    /// Set when a load found history.json unreadable and quarantined it: the path the bad file
    /// was renamed to (history.json.corrupt-*), or the original path when even the rename failed
    /// and the file had to be deleted. The Shell reads this after loading to toast once; the
    /// store itself carries on from whatever the asset sidecars could rebuild.
    /// </summary>
    public string? QuarantinedHistoryPath { get; private set; }

    public IReadOnlyList<ClipboardHistoryItem> GetItems()
    {
        lock (_sync)
        {
            if (_retainLoadedItems && _itemsCache is not null)
            {
                return _itemsCache;
            }

            if (!File.Exists(HistoryFilePath))
            {
                if (_retainLoadedItems)
                {
                    _itemsCache = [];
                    _summaryItemsCache = [];
                    ClearIndexCaches();
                    return _itemsCache;
                }

                ClearIndexCaches();
                return [];
            }

            List<ClipboardHistoryItem> items;
            var changed = false;
            try
            {
                var json = File.ReadAllBytes(HistoryFilePath);
                // A literal JSON "null" deserializes cleanly to null — coalescing it to an empty
                // list here would silently discard the whole history without ever quarantining
                // the file. The store never writes "null", so treat it as the corruption it is
                // and let the recovery below rebuild from the sidecars.
                items = JsonSerializer.Deserialize(json, ClipboardHistoryJsonContext.Default.ListClipboardHistoryItem)
                    ?? throw new JsonException("history.json holds JSON null, not an item array.");
            }
            catch (JsonException)
            {
                // A crash mid-write (or a truncated/empty file) used to throw out of every load
                // and silently kill capture forever - the palette kept rendering from the summary
                // index, so everything looked fine while every save failed. Quarantine the bad
                // file and rebuild what the asset sidecars can prove; changed forces the save
                // below so history.json and the indexes agree again.
                QuarantineCorruptHistory();
                items = RebuildItemsFromSidecars();
                changed = true;
            }

            changed |= NormalizeLoadedItems(items);
            if (_enableLoadMaintenance)
            {
                changed |= ReconcileExternalAssetRenames(items);
                changed |= BackfillContentAssets(items);
                changed |= EnsureFriendlyAssetNames(items);
                changed |= EnsureSidecars(items);
                CleanupEmptyFileCategoryFolders();
            }

            if (changed)
            {
                SaveCore(items);
            }

            if (_retainLoadedItems)
            {
                _itemsCache = items;
                return _itemsCache;
            }

            return items;
        }
    }

    public ClipboardHistoryItem? GetItem(string id)
    {
        var item = GetItems().FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        HydrateTextFromAsset(item);
        return item;
    }

    public IReadOnlyList<ClipboardHistoryItem> QueryItems(string? query = null)
    {
        var items = GetItems().AsEnumerable();
        return QueryCore(items, query);
    }

    public IReadOnlyList<ClipboardHistoryItem> QueryItemSummaries(string? query = null)
    {
        return QueryCore(GetSummaryItems(), query, alreadyOrdered: true);
    }

    public IReadOnlyList<ClipboardHistoryItem> QueryItemSummaries(string? query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return QueryRecentItemSummaries(limit);
        }

        var recentMatches = TryQueryRecentSummaryIndex(query, limit);
        if (recentMatches is { Count: > 0 })
        {
            return recentMatches;
        }

        return TryQuerySummaryIndex(query, limit) ??
            QueryCore(GetSummaryItems(), query, alreadyOrdered: true).Take(limit).ToList();
    }

    public IReadOnlyList<ClipboardHistoryItem> QueryRecentItemSummaries(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        lock (_sync)
        {
            if (_retainLoadedItems && _summaryItemsCache is not null)
            {
                return _summaryItemsCache.Take(limit).ToList();
            }

            var indexItems = TryLoadCurrentTopSummaryItems(out var needsCompaction);
            if (indexItems is not null)
            {
                if (!SummaryIndexNeedsRebuild(indexItems))
                {
                    indexItems = OrderedItems(indexItems);
                    if (needsCompaction)
                    {
                        SaveTopIndexCore(indexItems);
                    }

                    return indexItems.Take(limit).ToList();
                }
            }

            return GetSummaryItems().Take(limit).ToList();
        }
    }

    public bool HasCurrentSummaryIndex()
    {
        return IsSummaryIndexCurrent();
    }

    public bool HasCurrentRecentSummaryIndex()
    {
        return IsTopSummaryIndexCurrent();
    }

    public void WarmHotIndexes()
    {
        lock (_sync)
        {
            _ = TryLoadCurrentKeyItems();
            var topItems = TryLoadCurrentTopSummaryItems(out var needsCompaction);
            if (topItems is not null && needsCompaction)
            {
                SaveTopIndexCore(OrderedItems(topItems));
            }
        }
    }

    private static IReadOnlyList<ClipboardHistoryItem> QueryCore(IEnumerable<ClipboardHistoryItem> items, string? query, bool alreadyOrdered = false)
    {
        var source = alreadyOrdered ? items : OrderedItems(items);
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(item => MatchesQuery(item, query));
        }

        return source.ToList();
    }

    private IReadOnlyList<ClipboardHistoryItem>? TryQuerySummaryIndex(string query, int limit)
    {
        lock (_sync)
        {
            if (!IsSummaryIndexCurrent())
            {
                return null;
            }

            try
            {
                var json = File.ReadAllBytes(HistoryIndexFilePath);
                if (!SummaryIndexNeedsCompaction(json))
                {
                    return QuerySummaryIndexBytes(json, query, limit);
                }

                var indexItems = JsonSerializer.Deserialize(json, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem) ?? [];
                if (SummaryIndexNeedsRebuild(indexItems))
                {
                    return null;
                }

                var compactedItems = CreateSummaryItems(indexItems);
                SaveIndexCore(compactedItems);
                var matches = new List<ClipboardHistoryItem>();
                foreach (var item in compactedItems)
                {
                    if (MatchesQuery(item, query))
                    {
                        matches.Add(item);
                    }
                }

                matches.Sort(CompareItems);
                if (matches.Count > limit)
                {
                    matches.RemoveRange(limit, matches.Count - limit);
                }

                return matches;
            }
            catch
            {
                return null;
            }
        }
    }

    private IReadOnlyList<ClipboardHistoryItem>? TryQueryRecentSummaryIndex(string query, int limit)
    {
        lock (_sync)
        {
            if (!IsTopSummaryIndexCurrent())
            {
                return null;
            }

            try
            {
                var json = File.ReadAllBytes(HistoryTopIndexFilePath);
                if (!SummaryIndexNeedsCompaction(json))
                {
                    return QuerySummaryIndexBytes(json, query, limit);
                }

                var indexItems = TryLoadCurrentTopSummaryItems(out var needsCompaction);
                if (indexItems is null || SummaryIndexNeedsRebuild(indexItems))
                {
                    return null;
                }

                indexItems = OrderedItems(indexItems);
                if (needsCompaction)
                {
                    SaveTopIndexCore(indexItems);
                }

                return QueryCore(indexItems, query, alreadyOrdered: true).Take(limit).ToList();
            }
            catch
            {
                return null;
            }
        }
    }

    private static IReadOnlyList<ClipboardHistoryItem>? QuerySummaryIndexBytes(byte[] json, string query, int limit)
    {
        // The streaming reader checks one field at a time, which cannot express "every word
        // matches somewhere in this object" without materializing it — multi-word queries return
        // null so the caller falls back to the object path, and single-word queries scan with
        // the trimmed token so both paths agree on surrounding whitespace.
        var tokens = QueryTokens(query);
        if (tokens.Length != 1)
        {
            return null;
        }

        query = tokens[0];
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return null;
            }

            var matches = new List<ClipboardHistoryItem>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return null;
                }

                var objectStart = checked((int)reader.TokenStartIndex);
                if (!SummaryObjectMatches(ref reader, query))
                {
                    continue;
                }

                var objectLength = checked((int)reader.BytesConsumed - objectStart);
                var item = JsonSerializer.Deserialize(
                    json.AsSpan(objectStart, objectLength),
                    ClipboardHistorySummaryJsonContext.Default.ClipboardHistoryItem);
                if (item is not null)
                {
                    matches.Add(item);
                }
            }

            matches.Sort(CompareItems);
            if (matches.Count > limit)
            {
                matches.RemoveRange(limit, matches.Count - limit);
            }

            return matches;
        }
        catch
        {
            return null;
        }
    }

    private static bool SummaryObjectMatches(ref Utf8JsonReader reader, string query)
    {
        var matches = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return matches;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var isSearchableText =
                reader.ValueTextEquals("Preview") ||
                reader.ValueTextEquals("CustomTitle") ||
                reader.ValueTextEquals("Text") ||
                reader.ValueTextEquals("OcrText");
            var isFilePaths = reader.ValueTextEquals("FilePaths");
            if (!reader.Read())
            {
                return matches;
            }

            if (isSearchableText)
            {
                matches |= reader.TokenType == JsonTokenType.String && ReaderStringContains(ref reader, query);
                continue;
            }

            if (isFilePaths)
            {
                matches |= FilePathsMatch(ref reader, query);
                continue;
            }

            reader.Skip();
        }

        return matches;
    }

    private static bool FilePathsMatch(ref Utf8JsonReader reader, string query)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return false;
        }

        var matches = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return matches;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                matches |= ReaderStringContains(ref reader, query);
                continue;
            }

            reader.Skip();
        }

        return matches;
    }

    private static bool ReaderStringContains(ref Utf8JsonReader reader, string query)
    {
        if (reader.ValueTextEquals(query.AsSpan()))
        {
            return true;
        }

        if (!reader.HasValueSequence && System.Text.Ascii.IsValid(query))
        {
            var value = reader.ValueSpan;
            if (query.Length > value.Length)
            {
                return false;
            }

            if (value.IndexOf((byte)'\\') < 0)
            {
                return ContainsAsciiIgnoreCase(value, query);
            }
        }

        return Contains(reader.GetString(), query);
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> value, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        var first = ToLowerAscii((byte)query[0]);
        var maxStart = value.Length - query.Length;
        for (var start = 0; start <= maxStart; start++)
        {
            if (ToLowerAscii(value[start]) != first)
            {
                continue;
            }

            var matched = true;
            for (var index = 1; index < query.Length; index++)
            {
                if (ToLowerAscii(value[start + index]) != ToLowerAscii((byte)query[index]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static bool MatchesQuery(ClipboardHistoryItem item, string query)
    {
        // Token-AND: every whitespace-separated word must match some searchable field, in any
        // order — "invoice pdf" finds an item titled "invoice" whose file path ends in ".pdf",
        // where a single contiguous-substring match would find nothing.
        foreach (var token in QueryTokens(query))
        {
            if (!MatchesToken(item, token))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesToken(ClipboardHistoryItem item, string token) =>
        Contains(item.Preview, token) ||
        Contains(item.CustomTitle, token) ||
        Contains(item.Text, token) ||
        Contains(item.OcrText, token) ||
        item.FilePaths.Any(path => Contains(path, token));

    private static string[] QueryTokens(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private List<ClipboardHistoryItem> GetSummaryItems()
    {
        lock (_sync)
        {
            if (_retainLoadedItems && _summaryItemsCache is not null)
            {
                return _summaryItemsCache;
            }

            if (IsSummaryIndexCurrent())
            {
                try
                {
                    var json = File.ReadAllBytes(HistoryIndexFilePath);
                    var indexItems = JsonSerializer.Deserialize(json, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem) ?? [];
                    if (!SummaryIndexNeedsRebuild(indexItems))
                    {
                        indexItems = OrderedItems(indexItems);
                        if (SummaryIndexNeedsCompaction(json))
                        {
                            indexItems = CreateSummaryItems(indexItems);
                            SaveIndexCore(indexItems);
                        }
                        else if (!IsTopSummaryIndexCurrent())
                        {
                            SaveTopIndexCore(indexItems);
                        }

                        if (_retainLoadedItems)
                        {
                            _summaryItemsCache = indexItems;
                            return _summaryItemsCache;
                        }

                        return indexItems;
                    }
                }
                catch
                {
                    _summaryItemsCache = null;
                }
            }

            var summaries = OrderedItems(CreateSummaryItems(GetItems()));
            SaveIndexCore(summaries);
            if (_retainLoadedItems)
            {
                _summaryItemsCache = summaries;
                return _summaryItemsCache;
            }

            return summaries;
        }
    }

    private bool IsSummaryIndexCurrent()
    {
        if (!File.Exists(HistoryIndexFilePath))
        {
            return false;
        }

        if (!File.Exists(HistoryFilePath))
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(HistoryIndexFilePath) >= File.GetLastWriteTimeUtc(HistoryFilePath);
    }

    private bool IsKeyIndexCurrent()
    {
        if (!File.Exists(HistoryKeyIndexFilePath))
        {
            return false;
        }

        if (!File.Exists(HistoryFilePath))
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(HistoryKeyIndexFilePath) >= File.GetLastWriteTimeUtc(HistoryFilePath);
    }

    private bool IsTopSummaryIndexCurrent()
    {
        if (!File.Exists(HistoryTopIndexFilePath))
        {
            return false;
        }

        if (!File.Exists(HistoryFilePath))
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(HistoryTopIndexFilePath) >= File.GetLastWriteTimeUtc(HistoryFilePath);
    }

    private static bool SummaryIndexNeedsRebuild(IEnumerable<ClipboardHistoryItem> items)
    {
        return items.Any(item => item.Text?.Length > SummaryTextCharacterLimit);
    }

    private static bool SummaryIndexNeedsCompaction(byte[] json)
    {
        return Array.IndexOf(json, (byte)'\n') >= 0 ||
            ContainsUtf8(json, "\"HtmlText\":") ||
            ContainsUtf8(json, "\"SourceApplicationPath\":");
    }

    private static bool ContainsUtf8(byte[] source, string value)
    {
        return source.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;
    }

    private static List<ClipboardHistoryItem> OrderedItems(IEnumerable<ClipboardHistoryItem> items)
    {
        var ordered = items.ToList();
        ordered.Sort(CompareItems);
        return ordered;
    }

    private static int CompareItems(ClipboardHistoryItem left, ClipboardHistoryItem right)
    {
        var pinned = right.IsPinned.CompareTo(left.IsPinned);
        if (pinned != 0)
        {
            return pinned;
        }

        var leftPinOrder = left.PinOrder == 0 ? int.MaxValue : left.PinOrder;
        var rightPinOrder = right.PinOrder == 0 ? int.MaxValue : right.PinOrder;
        var pinOrder = leftPinOrder.CompareTo(rightPinOrder);
        if (pinOrder != 0)
        {
            return pinOrder;
        }

        return right.LastUsedAt.CompareTo(left.LastUsedAt);
    }

    public ClipboardHistoryItem AddOrUpdate(ClipboardHistoryItem item, int maxItems = 500, bool refreshCopiedAt = true)
    {
        if (!_retainLoadedItems && !_enableLoadMaintenance)
        {
            return AddOrUpdateWithoutRetention(item, maxItems, refreshCopiedAt);
        }

        lock (_sync)
        {
            Enrich(item, refreshCopiedAt);
            PersistContentAsset(item);
            var items = GetItems().ToList();
            var duplicate = FindDuplicate(items, item);
            if (duplicate is not null)
            {
                var redundantAssetPath = RedundantAssetPath(item, duplicate);
                ApplyDuplicateUpdate(duplicate, item);
                TouchAsset(duplicate.AssetPath);
                WriteSidecar(duplicate);
                Save(items);
                DeleteAssetPath(redundantAssetPath);
                return duplicate;
            }

            EnsureFriendlyAssetName(item, items);
            WriteSidecar(item);
            items.Insert(0, item);
            var removed = TrimUnpinned(items, maxItems);
            Save(items);
            DeleteAssets(removed);
            return item;
        }
    }

    private ClipboardHistoryItem AddOrUpdateWithoutRetention(ClipboardHistoryItem item, int maxItems, bool refreshCopiedAt)
    {
        lock (_sync)
        {
            Enrich(item, refreshCopiedAt);
            PersistContentAsset(item);
            EnsureFriendlyAssetName(item, []);

            try
            {
                var keyItems = TryLoadCurrentKeyItems();
                if (keyItems is not null)
                {
                    var keyDuplicateId = FindDuplicateId(keyItems, item);
                    if (keyDuplicateId is null && CanAppendWithoutTrim(keyItems, item, maxItems))
                    {
                        AppendNewWithoutRetention(keyItems, item);
                        return item;
                    }
                }

                if (!File.Exists(HistoryFilePath) || new FileInfo(HistoryFilePath).Length == 0)
                {
                    SaveStoredCore([item]);
                    QueueSidecarWrite(item);
                    _itemsCache = null;
                    _summaryItemsCache = null;
                    return item;
                }

                byte[]? json = null;
                var duplicateId = keyItems is not null ? FindDuplicateId(keyItems, item) : null;
                if (keyItems is null)
                {
                    duplicateId = TryFindDuplicateIdFromSummary(item, out var summaryDuplicateLookupCurrent);
                    if (!summaryDuplicateLookupCurrent)
                    {
                        json = File.ReadAllBytes(HistoryFilePath);
                        duplicateId = FindDuplicateIdInHistory(json, item);
                    }
                }

                if (duplicateId is not null)
                {
                    json ??= File.ReadAllBytes(HistoryFilePath);
                    return UpdateDuplicateWithoutRetention(json, item, duplicateId);
                }

                var keptUnpinnedIds = TryKeptUnpinnedIdsFromSummary(item, maxItems, out var summaryTrimCurrent);
                json ??= File.ReadAllBytes(HistoryFilePath);
                return AddNewWithoutRetention(json, item, maxItems, keptUnpinnedIds, summaryTrimCurrent);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                // The append fast path and the duplicate scan both parse history.json in place; a
                // corrupt or truncated file used to throw the capture away entirely. Quarantine
                // it and rebuild from the sidecars instead - the copy that just happened is the
                // one thing that must never be lost.
                QuarantineCorruptHistory();
                var items = RebuildItemsFromSidecars();
                items.RemoveAll(existing => existing.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
                items.Insert(0, item);
                SaveStoredCore(items);
                QueueSidecarWrite(item);
                _itemsCache = null;
                _summaryItemsCache = null;
                return item;
            }
        }
    }

    private List<ClipboardHistoryKeyItem>? TryLoadCurrentKeyItems()
    {
        if (!File.Exists(HistoryFilePath) || new FileInfo(HistoryFilePath).Length == 0)
        {
            return null;
        }

        if (!IsSummaryIndexCurrent() || !IsKeyIndexCurrent())
        {
            return null;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(HistoryKeyIndexFilePath);
            if (_keyItemsCache is not null && stamp == _keyItemsCacheStampUtc)
            {
                return _keyItemsCache;
            }

            var json = File.ReadAllBytes(HistoryKeyIndexFilePath);
            _keyItemsCache = JsonSerializer.Deserialize(json, ClipboardHistoryKeyJsonContext.Default.ListClipboardHistoryKeyItem) ?? [];
            _keyItemsCacheStampUtc = stamp;
            return _keyItemsCache;
        }
        catch
        {
            _keyItemsCache = null;
            _keyItemsCacheStampUtc = default;
            return null;
        }
    }

    private static string? FindDuplicateId(IEnumerable<ClipboardHistoryKeyItem> keys, ClipboardHistoryItem item)
    {
        return keys.FirstOrDefault(existing => IsDuplicateKey(existing, item))?.Id;
    }

    private static bool IsDuplicateKey(ClipboardHistoryKeyItem existing, ClipboardHistoryItem item)
    {
        if (string.IsNullOrEmpty(existing.ContentHash) ||
            !existing.ContentHash.Equals(item.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return item.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link => existing.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link,
            ClipboardItemKind.Color => existing.Kind == ClipboardItemKind.Color,
            ClipboardItemKind.Image => existing.Kind == ClipboardItemKind.Image,
            ClipboardItemKind.Files => existing.Kind == ClipboardItemKind.Files,
            _ => false,
        };
    }

    private string? TryFindDuplicateIdFromSummary(ClipboardHistoryItem item, out bool summaryCurrent)
    {
        summaryCurrent = false;
        if (!IsSummaryIndexCurrent())
        {
            return null;
        }

        try
        {
            var json = File.ReadAllBytes(HistoryIndexFilePath);
            var summaries = JsonSerializer.Deserialize(json, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem) ?? [];
            summaryCurrent = true;
            return FindDuplicate(summaries, item)?.Id;
        }
        catch
        {
            return null;
        }
    }

    private HashSet<string>? TryKeptUnpinnedIdsFromSummary(ClipboardHistoryItem item, int maxItems, out bool summaryCurrent)
    {
        summaryCurrent = false;
        if (maxItems < 0)
        {
            return null;
        }

        if (!IsSummaryIndexCurrent())
        {
            return null;
        }

        try
        {
            var json = File.ReadAllBytes(HistoryIndexFilePath);
            var summaries = JsonSerializer.Deserialize(json, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem) ?? [];
            summaryCurrent = true;
            return summaries
                .Where(existing => !existing.IsPinned)
                .Select(existing => (existing.Id, existing.LastUsedAt))
                .Append((item.Id, item.LastUsedAt))
                .OrderByDescending(entry => entry.LastUsedAt)
                .Take(maxItems)
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindDuplicateIdInHistory(byte[] json, ClipboardHistoryItem item)
    {
        string? duplicateId = null;
        ForEachHistoryItem(json, existing =>
        {
            if (duplicateId is null && IsDuplicate(existing, item))
            {
                duplicateId = existing.Id;
            }
        });

        return duplicateId;
    }

    private ClipboardHistoryItem UpdateDuplicateWithoutRetention(byte[] json, ClipboardHistoryItem item, string duplicateId)
    {
        var summaries = new List<ClipboardHistoryItem>();
        ClipboardHistoryItem savedItem = item;
        string? redundantAssetPath = null;
        var tempPath = TempHistoryFilePath();

        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                ForEachHistoryItem(json, existing =>
                {
                    if (string.Equals(existing.Id, duplicateId, StringComparison.OrdinalIgnoreCase))
                    {
                        redundantAssetPath = RedundantAssetPath(item, existing);
                        ApplyDuplicateUpdate(existing, item);
                        TouchAsset(existing.AssetPath);
                        WriteSidecar(existing);
                        savedItem = existing;
                    }

                    WriteHistoryItem(writer, existing);
                    summaries.Add(CreateSummaryItem(existing));
                });
                writer.WriteEndArray();
            }

            File.Move(tempPath, HistoryFilePath, overwrite: true);
            SaveIndexCore(OrderedItems(summaries));
            _itemsCache = null;
            _summaryItemsCache = null;
            DeleteAssetPath(redundantAssetPath);
            return savedItem;
        }
        finally
        {
            DeleteTempHistoryFile(tempPath);
        }
    }

    private ClipboardHistoryItem AddNewWithoutRetention(byte[] json, ClipboardHistoryItem item, int maxItems, HashSet<string>? keptUnpinnedIds, bool trimDecisionFromSummary)
    {
        if (!trimDecisionFromSummary && maxItems >= 0)
        {
            var unpinned = new List<(string Id, DateTimeOffset LastUsedAt)> { (item.Id, item.LastUsedAt) };
            ForEachHistoryItem(json, existing =>
            {
                if (!existing.IsPinned)
                {
                    unpinned.Add((existing.Id, existing.LastUsedAt));
                }
            });

            keptUnpinnedIds = unpinned
                .OrderByDescending(entry => entry.LastUsedAt)
                .Take(maxItems)
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var summaries = new List<ClipboardHistoryItem>();
        var removed = new List<ClipboardHistoryItem>();
        var tempPath = TempHistoryFilePath();

        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                if (ShouldKeepAfterTrim(item, keptUnpinnedIds))
                {
                    WriteHistoryItem(writer, item);
                    summaries.Add(CreateSummaryItem(item));
                }
                else
                {
                    removed.Add(item);
                }

                ForEachHistoryItem(json, existing =>
                {
                    if (ShouldKeepAfterTrim(existing, keptUnpinnedIds))
                    {
                        WriteHistoryItem(writer, existing);
                        summaries.Add(CreateSummaryItem(existing));
                    }
                    else
                    {
                        removed.Add(existing);
                    }
                });
                writer.WriteEndArray();
            }

            File.Move(tempPath, HistoryFilePath, overwrite: true);
            SaveIndexCore(OrderedItems(summaries));
            _itemsCache = null;
            _summaryItemsCache = null;
            DeleteAssets(removed);
            if (!removed.Contains(item))
            {
                QueueSidecarWrite(item);
            }

            return item;
        }
        finally
        {
            DeleteTempHistoryFile(tempPath);
        }
    }

    public bool ContainsEquivalent(ClipboardHistoryItem item)
    {
        Enrich(item);
        return FindDuplicate(GetItems(), item) is not null;
    }

    public int ApplyHistoryLimit(int maxItems)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var before = items.Count;
            var removed = TrimUnpinned(items, maxItems);
            if (removed.Count == 0)
            {
                return 0;
            }

            Save(items);
            DeleteAssets(removed);
            return before - items.Count;
        }
    }

    public bool Delete(string id)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var removedItems = items.Where(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (removedItems.Count > 0)
            {
                items.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                Save(items);
                DeleteAssets(removedItems);
            }

            return removedItems.Count > 0;
        }
    }

    public int ClearHistory(bool includePinned)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var removed = items.Where(item => includePinned || !item.IsPinned).ToList();
            if (removed.Count == 0)
            {
                return 0;
            }

            items.RemoveAll(item => includePinned || !item.IsPinned);
            Save(items);
            DeleteAssets(removed);
            return removed.Count;
        }
    }

    public bool SetPinned(string id, bool isPinned)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var item = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return false;
            }

            item.IsPinned = isPinned;
            if (isPinned && item.PinOrder <= 0)
            {
                item.PinOrder = items.Where(i => i.IsPinned).Select(i => i.PinOrder).DefaultIfEmpty(0).Max() + 1;
            }

            if (!isPinned)
            {
                item.PinOrder = 0;
                item.LastUsedAt = DateTimeOffset.Now;
                item.LastCopiedAt = DateTimeOffset.Now;
            }

            Save(items);
            return true;
        }
    }

    public bool MovePinned(string id, int direction)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var pins = items.Where(i => i.IsPinned).OrderBy(i => i.PinOrder).ToList();
            var index = pins.FindIndex(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var target = index + Math.Sign(direction);
            if (index < 0 || target < 0 || target >= pins.Count)
            {
                return false;
            }

            (pins[index].PinOrder, pins[target].PinOrder) = (pins[target].PinOrder, pins[index].PinOrder);
            Save(items);
            return true;
        }
    }

    public bool EditText(string id, string text)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var item = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item is null || item.Kind != ClipboardItemKind.Text)
            {
                return false;
            }

            item.Text = text;
            item.Preview = PreviewText(text);
            item.LastUsedAt = DateTimeOffset.Now;
            item.Kind = ClipboardItemKind.Text;
            item.ContentHash = null;
            item.HtmlText = null;
            item.RtfText = null;
            item.HasOriginalFormatting = false;
            DeleteAssetPath(item.AssetPath);
            item.AssetPath = null;
            Enrich(item);
            PersistContentAsset(item);
            EnsureFriendlyAssetName(item, items);
            WriteSidecar(item);
            Save(items);
            return true;
        }
    }

    /// <summary>
    /// Stores recognized image text for several items in one write. Batched deliberately: every
    /// save rewrites history.json and rebuilds the index files, so writing per image would thrash
    /// the index the list's fast path depends on.
    /// </summary>
    public int SetOcrText(IReadOnlyDictionary<string, string?> textById)
    {
        if (textById.Count == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            var items = GetItems().ToList();
            var updated = 0;
            foreach (var item in items)
            {
                if (!textById.TryGetValue(item.Id, out var text))
                {
                    continue;
                }

                // Empty string, not null, when nothing was found — null means "not attempted yet"
                // and would make the same image be scanned forever.
                item.OcrText = text ?? string.Empty;
                updated++;
            }

            if (updated > 0)
            {
                Save(items);
            }

            return updated;
        }
    }

    public bool Rename(string id, string? title)
    {
        lock (_sync)
        {
            var items = GetItems().ToList();
            var item = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return false;
            }

            var cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            if (cleanTitle?.Length > 120)
            {
                cleanTitle = cleanTitle[..120];
            }

            item.CustomTitle = cleanTitle;
            item.LastUsedAt = DateTimeOffset.Now;
            RenameAssetForItem(item, items);
            WriteSidecar(item);
            Save(items);
            return true;
        }
    }

    public string SaveAsFile(string id, string? outputPath = null)
    {
        var item = GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        var target = outputPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            DefaultFileName(item));

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            File.WriteAllText(target, item.Text ?? string.Empty);
            return target;
        }

        if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            File.Copy(item.AssetPath, target, overwrite: true);
            return target;
        }

        throw new InvalidOperationException("This item cannot be saved as a file yet.");
    }

    public void Save(IEnumerable<ClipboardHistoryItem> items)
    {
        lock (_sync)
        {
            SaveCore(items);
            _itemsCache = _retainLoadedItems ? items.ToList() : null;
        }
    }

    private void SaveCore(IEnumerable<ClipboardHistoryItem> items)
    {
        var list = items.ToList();
        var storedItems = list.Select(item => CreateStoredHistoryItem(item, compactText: false)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
        WriteBytesAtomic(HistoryFilePath, JsonSerializer.SerializeToUtf8Bytes(storedItems, ClipboardHistoryJsonContext.Default.ListClipboardHistoryItem));
        var summaries = OrderedItems(CreateSummaryItems(list));
        SaveIndexCore(summaries);
        _summaryItemsCache = _retainLoadedItems ? summaries : null;
    }

    private void SaveStoredCore(IEnumerable<ClipboardHistoryItem> items)
    {
        var list = items.ToList();
        var tempPath = TempHistoryFilePath();

        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteHistoryItem(writer, item);
                }

                writer.WriteEndArray();
            }

            File.Move(tempPath, HistoryFilePath, overwrite: true);
            SaveIndexCore(OrderedItems(CreateSummaryItems(list)));
        }
        finally
        {
            DeleteTempHistoryFile(tempPath);
        }
    }

    private static bool CanAppendWithoutTrim(IReadOnlyCollection<ClipboardHistoryKeyItem> keys, ClipboardHistoryItem item, int maxItems)
    {
        if (item.IsPinned || maxItems < 0)
        {
            return true;
        }

        return keys.Count(existing => !existing.IsPinned) < maxItems;
    }

    private void AppendNewWithoutRetention(IReadOnlyCollection<ClipboardHistoryKeyItem> keys, ClipboardHistoryItem item)
    {
        var summary = CreateSummaryItem(item);
        var key = ClipboardHistoryKeyItem.From(item);
        var topIndexWasCurrent = IsTopSummaryIndexCurrent();
        AppendStoredHistoryItem(item, hasExistingItems: keys.Count > 0);
        AppendSummaryIndexItem(summary, hasExistingItems: keys.Count > 0);
        AppendKeyIndexItem(key, hasExistingItems: keys.Count > 0);
        RefreshKeyItemsCacheAfterAppend(key);
        UpdateTopIndexAfterAppend(summary, topIndexWasCurrent);
        QueueSidecarWrite(item);
        _itemsCache = null;
        _summaryItemsCache = null;
    }

    private void AppendStoredHistoryItem(ClipboardHistoryItem item, bool hasExistingItems)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
        using var itemStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(itemStream))
        {
            WriteHistoryItem(writer, item);
        }

        using var stream = new FileStream(HistoryFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var closingBracketPosition = FindClosingArrayBracket(stream);
        stream.SetLength(closingBracketPosition);
        stream.Seek(closingBracketPosition, SeekOrigin.Begin);
        if (hasExistingItems)
        {
            stream.WriteByte((byte)',');
        }

        stream.Write(itemStream.ToArray());
        stream.WriteByte((byte)']');
    }

    private void AppendSummaryIndexItem(ClipboardHistoryItem item, bool hasExistingItems)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryIndexFilePath)!);
        var itemJson = JsonSerializer.SerializeToUtf8Bytes(item, ClipboardHistorySummaryJsonContext.Default.ClipboardHistoryItem);
        AppendJsonArrayValue(HistoryIndexFilePath, itemJson, hasExistingItems);
    }

    private void AppendKeyIndexItem(ClipboardHistoryKeyItem item, bool hasExistingItems)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryKeyIndexFilePath)!);
        var itemJson = JsonSerializer.SerializeToUtf8Bytes(item, ClipboardHistoryKeyJsonContext.Default.ClipboardHistoryKeyItem);
        AppendJsonArrayValue(HistoryKeyIndexFilePath, itemJson, hasExistingItems);
    }

    private void RefreshKeyItemsCacheAfterAppend(ClipboardHistoryKeyItem item)
    {
        if (_keyItemsCache is not null)
        {
            _keyItemsCache.Add(item);
            _keyItemsCacheStampUtc = File.GetLastWriteTimeUtc(HistoryKeyIndexFilePath);
        }
    }

    private void UpdateTopIndexAfterAppend(ClipboardHistoryItem summary, bool topIndexWasCurrent)
    {
        if (!topIndexWasCurrent)
        {
            return;
        }

        try
        {
            var topItems = TryReadTopSummaryItems();
            if (topItems is null)
            {
                return;
            }

            SaveTopIndexCore(OrderedItems(topItems.Append(summary)));
        }
        catch
        {
        }
    }

    private List<ClipboardHistoryItem>? TryLoadCurrentTopSummaryItems(out bool needsCompaction)
    {
        needsCompaction = false;
        if (!IsTopSummaryIndexCurrent())
        {
            return null;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(HistoryTopIndexFilePath);
            if (_topSummaryItemsCache is not null && stamp == _topSummaryItemsCacheStampUtc)
            {
                return _topSummaryItemsCache;
            }

            var json = File.ReadAllBytes(HistoryTopIndexFilePath);
            _topSummaryItemsCache = JsonSerializer.Deserialize(json, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem) ?? [];
            needsCompaction = SummaryIndexNeedsCompaction(json) ||
                _topSummaryItemsCache.Count > TopSummaryIndexItemLimit ||
                TopSummaryIndexNeedsCompaction(_topSummaryItemsCache);
            _topSummaryItemsCacheStampUtc = stamp;
            return _topSummaryItemsCache;
        }
        catch
        {
            _topSummaryItemsCache = null;
            _topSummaryItemsCacheStampUtc = default;
            return null;
        }
    }

    private static bool TopSummaryIndexNeedsCompaction(IEnumerable<ClipboardHistoryItem> items)
    {
        return items.Any(item =>
            item.Text?.Length > TopSummaryTextCharacterLimit ||
            !string.IsNullOrWhiteSpace(item.HtmlText) ||
            !string.IsNullOrWhiteSpace(item.RtfText));
    }

    private List<ClipboardHistoryItem>? TryReadTopSummaryItems()
    {
        if (!File.Exists(HistoryTopIndexFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllBytes(HistoryTopIndexFilePath);
            return JsonSerializer.Deserialize(json, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem) ?? [];
        }
        catch
        {
            return null;
        }
    }

    private static void AppendJsonArrayValue(string path, byte[] value, bool hasExistingItems)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var closingBracketPosition = FindClosingArrayBracket(stream);
        stream.SetLength(closingBracketPosition);
        stream.Seek(closingBracketPosition, SeekOrigin.Begin);
        if (hasExistingItems)
        {
            stream.WriteByte((byte)',');
        }

        stream.Write(value);
        stream.WriteByte((byte)']');
    }

    private static long FindClosingArrayBracket(FileStream stream)
    {
        for (var position = stream.Length - 1; position >= 0; position--)
        {
            stream.Seek(position, SeekOrigin.Begin);
            var value = stream.ReadByte();
            if (value < 0)
            {
                break;
            }

            if (char.IsWhiteSpace((char)value))
            {
                continue;
            }

            if (value != ']')
            {
                throw new InvalidDataException("Clip history is not a JSON array.");
            }

            return position;
        }

        throw new InvalidDataException("Clip history is empty.");
    }

    private void SaveIndexCore(IEnumerable<ClipboardHistoryItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryIndexFilePath)!);
        var list = items.ToList();
        WriteBytesAtomic(HistoryIndexFilePath, JsonSerializer.SerializeToUtf8Bytes(list, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem));
        SaveKeyIndexCore(list);
        SaveTopIndexCore(list);
    }

    private void SaveKeyIndexCore(IEnumerable<ClipboardHistoryItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryKeyIndexFilePath)!);
        var keys = items.Select(ClipboardHistoryKeyItem.From).ToList();
        WriteBytesAtomic(HistoryKeyIndexFilePath, JsonSerializer.SerializeToUtf8Bytes(keys, ClipboardHistoryKeyJsonContext.Default.ListClipboardHistoryKeyItem));
        _keyItemsCache = keys;
        _keyItemsCacheStampUtc = File.GetLastWriteTimeUtc(HistoryKeyIndexFilePath);
    }

    private void SaveTopIndexCore(IEnumerable<ClipboardHistoryItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryTopIndexFilePath)!);
        var topItems = items
            .Take(TopSummaryIndexItemLimit)
            .Select(CreateTopSummaryItem)
            .ToList();
        WriteBytesAtomic(HistoryTopIndexFilePath, JsonSerializer.SerializeToUtf8Bytes(topItems, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem));
        _topSummaryItemsCache = topItems;
        _topSummaryItemsCacheStampUtc = File.GetLastWriteTimeUtc(HistoryTopIndexFilePath);
    }

    /// <summary>
    /// Writes through a sibling temp file plus a rename, the same pattern SaveStoredCore streams
    /// through, so a crash mid-write can never leave a half-written store file behind - the old
    /// content survives on disk until the atomic Move replaces it.
    /// </summary>
    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            DeleteTempHistoryFile(tempPath);
        }
    }

    private static List<ClipboardHistoryItem> CreateSummaryItems(IEnumerable<ClipboardHistoryItem> items)
    {
        return items.Select(CreateSummaryItem).ToList();
    }

    private static ClipboardHistoryItem CreateSummaryItem(ClipboardHistoryItem item)
    {
        return new ClipboardHistoryItem
        {
            Id = item.Id,
            Kind = item.Kind,
            Preview = item.Preview,
            CustomTitle = item.CustomTitle,
            ContentHash = item.ContentHash,
            Text = SummaryText(item.Text),
            HtmlText = null,
            RtfText = null,
            HasOriginalFormatting = item.HasOriginalFormatting ||
                !string.IsNullOrWhiteSpace(item.HtmlText) ||
                !string.IsNullOrWhiteSpace(item.RtfText),
            AssetPath = SummaryAssetPath(item),
            FilePaths = item.FilePaths.ToList(),
            IsPinned = item.IsPinned,
            PinOrder = item.PinOrder,
            CreatedAt = item.CreatedAt,
            LastUsedAt = item.LastUsedAt,
            FirstCopiedAt = item.FirstCopiedAt,
            LastCopiedAt = item.LastCopiedAt,
            CopyCount = item.CopyCount,
            SourceApplication = item.SourceApplication,
            // The list needs these to draw the source app's real icon. Nulling the path here is
            // why the detail pane's source icon almost never appeared.
            SourceApplicationPath = item.SourceApplicationPath,
            SourceAppUserModelId = item.SourceAppUserModelId,
            AssetSizeBytes = item.AssetSizeBytes,
            ImageWidth = item.ImageWidth,
            ImageHeight = item.ImageHeight,
            // Capped: the summary index is the hot-path file, and a wall of recognized text from
            // a full-screen screenshot would bloat every read.
            OcrText = SummaryText(item.OcrText),
            CharacterCount = item.CharacterCount ?? item.Text?.Length,
            WordCount = item.WordCount,
        };
    }

    private static ClipboardHistoryItem CreateTopSummaryItem(ClipboardHistoryItem item)
    {
        var summary = CreateSummaryItem(item);
        summary.Text = TopSummaryText(summary.Text);
        return summary;
    }

    private static string? SummaryAssetPath(ClipboardHistoryItem item)
    {
        return item.Kind is ClipboardItemKind.Image or ClipboardItemKind.Files ? item.AssetPath : null;
    }

    private static string? SummaryText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= SummaryTextCharacterLimit)
        {
            return text;
        }

        return text[..SummaryTextCharacterLimit];
    }

    private static string? TopSummaryText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= TopSummaryTextCharacterLimit)
        {
            return text;
        }

        return text[..TopSummaryTextCharacterLimit];
    }

    public string NewAssetFilePath(string extension)
    {
        return NewAssetFilePath(ClipboardItemKind.Image, extension: extension);
    }

    public string NewAssetFilePath(ClipboardItemKind kind, string? sourcePath = null, string? extension = null)
    {
        var folder = ContentFolderFor(kind, sourcePath);
        Directory.CreateDirectory(folder);
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".txt" : extension;
        return Path.Combine(folder, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{safeExtension}");
    }

    public void SetContentRootPath(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            throw new ArgumentException("Clipboard folder path is required.", nameof(contentRootPath));
        }

        lock (_sync)
        {
            var items = GetItems().ToList();
            ContentRootPath = contentRootPath;
            AssetPath = Path.Combine(ContentRootPath, "image");
            HistoryFilePath = Path.Combine(ContentRootPath, "history.json");
            HistoryIndexFilePath = Path.Combine(ContentRootPath, HistoryIndexFileName);
            HistoryTopIndexFilePath = Path.Combine(ContentRootPath, HistoryTopIndexFileName);
            HistoryKeyIndexFilePath = Path.Combine(ContentRootPath, HistoryKeyIndexFileName);
            EnsureContentFolders();
            BackfillContentAssets(items);
            EnsureFriendlyAssetNames(items);
            CleanupEmptyFileCategoryFolders();
            SaveCore(items);
            _itemsCache = _retainLoadedItems ? items : null;
            _summaryItemsCache = _retainLoadedItems ? OrderedItems(CreateSummaryItems(items)) : null;
        }
    }

    /// <summary>
    /// Drops the in-memory caches so the next read comes off disk again. Needed after something
    /// replaced the store's files behind its back — restoring a backup zip — because otherwise the
    /// first mutation would save the pre-restore items straight back over what was just restored.
    /// </summary>
    public void ReloadFromDisk()
    {
        lock (_sync)
        {
            _itemsCache = null;
            _summaryItemsCache = null;
            ClearIndexCaches();
        }
    }

    private void ClearIndexCaches()
    {
        _topSummaryItemsCache = null;
        _topSummaryItemsCacheStampUtc = default;
        _keyItemsCache = null;
        _keyItemsCacheStampUtc = default;
    }

    public static string PreviewText(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 120 ? oneLine : oneLine[..117] + "...";
    }

    private static ClipboardHistoryItem? FindDuplicate(IEnumerable<ClipboardHistoryItem> items, ClipboardHistoryItem incoming)
    {
        return items.FirstOrDefault(existing => IsDuplicate(existing, incoming));
    }

    private static bool IsDuplicate(ClipboardHistoryItem existing, ClipboardHistoryItem incoming)
    {
        return incoming.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link => (existing.Kind == ClipboardItemKind.Text || existing.Kind == ClipboardItemKind.Link) && SameHash(existing, incoming),
            ClipboardItemKind.Color => existing.Kind == ClipboardItemKind.Color && SameHash(existing, incoming),
            ClipboardItemKind.Image => existing.Kind == ClipboardItemKind.Image && SameHash(existing, incoming),
            ClipboardItemKind.Files => existing.Kind == ClipboardItemKind.Files && SameHash(existing, incoming),
            _ => false,
        };
    }

    private static string? RedundantAssetPath(ClipboardHistoryItem item, ClipboardHistoryItem duplicate)
    {
        return !string.IsNullOrWhiteSpace(item.AssetPath) &&
            !string.Equals(item.AssetPath, duplicate.AssetPath, StringComparison.OrdinalIgnoreCase)
                ? item.AssetPath
                : null;
    }

    private static void ApplyDuplicateUpdate(ClipboardHistoryItem duplicate, ClipboardHistoryItem item)
    {
        duplicate.LastUsedAt = DateTimeOffset.Now;
        duplicate.LastCopiedAt = DateTimeOffset.Now;
        duplicate.CopyCount++;
        duplicate.Preview = item.Preview;
        duplicate.CustomTitle = item.CustomTitle ?? duplicate.CustomTitle;
        duplicate.Text = item.Text ?? duplicate.Text;
        duplicate.HtmlText = item.HtmlText ?? duplicate.HtmlText;
        duplicate.RtfText = item.RtfText ?? duplicate.RtfText;
        duplicate.HasOriginalFormatting = duplicate.HasOriginalFormatting || item.HasOriginalFormatting;
        duplicate.AssetPath ??= item.AssetPath;
        duplicate.FilePaths = item.FilePaths.Count > 0 ? item.FilePaths : duplicate.FilePaths;
        duplicate.SourceApplication = item.SourceApplication ?? duplicate.SourceApplication;
        duplicate.AssetSizeBytes = item.AssetSizeBytes ?? duplicate.AssetSizeBytes;
        duplicate.ImageWidth = item.ImageWidth ?? duplicate.ImageWidth;
        duplicate.ImageHeight = item.ImageHeight ?? duplicate.ImageHeight;
        duplicate.CharacterCount = item.CharacterCount ?? duplicate.CharacterCount;
        duplicate.WordCount = item.WordCount ?? duplicate.WordCount;
        // Re-copying the same screenshot merges into the existing item; without this the text
        // already recognized for it would be thrown away and have to be scanned again.
        duplicate.OcrText = item.OcrText ?? duplicate.OcrText;
        duplicate.SourceAppUserModelId = item.SourceAppUserModelId ?? duplicate.SourceAppUserModelId;
        duplicate.SourceApplicationPath = item.SourceApplicationPath ?? duplicate.SourceApplicationPath;
    }

    private static bool ShouldKeepAfterTrim(ClipboardHistoryItem item, HashSet<string>? keptUnpinnedIds)
    {
        return item.IsPinned ||
            keptUnpinnedIds is null ||
            keptUnpinnedIds.Contains(item.Id);
    }

    private static void ForEachHistoryItem(byte[] json, Action<ClipboardHistoryItem> action)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Clipboard history is not a JSON array.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return;
            }

            var item = JsonSerializer.Deserialize(ref reader, ClipboardHistoryJsonContext.Default.ClipboardHistoryItem);
            if (item is not null)
            {
                action(item);
            }
        }
    }

    private string TempHistoryFilePath()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
        return Path.Combine(Path.GetDirectoryName(HistoryFilePath)!, $"history.{Guid.NewGuid():N}.tmp");
    }

    private static void WriteHistoryItem(Utf8JsonWriter writer, ClipboardHistoryItem item)
    {
        JsonSerializer.Serialize(writer, CreateStoredHistoryItem(item, compactText: true), ClipboardHistoryJsonContext.Default.ClipboardHistoryItem);
    }

    private static ClipboardHistoryItem CreateStoredHistoryItem(ClipboardHistoryItem item, bool compactText)
    {
        return new ClipboardHistoryItem
        {
            Id = item.Id,
            Kind = item.Kind,
            Preview = item.Preview,
            CustomTitle = item.CustomTitle,
            ContentHash = item.ContentHash,
            Text = compactText ? StoredText(item) : item.Text,
            HtmlText = StoredRichText(item, ".html", item.HtmlText),
            RtfText = StoredRichText(item, ".rtf", item.RtfText),
            HasOriginalFormatting = item.HasOriginalFormatting,
            AssetPath = item.AssetPath,
            FilePaths = item.FilePaths.ToList(),
            IsPinned = item.IsPinned,
            PinOrder = item.PinOrder,
            CreatedAt = item.CreatedAt,
            LastUsedAt = item.LastUsedAt,
            FirstCopiedAt = item.FirstCopiedAt,
            LastCopiedAt = item.LastCopiedAt,
            CopyCount = item.CopyCount,
            SourceApplication = item.SourceApplication,
            SourceApplicationPath = item.SourceApplicationPath,
            SourceAppUserModelId = item.SourceAppUserModelId,
            AssetSizeBytes = item.AssetSizeBytes,
            ImageWidth = item.ImageWidth,
            ImageHeight = item.ImageHeight,
            OcrText = item.OcrText,
            CharacterCount = item.CharacterCount,
            WordCount = item.WordCount,
        };
    }

    private static string? StoredText(ClipboardHistoryItem item)
    {
        return item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link &&
            !string.IsNullOrWhiteSpace(item.AssetPath)
                ? item.Preview
                : item.Text;
    }

    private static string? StoredRichText(ClipboardHistoryItem item, string extension, string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            item.Kind is not (ClipboardItemKind.Text or ClipboardItemKind.Link) ||
            string.IsNullOrWhiteSpace(item.AssetPath))
        {
            return value;
        }

        try
        {
            File.WriteAllText(RichPayloadPath(item.AssetPath, extension), value);
            return null;
        }
        catch
        {
            return value;
        }
    }

    private static void HydrateTextFromAsset(ClipboardHistoryItem? item)
    {
        if (item is null ||
            item.Kind is not (ClipboardItemKind.Text or ClipboardItemKind.Link) ||
            string.IsNullOrWhiteSpace(item.AssetPath))
        {
            return;
        }

        HydratePlainTextFromAsset(item);
        HydrateRichTextFromAsset(item, ".html", value => item.HtmlText = value, item.HtmlText);
        HydrateRichTextFromAsset(item, ".rtf", value => item.RtfText = value, item.RtfText);
    }

    private static void HydratePlainTextFromAsset(ClipboardHistoryItem item)
    {
        if (!File.Exists(item.AssetPath))
        {
            return;
        }

        if (item.Text is not null &&
            item.CharacterCount is int characterCount &&
            item.Text.Length >= characterCount)
        {
            return;
        }

        try
        {
            item.Text = ReadLinkAssetText(item.AssetPath);
            item.CharacterCount = item.Text.Length;
        }
        catch
        {
        }
    }

    private static void HydrateRichTextFromAsset(ClipboardHistoryItem item, string extension, Action<string> apply, string? currentValue)
    {
        if (!string.IsNullOrEmpty(currentValue))
        {
            return;
        }

        var path = RichPayloadPath(item.AssetPath!, extension);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            apply(File.ReadAllText(path));
        }
        catch
        {
        }
    }

    private static string RichPayloadPath(string assetPath, string extension) => assetPath + extension;

    private static void DeleteTempHistoryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool SameHash(ClipboardHistoryItem existing, ClipboardHistoryItem incoming)
    {
        return !string.IsNullOrEmpty(existing.ContentHash) &&
            existing.ContentHash.Equals(incoming.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ClipboardHistoryItem> TrimUnpinned(List<ClipboardHistoryItem> items, int maxItems)
    {
        if (maxItems < 0)
        {
            return [];
        }

        var unpinned = items.Where(item => !item.IsPinned).OrderByDescending(item => item.LastUsedAt).Take(maxItems).ToHashSet();
        var removed = items.Where(item => !item.IsPinned && !unpinned.Contains(item)).ToList();
        items.RemoveAll(item => !item.IsPinned && !unpinned.Contains(item));
        return removed;
    }

    private void PersistContentAsset(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.AssetPath))
        {
            return;
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            var path = UniqueAssetPathForItem(item, []);
            WriteTextLikeAsset(item, path);
            item.AssetPath = path;
            return;
        }

        if (item.Kind == ClipboardItemKind.Color)
        {
            var path = UniqueAssetPathForItem(item, []);
            WriteColorSwatchAsset(item, path);
            item.AssetPath = path;
            return;
        }

        if (item.Kind != ClipboardItemKind.Files || item.FilePaths.Count == 0)
        {
            return;
        }

        item.AssetPath = PersistFileContent(item.FilePaths);
    }

    private string PersistFileContent(IReadOnlyList<string> filePaths)
    {
        var firstPath = filePaths[0];
        var folder = ContentFolderFor(ClipboardItemKind.Files, firstPath);
        Directory.CreateDirectory(folder);

        if (filePaths.Count == 1 && File.Exists(firstPath))
        {
            var copyPath = UniqueAssetPath(folder, Path.GetFileName(firstPath));
            File.Copy(firstPath, copyPath, overwrite: false);
            return copyPath;
        }

        var bundleFolder = UniqueAssetPath(folder, Path.GetFileName(firstPath));
        Directory.CreateDirectory(bundleFolder);
        foreach (var path in filePaths.Where(File.Exists))
        {
            File.Copy(path, UniqueAssetPath(bundleFolder, Path.GetFileName(path)), overwrite: false);
        }

        File.WriteAllLines(Path.Combine(bundleFolder, "original-paths.txt"), filePaths);
        return bundleFolder;
    }

    private static string UniqueAssetPath(string folder, string fileName, string? currentPath = null)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Clipboard item";
        }

        baseName = TrimFileName(SanitizeFileName(baseName), 90);
        var safeExtension = SanitizeExtension(extension);
        var candidate = Path.Combine(folder, baseName + safeExtension);
        if (PathMatches(candidate, currentPath) || (!File.Exists(candidate) && !Directory.Exists(candidate)))
        {
            return candidate;
        }

        for (var index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(folder, $"{baseName} ({index}){safeExtension}");
            if (PathMatches(candidate, currentPath) || (!File.Exists(candidate) && !Directory.Exists(candidate)))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{baseName} ({Guid.NewGuid():N}){safeExtension}");
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '-');
        }

        return fileName;
    }

    private static string SanitizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        extension = extension.Trim();
        return extension.StartsWith('.') ? SanitizeFileName(extension) : "." + SanitizeFileName(extension);
    }

    private static bool PathMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string TrimFileName(string fileName, int maxLength)
    {
        fileName = fileName.Trim();
        if (fileName.Length == 0)
        {
            return "Clipboard item";
        }

        fileName = fileName.TrimEnd('.');
        if (fileName.Length == 0)
        {
            return "Clipboard item";
        }

        return fileName.Length <= maxLength ? fileName : fileName[..maxLength].TrimEnd();
    }

    private string ContentFolderFor(ClipboardItemKind kind, string? sourcePath = null)
    {
        return kind switch
        {
            ClipboardItemKind.Text => Path.Combine(ContentRootPath, "text"),
            ClipboardItemKind.Link => Path.Combine(ContentRootPath, "links"),
            ClipboardItemKind.Color => Path.Combine(ContentRootPath, "color"),
            ClipboardItemKind.Image => Path.Combine(ContentRootPath, "image"),
            ClipboardItemKind.Files => Path.Combine(ContentRootPath, "file", FileCategoryKey(sourcePath)),
            _ => ContentRootPath,
        };
    }

    private void EnsureContentFolders()
    {
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "text"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "image"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "links"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "color"));
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "file"));
    }

    private void EnsureHistoryFileInContentRoot()
    {
        if (File.Exists(HistoryFilePath))
        {
            return;
        }

        var previousHistoryPath = Path.Combine(RootPath, "history.json");
        if (!File.Exists(previousHistoryPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
        File.Copy(previousHistoryPath, HistoryFilePath, overwrite: false);
    }

    private void MigratePreviousDefaultContentFolder()
    {
        var previousPath = Path.Combine(RootPath, PreviousContentFolderName);
        var currentPath = Path.Combine(RootPath, ContentFolderName);
        if (!ContentRootPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(previousPath) ||
            Directory.Exists(currentPath))
        {
            return;
        }

        try
        {
            Directory.Move(previousPath, currentPath);
        }
        catch (IOException)
        {
            CopyDirectory(previousPath, currentPath);
            TryDeleteDirectory(previousPath);
        }
        catch (UnauthorizedAccessException)
        {
            CopyDirectory(previousPath, currentPath);
            TryDeleteDirectory(previousPath);
        }
    }

    private static string FileCategoryKey(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return "folder";
        }

        var ext = string.IsNullOrWhiteSpace(path) ? "" : Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".xls" or ".xlsx" or ".xlsm" => "excel",
            ".vsd" or ".vsdx" => "visio",
            ".html" or ".htm" => "html",
            ".doc" or ".docx" => "word",
            ".ppt" or ".pptx" => "powerpoint",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "image",
            ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".cmd" or ".ps1" or ".py" => "text",
            _ => "other",
        };
    }

    private void DeleteAssets(IEnumerable<ClipboardHistoryItem> items)
    {
        foreach (var item in items)
        {
            DeleteAssetPath(item.AssetPath);
        }

        CleanupEmptyFileCategoryFolders();
    }

    private static void DeleteAssetPath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        try
        {
            if (File.Exists(SidecarPathFor(assetPath)))
            {
                File.Delete(SidecarPathFor(assetPath));
            }

            DeleteRichPayloadPath(assetPath, ".html");
            DeleteRichPayloadPath(assetPath, ".rtf");

            if (File.Exists(assetPath))
            {
                File.Delete(assetPath);
            }
            else if (Directory.Exists(assetPath))
            {
                Directory.Delete(assetPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void DeleteRichPayloadPath(string assetPath, string extension)
    {
        try
        {
            var path = RichPayloadPath(assetPath, extension);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool EnsureSidecars(IEnumerable<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= WriteSidecar(item);
        }

        return changed;
    }

    private bool BackfillContentAssets(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= BackfillContentAsset(item);
        }

        return changed;
    }

    private bool EnsureFriendlyAssetNames(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= EnsureFriendlyAssetName(item, items);
        }

        return changed;
    }

    private bool EnsureFriendlyAssetName(ClipboardHistoryItem item, IReadOnlyList<ClipboardHistoryItem> items)
    {
        if (string.IsNullOrWhiteSpace(item.AssetPath) || !IsInsideContentRoot(item.AssetPath))
        {
            return false;
        }

        if (!File.Exists(item.AssetPath) && !Directory.Exists(item.AssetPath))
        {
            return false;
        }

        var targetPath = UniqueAssetPathForItem(item, items);
        if (PathMatches(item.AssetPath, targetPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(item.AssetPath))
            {
                MoveSidecar(item.AssetPath, targetPath);
                File.Move(item.AssetPath, targetPath);
            }
            else
            {
                Directory.Move(item.AssetPath, targetPath);
            }

            item.AssetPath = targetPath;
            WriteSidecar(item);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool BackfillContentAsset(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            if (IsInsideContentRoot(item.AssetPath) && File.Exists(item.AssetPath))
            {
                return false;
            }

            var path = UniqueAssetPathForItem(item, []);
            WriteTextLikeAsset(item, path);
            item.AssetPath = path;
            return true;
        }

        if (item.Kind == ClipboardItemKind.Color)
        {
            if (IsInsideContentRoot(item.AssetPath) && File.Exists(item.AssetPath) && Path.GetExtension(item.AssetPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var oldPath = item.AssetPath;
            var path = UniqueAssetPathForItem(item, []);
            WriteColorSwatchAsset(item, path);
            item.AssetPath = path;
            if (!PathMatches(oldPath, path))
            {
                DeleteAssetPath(oldPath);
            }

            return true;
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            if (IsInsideContentRoot(item.AssetPath) && File.Exists(item.AssetPath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.AssetPath) || !File.Exists(item.AssetPath))
            {
                return false;
            }

            var extension = Path.GetExtension(item.AssetPath);
            var path = UniqueAssetPathForItem(item, []);
            File.Copy(item.AssetPath, path, overwrite: false);
            item.AssetPath = path;
            return true;
        }

        if (item.Kind != ClipboardItemKind.Files || item.FilePaths.Count == 0)
        {
            return false;
        }

        if (IsInsideContentRoot(item.AssetPath) && (File.Exists(item.AssetPath) || Directory.Exists(item.AssetPath)))
        {
            return false;
        }

        item.AssetPath = PersistFileContent(item.FilePaths);
        return true;
    }

    private bool ReconcileExternalAssetRenames(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.AssetPath) ||
                File.Exists(item.AssetPath) ||
                Directory.Exists(item.AssetPath))
            {
                continue;
            }

            var foundPath = FindMatchingAssetByContent(item);
            if (foundPath is null)
            {
                continue;
            }

            item.AssetPath = foundPath;
            var title = TitleFromAssetPath(item, foundPath);
            item.CustomTitle = string.IsNullOrWhiteSpace(title) ? item.CustomTitle : title;
            WriteSidecar(item);
            changed = true;
        }

        return changed;
    }

    private string? FindMatchingAssetByContent(ClipboardHistoryItem item)
    {
        var folder = ContentFolderFor(item.Kind, item.FilePaths.FirstOrDefault());
        if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(item.ContentHash))
        {
            return null;
        }

        var sidecarMatch = FindMatchingAssetBySidecar(folder, item);
        if (sidecarMatch is not null)
        {
            return sidecarMatch;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (ShouldIgnoreAssetCandidate(file))
            {
                continue;
            }

            if (AssetContentMatches(item, file))
            {
                return file;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            if (item.Kind == ClipboardItemKind.Files && AssetContentMatches(item, directory))
            {
                return directory;
            }
        }

        return null;
    }

    private string? FindMatchingAssetBySidecar(string folder, ClipboardHistoryItem item)
    {
        foreach (var sidecar in Directory.EnumerateFiles(folder, "*" + SidecarExtension, SearchOption.AllDirectories))
        {
            var metadata = ReadSidecar(sidecar);
            if (!string.Equals(metadata?.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assetPath = AssetPathFromSidecarPath(sidecar);
            if (File.Exists(assetPath) || Directory.Exists(assetPath))
            {
                return assetPath;
            }

            if (Path.GetFileName(sidecar).Equals(DirectorySidecarName, StringComparison.OrdinalIgnoreCase))
            {
                var folderAsset = Path.GetDirectoryName(sidecar);
                if (!string.IsNullOrWhiteSpace(folderAsset) && Directory.Exists(folderAsset))
                {
                    return folderAsset;
                }
            }
        }

        return null;
    }

    private static bool ShouldIgnoreAssetCandidate(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("history.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(HistoryIndexFileName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(HistoryTopIndexFileName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(HistoryKeyIndexFileName, StringComparison.OrdinalIgnoreCase) ||
            IsRichPayloadFileName(name) ||
            name.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRichPayloadFileName(string name)
    {
        return name.EndsWith(".txt.html", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".txt.rtf", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".url.html", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".url.rtf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AssetContentMatches(ClipboardHistoryItem item, string path)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Text)
            {
                var text = File.ReadAllText(path);
                return string.Equals(HashText(text), item.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, item.Text ?? string.Empty, StringComparison.Ordinal);
            }

            if (item.Kind == ClipboardItemKind.Color)
            {
                return string.Equals(Path.GetFileNameWithoutExtension(path), item.Text, StringComparison.OrdinalIgnoreCase);
            }

            if (item.Kind == ClipboardItemKind.Link)
            {
                var text = ReadLinkAssetText(path);
                return string.Equals(HashText(text), item.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, item.Text ?? string.Empty, StringComparison.Ordinal);
            }

            if ((item.Kind == ClipboardItemKind.Image || item.Kind == ClipboardItemKind.Files) && File.Exists(path))
            {
                return string.Equals(HashFile(path), item.ContentHash, StringComparison.OrdinalIgnoreCase);
            }

            if (item.Kind == ClipboardItemKind.Files && Directory.Exists(path))
            {
                var manifest = Path.Combine(path, "original-paths.txt");
                if (File.Exists(manifest))
                {
                    var text = string.Join("|", File.ReadAllLines(manifest).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                    return string.Equals(HashText(text), item.ContentHash, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ReadLinkAssetText(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        if (!Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllText(path);
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return line[4..];
            }
        }

        return string.Empty;
    }

    private void RenameAssetForItem(ClipboardHistoryItem item, IReadOnlyList<ClipboardHistoryItem> items)
    {
        EnsureFriendlyAssetName(item, items);
    }

    private string UniqueAssetPathForItem(ClipboardHistoryItem item, IReadOnlyList<ClipboardHistoryItem> items)
    {
        var folder = ContentFolderFor(item.Kind, item.FilePaths.FirstOrDefault());
        Directory.CreateDirectory(folder);
        var fileName = DesiredAssetFileName(item);
        var currentPath = item.AssetPath;
        var reservedPaths = items
            .Where(existing => !ReferenceEquals(existing, item))
            .Select(existing => existing.AssetPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var target = UniqueAssetPath(folder, fileName, currentPath);
        if (!reservedPaths.Contains(target))
        {
            return target;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, $"{SanitizeFileName(baseName)} ({index}){SanitizeExtension(extension)}");
            if (PathMatches(candidate, currentPath) || (!reservedPaths.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{SanitizeFileName(baseName)} ({Guid.NewGuid():N}){SanitizeExtension(extension)}");
    }

    private static string DesiredAssetFileName(ClipboardHistoryItem item)
    {
        var title = DisplayTitle(item);
        return item.Kind switch
        {
            ClipboardItemKind.Link => $"{title}.url",
            ClipboardItemKind.Text => $"{title}.txt",
            ClipboardItemKind.Color => $"{title}.png",
            ClipboardItemKind.Image => $"{title}{ImageAssetExtension(item)}",
            ClipboardItemKind.Files when item.FilePaths.Count == 1 && File.Exists(item.FilePaths[0]) => FileNameWithExtension(title, Path.GetExtension(item.FilePaths[0])),
            ClipboardItemKind.Files when !string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath) => FileNameWithExtension(title, Path.GetExtension(item.AssetPath)),
            ClipboardItemKind.Files => title,
            _ => $"{title}.txt",
        };
    }

    private static string DisplayTitle(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomTitle))
        {
            return item.CustomTitle;
        }

        if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
        {
            return Path.GetFileName(item.FilePaths[0]);
        }

        if (item.Kind == ClipboardItemKind.Image && string.IsNullOrWhiteSpace(item.Preview))
        {
            return item.ImageWidth is not null && item.ImageHeight is not null ? $"Image {item.ImageWidth} x {item.ImageHeight}" : "Image";
        }

        return string.IsNullOrWhiteSpace(item.Preview) ? item.Kind.ToString() : item.Preview;
    }

    private static string ImageAssetExtension(ClipboardHistoryItem item)
    {
        var extension = Path.GetExtension(item.AssetPath);
        return string.IsNullOrWhiteSpace(extension) ? ".png" : extension;
    }

    private static string FileNameWithExtension(string title, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return title;
        }

        return title.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? title : title + extension;
    }

    private static string TitleFromAssetPath(ClipboardHistoryItem item, string path)
    {
        if (item.Kind == ClipboardItemKind.Files)
        {
            return Path.GetFileName(path);
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private static void WriteTextLikeAsset(ClipboardHistoryItem item, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (item.Kind == ClipboardItemKind.Link)
        {
            File.WriteAllText(path, $"[InternetShortcut]{Environment.NewLine}URL={item.Text ?? string.Empty}{Environment.NewLine}");
            return;
        }

        File.WriteAllText(path, item.Text ?? string.Empty);
    }

    private static void WriteColorSwatchAsset(ClipboardHistoryItem item, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var color = ParseHexColor(item.Text ?? item.Preview);
        WriteSolidPng(path, color.red, color.green, color.blue, 128, 128);
    }

    private static (byte red, byte green, byte blue) ParseHexColor(string? value)
    {
        var hex = (value ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        }

        if (hex.Length != 6 ||
            !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return (0, 0, 0);
        }

        return (red, green, blue);
    }

    private static void WriteSolidPng(string path, byte red, byte green, byte blue, int width, int height)
    {
        var rowLength = 1 + (width * 3);
        var raw = new byte[rowLength * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowLength;
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var pixel = rowStart + 1 + (x * 3);
                raw[pixel] = red;
                raw[pixel + 1] = green;
                raw[pixel + 2] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var stream = File.Create(path);
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(stream, "IHDR", ihdr);
        WritePngChunk(stream, "IDAT", compressed.ToArray());
        WritePngChunk(stream, "IEND", []);
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)Crc32(typeBytes, data)));
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)((value >> 24) & 0xFF);
        target[offset + 1] = (byte)((value >> 16) & 0xFF);
        target[offset + 2] = (byte)((value >> 8) & 0xFF);
        target[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes)
        {
            crc = UpdateCrc32(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc32(crc, value);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc;
    }

    private static void TouchAsset(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        try
        {
            if (File.Exists(assetPath))
            {
                File.SetLastWriteTime(assetPath, DateTime.Now);
            }
            else if (Directory.Exists(assetPath))
            {
                Directory.SetLastWriteTime(assetPath, DateTime.Now);
            }
        }
        catch
        {
        }
    }

    private static bool WriteSidecar(ClipboardHistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AssetPath) ||
            (!File.Exists(item.AssetPath) && !Directory.Exists(item.AssetPath)))
        {
            return false;
        }

        try
        {
            var sidecarPath = SidecarPathFor(item.AssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            var metadata = new ClipboardAssetMetadata
            {
                Id = item.Id,
                Kind = item.Kind.ToString(),
                ContentHash = item.ContentHash,
            };
            var json = JsonSerializer.Serialize(metadata, ClipboardHistoryJsonContext.Default.ClipboardAssetMetadata);
            var exists = File.Exists(sidecarPath);
            var oldJson = exists ? File.ReadAllText(sidecarPath) : null;
            var oldAttributes = exists ? File.GetAttributes(sidecarPath) : default;
            var shouldWrite = !exists || !string.Equals(oldJson, json, StringComparison.Ordinal);
            if (shouldWrite)
            {
                File.WriteAllText(sidecarPath, json);
            }

            var newAttributes = File.GetAttributes(sidecarPath) | FileAttributes.Hidden;
            if (oldAttributes != newAttributes)
            {
                File.SetAttributes(sidecarPath, newAttributes);
            }

            return shouldWrite || oldAttributes != newAttributes;
        }
        catch
        {
            return false;
        }
    }

    private static void QueueSidecarWrite(ClipboardHistoryItem item)
    {
        ThreadPool.QueueUserWorkItem(_ => WriteSidecar(item));
    }

    private static ClipboardAssetMetadata? ReadSidecar(string sidecarPath)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(sidecarPath), ClipboardHistoryJsonContext.Default.ClipboardAssetMetadata);
        }
        catch
        {
            return null;
        }
    }

    private static string SidecarPathFor(string assetPath)
    {
        return Directory.Exists(assetPath)
            ? Path.Combine(assetPath, DirectorySidecarName)
            : assetPath + SidecarExtension;
    }

    private static string AssetPathFromSidecarPath(string sidecarPath)
    {
        if (Path.GetFileName(sidecarPath).Equals(DirectorySidecarName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(sidecarPath) ?? sidecarPath;
        }

        return sidecarPath.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase)
            ? sidecarPath[..^SidecarExtension.Length]
            : sidecarPath;
    }

    private void QuarantineCorruptHistory()
    {
        // The millisecond timestamp is not unique enough on its own: two quarantines inside the
        // same tick (a corrupt load followed by an immediately re-corrupted save, or rapid
        // retries) would Move onto the existing backup, throw, and fall into the delete branch
        // below — losing the evidence file. Bump a counter until the name is free.
        var basePath = HistoryFilePath + $".corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}";
        var backupPath = basePath;
        for (var attempt = 2; File.Exists(backupPath); attempt++)
        {
            backupPath = $"{basePath}-{attempt}";
        }

        try
        {
            File.Move(HistoryFilePath, backupPath);
            QuarantinedHistoryPath = backupPath;
        }
        catch
        {
            // The rename can lose to a scanner holding the file open. Deleting still unblocks
            // future saves, and the caller rewrites history.json right after; the original path
            // in the property tells the Shell recovery happened even without a backup.
            try
            {
                File.Delete(HistoryFilePath);
            }
            catch
            {
            }

            QuarantinedHistoryPath = HistoryFilePath;
        }
    }

    /// <summary>
    /// Rebuilds the history list from the hidden per-asset sidecar files after history.json was
    /// found unreadable. A sidecar only carries Id/Kind/ContentHash, but the asset file next to
    /// it is the real payload, so everything except per-item metadata (pin state, copy counts,
    /// custom titles) comes back rather than the user losing their whole history.
    /// </summary>
    private List<ClipboardHistoryItem> RebuildItemsFromSidecars()
    {
        var items = new List<ClipboardHistoryItem>();
        if (!Directory.Exists(ContentRootPath))
        {
            return items;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sidecar in Directory.EnumerateFiles(ContentRootPath, "*" + SidecarExtension, SearchOption.AllDirectories))
        {
            var metadata = ReadSidecar(sidecar);
            if (metadata is null ||
                string.IsNullOrWhiteSpace(metadata.Id) ||
                !seenIds.Add(metadata.Id) ||
                !Enum.TryParse<ClipboardItemKind>(metadata.Kind, ignoreCase: true, out var kind))
            {
                continue;
            }

            var assetPath = AssetPathFromSidecarPath(sidecar);
            var isFile = File.Exists(assetPath);
            if (!isFile && !Directory.Exists(assetPath))
            {
                continue;
            }

            // The asset's write time stands in for the lost copy timestamps so the rebuilt list
            // keeps a sensible recency order.
            var stamp = new DateTimeOffset(isFile
                ? File.GetLastWriteTime(assetPath)
                : Directory.GetLastWriteTime(assetPath));
            var item = new ClipboardHistoryItem
            {
                Id = metadata.Id,
                Kind = kind,
                ContentHash = metadata.ContentHash,
                AssetPath = assetPath,
                CreatedAt = stamp,
                LastUsedAt = stamp,
                FirstCopiedAt = stamp,
                LastCopiedAt = stamp,
            };

            if (kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
            {
                item.Text = ReadLinkAssetText(assetPath);
                item.Preview = PreviewText(item.Text);
            }
            else if (kind == ClipboardItemKind.Color)
            {
                item.Text = Path.GetFileNameWithoutExtension(assetPath);
                item.Preview = item.Text;
            }
            else if (kind == ClipboardItemKind.Files)
            {
                item.FilePaths = [assetPath];
                item.Preview = Path.GetFileName(assetPath);
            }
            else
            {
                item.Preview = Path.GetFileNameWithoutExtension(assetPath);
            }

            items.Add(item);
        }

        items.Sort(CompareItems);
        return items;
    }

    private static void MoveSidecar(string oldAssetPath, string newAssetPath)
    {
        var oldSidecar = SidecarPathFor(oldAssetPath);
        if (!File.Exists(oldSidecar))
        {
            return;
        }

        try
        {
            var newSidecar = SidecarPathFor(newAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(newSidecar)!);
            if (File.Exists(newSidecar))
            {
                File.Delete(newSidecar);
            }

            File.Move(oldSidecar, newSidecar);
            File.SetAttributes(newSidecar, File.GetAttributes(newSidecar) | FileAttributes.Hidden);
        }
        catch
        {
        }
    }

    private bool IsInsideContentRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(ContentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void CleanupEmptyFileCategoryFolders()
    {
        var fileRoot = Path.Combine(ContentRootPath, "file");
        if (!Directory.Exists(fileRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(fileRoot))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
            }
        }
    }

    private static string DefaultFileName(ClipboardHistoryItem item)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        return item.Kind == ClipboardItemKind.Image ? $"clipboard-{stamp}.png" : $"clipboard-{stamp}.txt";
    }

    private static bool Contains(string? value, string query)
    {
        return value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void Enrich(ClipboardHistoryItem item, bool refreshCopiedAt = false)
    {
        var textLength = item.Text?.Length ?? 0;
        var hasTruncatedAssetText = item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link &&
            !string.IsNullOrWhiteSpace(item.AssetPath) &&
            item.CharacterCount is int characterCount &&
            characterCount > textLength;

        item.HasOriginalFormatting = item.HasOriginalFormatting ||
            !string.IsNullOrWhiteSpace(item.HtmlText) ||
            !string.IsNullOrWhiteSpace(item.RtfText);
        item.FirstCopiedAt = item.FirstCopiedAt == default ? DateTimeOffset.Now : item.FirstCopiedAt;
        item.LastCopiedAt = refreshCopiedAt || item.LastCopiedAt == default ? DateTimeOffset.Now : item.LastCopiedAt;

        if (item.Kind == ClipboardItemKind.Text && ClipboardLinkDetector.IsLinkOrEmail(item.Text))
        {
            item.Kind = ClipboardItemKind.Link;
        }

        if (item.Kind == ClipboardItemKind.Text && TryNormalizeColorText(item.Text, item.SourceApplication, out var colorHex))
        {
            item.Kind = ClipboardItemKind.Color;
            item.Text = colorHex;
            item.Preview = colorHex;
            item.ContentHash = HashText(colorHex);
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            item.CharacterCount = hasTruncatedAssetText ? item.CharacterCount : textLength;
            item.WordCount = hasTruncatedAssetText ? item.WordCount : Regex.Matches(item.Text ?? string.Empty, @"\b[\w']+\b").Count;
            if (string.IsNullOrWhiteSpace(item.ContentHash) || item.ContentHash == HashText(string.Empty))
            {
                item.ContentHash = HashText(item.Text ?? string.Empty);
            }
        }

        if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            item.AssetSizeBytes = new FileInfo(item.AssetPath).Length;
            if (string.IsNullOrWhiteSpace(item.ContentHash) || item.ContentHash == HashText(string.Empty))
            {
                item.ContentHash = HashFile(item.AssetPath);
            }
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            item.ContentHash ??= HashText(string.Join("|", item.FilePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
            item.AssetSizeBytes = item.FilePaths
                .Where(File.Exists)
                .Select(path => new FileInfo(path).Length)
                .Sum();
        }
    }

    private static bool NormalizeLoadedItems(List<ClipboardHistoryItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            var before = item.ContentHash;
            var kindBefore = item.Kind;
            var copiedBefore = item.LastCopiedAt;
            var formattingBefore = item.HasOriginalFormatting;
            NormalizeSource(item);
            RepairBadLoadCopiedAt(item);
            if (TryNormalizeColorText(item.Text ?? item.Preview, item.SourceApplication, out var colorHex))
            {
                item.Kind = ClipboardItemKind.Color;
                item.Text = colorHex;
                item.Preview = colorHex;
                item.ContentHash = HashText(colorHex);
            }
            else if (LooksLikeSavedImage(item))
            {
                item.Kind = ClipboardItemKind.Image;
            }

            Enrich(item);
            changed |= before != item.ContentHash;
            changed |= kindBefore != item.Kind;
            changed |= copiedBefore != item.LastCopiedAt;
            changed |= formattingBefore != item.HasOriginalFormatting;
        }

        var seen = new Dictionary<string, ClipboardHistoryItem>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (string.IsNullOrEmpty(item.ContentHash))
            {
                continue;
            }

            var key = $"{item.Kind}:{item.ContentHash}";
            if (!seen.TryGetValue(key, out var existing))
            {
                seen[key] = item;
                continue;
            }

            existing.CopyCount += Math.Max(1, item.CopyCount);
            existing.FirstCopiedAt = existing.FirstCopiedAt < item.FirstCopiedAt ? existing.FirstCopiedAt : item.FirstCopiedAt;
            existing.LastCopiedAt = existing.LastCopiedAt > item.LastCopiedAt ? existing.LastCopiedAt : item.LastCopiedAt;
            existing.IsPinned |= item.IsPinned;
            existing.PinOrder = existing.PinOrder == 0 ? item.PinOrder : existing.PinOrder;
            items.RemoveAt(index);
            index--;
            changed = true;
        }

        return changed;
    }

    private static void RepairBadLoadCopiedAt(ClipboardHistoryItem item)
    {
        if (item.LastCopiedAt == default)
        {
            return;
        }

        var baseline = item.FirstCopiedAt != default ? item.FirstCopiedAt : item.CreatedAt;
        if (baseline == default)
        {
            return;
        }

        var lastUsed = item.LastUsedAt == default ? baseline : item.LastUsedAt;
        var copiedIsFarAfterOriginal = item.LastCopiedAt - baseline > TimeSpan.FromMinutes(10);
        var usedDidNotMoveWithCopy = item.LastCopiedAt - lastUsed > TimeSpan.FromMinutes(5);
        if (copiedIsFarAfterOriginal && usedDidNotMoveWithCopy)
        {
            item.LastCopiedAt = baseline;
        }
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }

    private static bool LooksLikeSavedImage(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Image)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.AssetPath) || !File.Exists(item.AssetPath))
        {
            return false;
        }

        var extension = Path.GetExtension(item.AssetPath);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeColorText(string? text, string? source, out string hex) =>
        ClipboardColorDetector.TryNormalize(text, source, out hex);

    private static void NormalizeSource(ClipboardHistoryItem item)
    {
        if (item.SourceApplication?.Equals("olk", StringComparison.OrdinalIgnoreCase) == true)
        {
            item.SourceApplication = "Outlook";
        }

        if (string.IsNullOrWhiteSpace(item.SourceApplicationPath) || !File.Exists(item.SourceApplicationPath))
        {
            return;
        }

        var appName = Path.GetFileNameWithoutExtension(item.SourceApplicationPath);
        if (!string.IsNullOrWhiteSpace(appName))
        {
            item.SourceApplication = appName.Equals("olk", StringComparison.OrdinalIgnoreCase) ? "Outlook" : appName;
        }
    }

    private static void MigrateLegacyStore(string rootPath)
    {
        if (File.Exists(Path.Combine(rootPath, "history.json")))
        {
            return;
        }

        // Overridable so tests can run the migration against a temp folder instead of the
        // user's real %LocalAppData%\RayClipboard.
        var legacyRoot = LegacyRootOverrideForTests.Value ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppDataFolderName);
        var legacyHistory = Path.Combine(legacyRoot, "history.json");
        if (!File.Exists(legacyHistory))
        {
            return;
        }

        Directory.CreateDirectory(rootPath);
        var targetHistory = Path.Combine(rootPath, "history.json");
        File.Copy(legacyHistory, targetHistory, overwrite: false);

        var legacyAssets = Path.Combine(legacyRoot, "assets");
        var targetAssets = Path.Combine(rootPath, "assets");
        if (!Directory.Exists(legacyAssets) || Directory.Exists(targetAssets))
        {
            return;
        }

        CopyDirectory(legacyAssets, targetAssets);
        RewriteMigratedAssetPaths(targetHistory, legacyAssets, targetAssets);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The new folder is already usable. Leaving the old folder is safer than blocking startup.
        }
    }

    private static void RewriteMigratedAssetPaths(string historyPath, string legacyAssets, string targetAssets)
    {
        var items = JsonSerializer.Deserialize(File.ReadAllText(historyPath), ClipboardHistoryJsonContext.Default.ListClipboardHistoryItem);
        if (items is null)
        {
            return;
        }

        var changed = false;
        foreach (var item in items)
        {
            if (item.AssetPath?.StartsWith(legacyAssets, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            item.AssetPath = targetAssets + item.AssetPath[legacyAssets.Length..];
            changed = true;
        }

        if (changed)
        {
            WriteBytesAtomic(historyPath, JsonSerializer.SerializeToUtf8Bytes(items, ClipboardHistoryJsonContext.Default.ListClipboardHistoryItem));
        }
    }
}

internal sealed class ClipboardAssetMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? ContentHash { get; set; }
}
