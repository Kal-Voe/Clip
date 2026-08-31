using System.IO.Compression;
using System.Text.Json;

namespace Clip.Core;

/// <summary>
/// What sits at the top of an export zip. It does two jobs: it is the "this really is a Clip
/// export" marker Restore checks before it touches a single file on disk, and it records where
/// the content lived when it was written. That second one is not optional — item asset paths are
/// stored absolute, so a restore into a folder with a different path has to rewrite them or every
/// image, file and text item would point at somewhere that no longer exists.
/// </summary>
public sealed class ClipboardHistoryBackupManifest
{
    public string Format { get; set; } = ClipboardHistoryBackup.FormatId;

    public int Version { get; set; } = ClipboardHistoryBackup.FormatVersion;

    public string ContentRootPath { get; set; } = string.Empty;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

    public int ItemCount { get; set; }
}

/// <summary>
/// Zips the clipboard content folder up and puts it back again. Losing %LocalAppData%\Clip loses
/// every pinned item and every snippet, and nothing else in the app can hand those back.
///
/// The content folder is the whole backup: history.json, the hidden per-asset sidecars, and the
/// text/image/links/color/file trees the items point into. The derived indexes are deliberately
/// left out (see <see cref="IsDerivedIndexFile"/>) — they are a cache, and a restored folder with
/// no index behaves exactly like a fresh one.
/// </summary>
public static class ClipboardHistoryBackup
{
    public const string FormatId = "clip-history-export";
    public const int FormatVersion = 1;

    internal const string ManifestEntryName = "clip-export.json";
    internal const string ContentEntryPrefix = "content/";
    private const string HistoryFileName = "history.json";

    /// <summary>
    /// Writes every file under <paramref name="contentRootPath"/> into a zip at
    /// <paramref name="zipPath"/>, with the manifest alongside it.
    /// </summary>
    public static int Export(string contentRootPath, string zipPath)
    {
        var historyPath = Path.Combine(contentRootPath, HistoryFileName);
        if (!File.Exists(historyPath))
        {
            throw new InvalidOperationException("There is no clipboard history to export yet.");
        }

        var items = ReadItems(historyPath);
        var manifest = new ClipboardHistoryBackupManifest
        {
            ContentRootPath = Path.GetFullPath(contentRootPath),
            ItemCount = items.Count,
        };

        // Build beside the destination and move into place at the end. Writing straight to the
        // chosen path would leave a truncated zip sitting there looking like a backup if the
        // disk filled up or a file could not be read halfway through.
        var staging = zipPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var manifestStream = entry.Open())
                {
                    JsonSerializer.Serialize(manifestStream, manifest, ClipboardHistoryJsonContext.Default.ClipboardHistoryBackupManifest);
                }

