using System.Text;

namespace Clip.Core;

/// <summary>
/// Turns a clip into a real file on disk, so that dropping it on the desktop or into a folder
/// leaves something behind the way dropping an image already does.
///
/// The shell-native answer is a virtual file — CFSTR_FILEDESCRIPTORW plus CFSTR_FILECONTENTS,
/// rendered on demand and never touching disk. It is unreachable from here: the shell asks for
/// it through a COM <c>IDataObject</c>, and neither WPF's nor WinForms' DataObject implements the
/// COM <c>SetData</c> that path calls back into — both answer E_NOTIMPL. (WinShot hit the same
/// wall from the other direction when it tried to use IDragSourceHelper for its drag image.) So
/// the file is materialised up front and its path handed over as FileDrop, which is exactly the
/// mechanism that has always made a dragged image land on the desktop as a .png.
///
/// Everything here except <see cref="Materialize"/> and <see cref="CleanStale"/> is a pure
/// function of the clip, because the interesting part is the name: a desktop littered with
/// "Untitled.txt" is a worse outcome than no feature at all.
/// </summary>
public static class ClipboardDragFile
{
    /// <summary>
    /// Longest base name we will produce, before the extension. Long enough for a recognisable
    /// phrase, short enough that a folder full of them still reads at a glance — and far enough
    /// under MAX_PATH that a deep destination folder cannot push the copy over the limit.
    /// </summary>
    internal const int MaxBaseNameLength = 40;

    /// <summary>What to call a clip whose content sanitises away to nothing at all.</summary>
    internal const string FallbackName = "Clip";

    /// <summary>
    /// Temp files older than this are swept at the next drag. A day is well past the point where
    /// a drop could still be in flight, and it keeps the folder to roughly one day of drags
    /// rather than every clip ever dragged.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// The MS-DOS device names, which Windows still reserves at every level of every path and
    /// <em>whatever the extension</em> — "CON.txt" is as unopenable as "CON". Compared
    /// case-insensitively for the same reason.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// What a clip of this kind becomes on disk, or null when it is already a file (or is one
    /// on disk in its own right) and has nothing to materialise.
    /// </summary>
    public static string? ExtensionFor(ClipboardItemKind kind) => kind switch
    {
        // A .url internet shortcut is what Windows itself makes when you drag a link out of a
        // browser onto the desktop, so it is what a dropped link should leave behind here.
        ClipboardItemKind.Link => ".url",

        // A colour is text — the hex or rgb() string the user copied — so it gets the same
        // treatment as any other text.
        ClipboardItemKind.Text or ClipboardItemKind.Color => ".txt",

        // Images already carry their stored asset, and a Files clip already is files.
        _ => null,
    };

    /// <summary>
    /// The bytes-to-be. A link becomes the two-line INI that Windows recognises as an internet
    /// shortcut; everything else is written out as it stands.
    /// </summary>
    public static string BodyFor(ClipboardItemKind kind, string text) =>
        kind == ClipboardItemKind.Link
            // CRLF and no trailing blank line: this is read by the profile-string INI parser,
            // which is old enough to care.
            ? $"[InternetShortcut]\r\nURL={text.Trim()}\r\n"
            : text;

