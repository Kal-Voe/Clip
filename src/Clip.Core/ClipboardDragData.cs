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

    /// <summary>
    /// The payload for a drag of several rows at once. A drag carries exactly one data object
    /// however many rows started it, so every item's contribution is folded into one here.
    ///
    /// FileDrop is the union of every path the selection offers, in the order the rows are on
    /// screen, which is what makes dragging three screenshots into a folder land three files.
    /// The text side is those items' texts joined by newlines, and always plain: HTML and RTF are
    /// whole documents with their own headers, and gluing two of them together produces a third
    /// that is neither. A mixed selection therefore hands a file target the files and a text
    /// target the texts, each getting the part of the selection it can take rather than the drag
    /// refusing to start. The bitmap is dropped: CF_BITMAP holds one image, and picking which of
    /// several it should be would be a guess — several images travel as files instead.
    /// </summary>
    public static ClipboardDragPayload CreateMany(
        IReadOnlyList<ClipboardHistoryItem> items,
        PasteFormatPreference preference)
    {
        // One item is the ordinary drag, rich formats and bitmap included; nothing above applies.
        if (items.Count == 1)
        {
            return Create(items[0], preference);
        }

        var paths = new List<string>();
        var texts = new List<string>();
        foreach (var item in items)
        {
            var payload = Create(item, preference);
            paths.AddRange(payload.FilePaths);
            if (payload.Text is { Text.Length: > 0 } text)
            {
                texts.Add(text.Text);
            }
        }

        if (paths.Count == 0 && texts.Count == 0)
        {
            return ClipboardDragPayload.Empty;
        }

        return new ClipboardDragPayload(
            texts.Count == 0 ? null : new ClipboardPastePayload(string.Join(Environment.NewLine, texts), null, null),
            paths,
            null);
    }
}
