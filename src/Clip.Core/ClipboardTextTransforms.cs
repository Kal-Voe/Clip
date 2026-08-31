using System.Globalization;
using System.Text.RegularExpressions;

namespace Clip.Core;

/// <summary>
/// The pure string rewrites behind the item menu's Transform submenu.
///
/// Every one of these copies its result to the clipboard rather than editing the stored item, so
/// they are deliberately free of any history or clipboard knowledge: a transform is a function
/// from the item's text to a new string, nothing more. The menu decides which ones are worth
/// offering by comparing the result to the input, which only works because they are pure.
/// </summary>
public static partial class ClipboardTextTransforms
{
    public static string Upper(string? text) => text?.ToUpperInvariant() ?? string.Empty;

    public static string Lower(string? text) => text?.ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Lowercased first on purpose: ToTitleCase leaves an all-caps word alone, treating it as an
    /// acronym, so "HELLO WORLD" would come back unchanged and the menu would hide the entry.
    /// </summary>
    public static string TitleCase(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());

    public static string Trim(string? text) => text?.Trim() ?? string.Empty;

    /// <summary>
    /// Every run of whitespace — CRLF, LF, tabs, runs of spaces — becomes one space, so a wrapped
    /// paragraph pasted into a single-line field arrives as one line rather than as text with the
    /// line breaks merely swapped for spaces.
    /// </summary>
    public static string SingleLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : WhitespaceRunRegex().Replace(text, " ").Trim();

    /// <summary>
    /// One link per line, in the order they appear. Splitting on whitespace and asking
    /// <see cref="ClipboardLinkDetector"/> about each token reuses the same definition of "link"
    /// the capture path uses — including its trailing-punctuation trim, which is what stops a URL
    /// ending a sentence from swallowing the full stop.
    /// </summary>
    public static string ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var found = new List<string>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // The detector trims trailing punctuation but not leading, so a parenthesised or
            // quoted link would otherwise fail to parse as a URI.
            var candidate = token.TrimStart('(', '[', '{', '<', '"', '\'');
            if (ClipboardLinkDetector.TryNormalize(candidate, out var normalized))
            {
                found.Add(normalized);
            }
        }

        return string.Join(Environment.NewLine, found);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRunRegex();
}