    /// <summary>
    /// UTF-8 either way, but the BOM is not optional either way.
    ///
    /// A .txt gets one: Notepad and most other Windows tools fall back to the ANSI code page
    /// without it, which turns every accent and em dash into mojibake. A .url must not have one:
    /// it is parsed as an INI file, and a BOM sitting in front of "[InternetShortcut]" hides the
    /// section header, leaving a shortcut that points nowhere.
    /// </summary>
    public static Encoding EncodingFor(ClipboardItemKind kind) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: kind != ClipboardItemKind.Link);

    /// <summary>
    /// The file name a clip earns from its own content: the first few words of the text, or the
    /// host of a link, scrubbed into something Windows will actually accept.
    /// </summary>
    public static string DeriveBaseName(ClipboardItemKind kind, string content)
    {
        var seed = kind == ClipboardItemKind.Link ? HostOf(content) : content;
        return Sanitize(seed);
    }

    /// <summary>
    /// The host of a link, minus the "www." nobody reads. Falls back to the raw text when the
    /// string will not parse as a URL — a link clip is only ever as well-formed as what was
    /// copied, and half a URL still names the file better than nothing does.
    /// </summary>
    private static string HostOf(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        // Scheme-relative and bare hosts ("example.com/x") are common in clipboards and are not
        // absolute URIs, so give the parser a scheme to work with before giving up on it.
        var candidate = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return trimmed;
        }

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>
    /// Squeezes arbitrary clipboard content into a legal Windows base name. Every rule here
    /// exists because some file explorer, somewhere, refuses the name without it.
    /// </summary>
    private static string Sanitize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return FallbackName;
        }

        // Invalid characters and control characters (a newline is both a separator and illegal)
        // collapse to spaces rather than vanishing, so "a/b" reads as "a b" not "ab".
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(content.Length);
        var pendingSpace = false;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0)
            {
                // Runs of whitespace and stripped characters together become one space, so a
                // wrapped paragraph does not become a name full of gaps.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        var name = Truncate(builder.ToString());

        // Leading and trailing dots and spaces are the classic Windows trap: the shell silently
        // strips them, so "notes ." and "notes" become the same file and a name that is nothing
        // but dots becomes no name at all.
        name = name.Trim().Trim('.').Trim();
        if (name.Length == 0)
        {
            return FallbackName;
        }

        // Trailing underscore rather than a rename, so "CON" is still recognisably the clip that
        // said CON.
        return ReservedNames.Contains(name) ? name + "_" : name;
    }

    /// <summary>
    /// Cuts an over-long name back at a word boundary where there is one near the limit, so a
    /// pasted paragraph gives "the quarterly numbers are" rather than "the quarterly numbers ar".
    /// </summary>
    private static string Truncate(string name)
    {
        if (name.Length <= MaxBaseNameLength)
        {
            return name;
        }

        var cut = name[..MaxBaseNameLength];
        var lastSpace = cut.LastIndexOf(' ');

        // Only honour the boundary if it leaves a useful amount of name behind; a single very
        // long word would otherwise be cut down to almost nothing.
        return lastSpace >= MaxBaseNameLength / 2 ? cut[..lastSpace] : cut;
    }

    /// <summary>
    /// The first name in the "name", "name (2)", "name (3)" series that <paramref name="taken"/>
    /// does not reject. The caller decides what "taken" means — see <see cref="Materialize"/>,
    /// where a file already holding this exact content is deliberately not taken.
    /// </summary>
    public static string UniqueFileName(string baseName, string extension, Func<string, bool> taken)
    {
        var first = baseName + extension;
        if (!taken(first))
        {
            return first;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseName} ({suffix}){extension}";
            if (!taken(candidate))
            {
                return candidate;
            }
        }

        // A thousand live collisions on one name is not a real situation; this exists so the loop
        // has an exit rather than because anyone will ever see it.
        return $"{baseName} ({Guid.NewGuid():N}){extension}";
    }

    /// <summary>
    /// Writes the clip into <paramref name="directory"/> and returns the path, or null when this
    /// kind has nothing to write. Throws only what the filesystem throws; the caller decides
    /// whether a failure is worth reporting, because a drag must still happen without it.
    /// </summary>
    public static string? Materialize(string directory, ClipboardItemKind kind, string text)
    {
        if (ExtensionFor(kind) is not { } extension || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Directory.CreateDirectory(directory);

        var body = BodyFor(kind, text);
        var encoding = EncodingFor(kind);
        var name = UniqueFileName(
            DeriveBaseName(kind, text),
            extension,
            candidate => IsTakenByOtherContent(Path.Combine(directory, candidate), body, encoding));

        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, body, encoding);
        }

        return path;
    }

    /// <summary>
    /// A name is only taken when a <em>different</em> clip owns it. Dragging the same row out
    /// five times should keep reusing its one file, not leave "notes (2)" through "notes (5)"
    /// behind it.
    /// </summary>
    private static bool IsTakenByOtherContent(string path, string body, Encoding encoding)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            return !string.Equals(File.ReadAllText(path, encoding), body, StringComparison.Ordinal);
        }
        catch
        {
            // Locked or unreadable: treat it as somebody else's and pick the next name.
            return true;
        }
    }

    /// <summary>
    /// Deletes drag files older than <see cref="StaleAfter"/>. Best effort throughout: a file
    /// that will not delete is a file the next sweep can try again, and none of this is worth
    /// failing a drag over.
    /// </summary>
    public static void CleanStale(string directory, DateTime utcNow)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(path) > StaleAfter)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Still open in whatever it was dropped into, or already gone.
                }
            }
        }
        catch
        {
            // The folder went away underneath us, or is unreadable. Nothing to clean either way.
        }
    }
}
