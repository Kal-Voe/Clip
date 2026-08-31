namespace Clip.Core;

/// <summary>
/// The formats one item contributes to a drag's data object.
///
/// A drop target picks whichever format it understands and ignores the rest, so the answer to
/// "will this drop anywhere" is decided entirely here rather than at the target. Hence the
/// belt-and-braces pairs: files carry their paths <em>and</em> the same paths as text, so a text
/// box gets something rather than rejecting the drag outright, and an image carries its pixels
/// <em>and</em> a path to the asset on disk, because plenty of apps accept a dropped picture only
/// as a file.
/// </summary>
public sealed record ClipboardDragPayload(
    ClipboardPastePayload? Text,
    IReadOnlyList<string> FilePaths,
    string? BitmapPath)
{
    public static readonly ClipboardDragPayload Empty = new(null, [], null);

    /// <summary>Nothing to hand the OS, so there is no drag to start.</summary>
    public bool IsEmpty => Text is null && FilePaths.Count == 0 && BitmapPath is null;
}

public static class ClipboardDragData
{
    /// <summary>
    /// Builds the drag payload for an item. The text side goes through
    /// <see cref="ClipboardPasteData.Create"/>, the same call the copy and paste paths make, so a
    /// drag and a paste of the same row agree on plain-versus-rich rather than drifting apart.
    /// </summary>
    public static ClipboardDragPayload Create(ClipboardHistoryItem item, PasteFormatPreference preference)
    {
        switch (item.Kind)
        {
            case ClipboardItemKind.Image when !string.IsNullOrWhiteSpace(item.AssetPath):
                // No text for an image: a text box that accepted the drag would insert the asset's
                // temp path, which is never what dragging a screenshot was meant to mean.
                return new ClipboardDragPayload(null, [item.AssetPath!], item.AssetPath);

            case ClipboardItemKind.Files when item.FilePaths.Count > 0:
                return new ClipboardDragPayload(
                    new ClipboardPastePayload(string.Join(Environment.NewLine, item.FilePaths), null, null),
                    [.. item.FilePaths],
                    null);

            case ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color:
                var text = ClipboardPasteData.Create(item, preference);
                return string.IsNullOrEmpty(text.Text)
                    ? ClipboardDragPayload.Empty
                    : new ClipboardDragPayload(text, [], null);

            default:
                return ClipboardDragPayload.Empty;
        }
    }
}
