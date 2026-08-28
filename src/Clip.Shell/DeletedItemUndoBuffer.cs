using System.IO;
using Clip.Core;

namespace Clip.Shell;

/// <summary>
/// One-item holding pen for the last deleted clipboard item, so Del has an undo.
///
/// The store's Delete removes the item's asset file (the image or file payload) along with the
/// entry, so remembering the item object alone is not enough: the asset bytes are copied aside
/// while they still exist, and put back at the original path before the item is re-added.
/// One item deep on purpose — that matches the toast's promise: Ctrl+Z restores the delete the
/// toast just named, nothing older.
/// </summary>
internal sealed class DeletedItemUndoBuffer
{
    private ClipboardHistoryItem? _item;
    private string? _assetBackupPath;

    public bool HasItem => _item is not null;

    /// <summary>Call before the store deletes the item — after, the asset is gone.</summary>
    public void Remember(ClipboardHistoryItem item)
    {
        Forget();

        string? backup = null;
        if (!string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath))
        {
            try
            {
                backup = Path.Combine(Path.GetTempPath(), $"clip-undo-{Guid.NewGuid():N}{Path.GetExtension(item.AssetPath)}");
                File.Copy(item.AssetPath, backup);
            }
            catch
            {
                // An unreadable asset must not block the delete itself; the undo will bring the
                // entry back without its payload, which is still better than no undo at all.
                backup = null;
            }
        }

        _item = item;
        _assetBackupPath = backup;
    }

    /// <summary>
    /// Puts the asset bytes back where the item expects them and hands the item over for
    /// re-adding. The buffer is empty afterwards.
    /// </summary>
    public ClipboardHistoryItem? TakeRestored()
    {
        var item = _item;
        var backup = _assetBackupPath;
        _item = null;
        _assetBackupPath = null;
        if (item is null)
        {
            return null;
        }

        if (backup is not null && !string.IsNullOrWhiteSpace(item.AssetPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.AssetPath)!);
                File.Copy(backup, item.AssetPath, overwrite: true);
            }
            catch
            {
                // Same stance as Remember: a payload that cannot come back should not stop the
                // entry itself from coming back.
            }

            TryDelete(backup);
        }

        return item;
    }

    /// <summary>Drops the remembered item and its asset backup (a delete that never happened).</summary>
    public void Forget()
    {
        if (_assetBackupPath is not null)
        {
            TryDelete(_assetBackupPath);
        }

        _item = null;
        _assetBackupPath = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A leftover temp file is harmless.
        }
    }
}