                foreach (var file in Directory.EnumerateFiles(contentRootPath, "*", SearchOption.AllDirectories))
                {
                    if (IsDerivedIndexFile(Path.GetFileName(file)))
                    {
                        continue;
                    }

                    AddFile(archive, contentRootPath, file);
                }
            }

            File.Move(staging, zipPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(staging);
            throw;
        }

        return manifest.ItemCount;
    }

    /// <summary>
    /// Replaces the content folder with what the zip holds, and returns how many items came back.
    /// Everything that can be refused is refused before anything on disk is touched: a zip with
    /// no Clip manifest never gets as far as extraction. The replacement itself extracts to a
    /// temporary folder and then swaps, so a failure part way leaves the existing history intact
    /// rather than half of each.
    /// </summary>
    public static int Restore(string zipPath, string contentRootPath)
    {
        var target = Path.GetFullPath(contentRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var staging = target + ".restoring-" + suffix;
        var replaced = target + ".replaced-" + suffix;

        int itemCount;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var manifest = ReadManifest(archive)
                ?? throw new InvalidDataException("That file is not a Clip history export.");

            try
            {
                ExtractContent(archive, staging);
                var stagedHistory = Path.Combine(staging, HistoryFileName);
                if (!File.Exists(stagedHistory))
                {
                    throw new InvalidDataException("That Clip export has no history file in it.");
                }

                itemCount = RebaseAssetPaths(stagedHistory, manifest.ContentRootPath, target);
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }

        // Siblings of the target, so both moves stay on one volume and are a rename rather than
        // a copy — the window where neither folder is in place is as short as Windows can make it.
        try
        {
            if (Directory.Exists(target))
            {
                MoveDirectory(target, replaced);
            }

            try
            {
                MoveDirectory(staging, target);
            }
            catch
            {
                // Put the original back before giving up. A failed restore that leaves no content
                // folder at all is worse than a restore that simply did not happen.
                if (Directory.Exists(replaced) && !Directory.Exists(target))
                {
                    MoveDirectory(replaced, target);
                }

                throw;
            }
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        TryDeleteDirectory(replaced);
        return itemCount;
    }

    /// <summary>
    /// Whether the file is a Clip export, judged the same way <see cref="Restore"/> judges it.
    /// Never throws: an unreadable or non-zip file is simply not an export.
    /// </summary>
    public static bool IsExport(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return ReadManifest(archive) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The index files are rebuilt from history.json on demand, so shipping them in a backup only
    /// risks restoring a cache that disagrees with the items beside it.
    /// </summary>
    private static bool IsDerivedIndexFile(string fileName) =>
        fileName.Equals("history.index.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("history.top.index.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("history.keys.json", StringComparison.OrdinalIgnoreCase);

    private static void AddFile(ZipArchive archive, string contentRootPath, string filePath)
    {
        var relative = Path.GetRelativePath(contentRootPath, filePath).Replace('\\', '/');
        var entry = archive.CreateEntry(ContentEntryPrefix + relative, CompressionLevel.Optimal);

        // FileShare.ReadWrite because capture, the OCR worker and the UI all write into this tree.
        // Opening exclusively would fail the whole export just because something touched
        // history.json a moment before the user pressed the button.
        using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static ClipboardHistoryBackupManifest? ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestEntryName);
        if (entry is null)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize(stream, ClipboardHistoryJsonContext.Default.ClipboardHistoryBackupManifest);
            return manifest is not null && manifest.Format.Equals(FormatId, StringComparison.OrdinalIgnoreCase)
                ? manifest
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ExtractContent(ZipArchive archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(ContentEntryPrefix, StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var relative = entry.FullName[ContentEntryPrefix.Length..];
            var targetPath = Path.GetFullPath(Path.Combine(destination, relative));
            // A zip is untrusted input even when we wrote it: an entry named ..\..\something
            // would otherwise write outside the folder being restored.
            if (!targetPath.StartsWith(fullDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("That Clip export contains a file path outside the history folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    /// <summary>
    /// Points every asset path at the folder being restored into. Paths that were not under the
    /// exported content root are left alone: those are items whose asset went missing before the
    /// export, and inventing a path for them would be worse than leaving the broken one.
    /// </summary>
    private static int RebaseAssetPaths(string historyPath, string exportedRoot, string targetRoot)
    {
        var items = ReadItems(historyPath);
        if (string.IsNullOrWhiteSpace(exportedRoot) ||
            Path.GetFullPath(exportedRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            return items.Count;
        }

        var prefix = Path.GetFullPath(exportedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var changed = false;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.AssetPath) ||
                !item.AssetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.AssetPath = Path.Combine(targetRoot, item.AssetPath[prefix.Length..]);
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(historyPath, JsonSerializer.Serialize(items, ClipboardHistoryJsonContext.Default.ListClipboardHistoryItem));
        }

        return items.Count;
    }

    private static List<ClipboardHistoryItem> ReadItems(string historyPath)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(historyPath), ClipboardHistoryJsonContext.Default.ListClipboardHistoryItem) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt history.json is still worth backing up byte for byte — the item count is
            // the only thing lost, and refusing the export would strand the assets too.
            return [];
        }
    }

    /// <summary>
    /// Renaming a folder fails with "access denied" while anything still holds a handle to a file
    /// inside it, and this store is written to from several places at once — capture, the OCR
    /// worker, the background sidecar writes. Those handles live for milliseconds, so a short
    /// retry turns a coin-flip failure into a reliable swap. If it still will not move, the caller
    /// puts the original folder back and reports it: the one thing that must never happen is a
    /// half-replaced store.
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < 6)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 6)
            {
            }

            Thread.Sleep(60);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
