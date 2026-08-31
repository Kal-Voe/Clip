using System.Runtime.InteropServices;

namespace Clip.Core;

/// <summary>
/// Builds CFSTR_SHELLIDLIST ("Shell IDList Array"), the format Explorer reads when files are
/// dragged between shell windows.
///
/// It exists here for one reason: CF_HDROP is not the only way to hand Explorer a file, and it is
/// the one that has side effects. A data object carrying CF_HDROP tells every app in the drag
/// "this is a file", and the Chromium-based ones (VS Code, Slack, Electron in general) act on that
/// in preference to the text sitting right next to it. A CIDA says the same thing to the shell and
/// nothing at all to anyone else, so a text clip can offer both a real file to the desktop and
/// plain text to a text box without the two fighting.
///
/// The structure is a CIDA — a count, then an offset per PIDL, then the PIDLs themselves — laid
/// out in one flat block:
///
///   UINT cidl; UINT aoffset[cidl + 1]; ... PIDL bytes ...
///
/// aoffset[0] points at the parent folder's PIDL and the rest at the children, each relative to
/// its parent. The parent used here is the desktop, whose PIDL is the empty ID list, which makes
/// every child a fully-qualified PIDL. That is what lets one CIDA name files that live in
/// different folders, which a real parent folder could not.
/// </summary>
public static class ShellIdList
{
    /// <summary>CFSTR_SHELLIDLIST. The clipboard format name, registered by whoever asks first.</summary>
    public const string FormatName = "Shell IDList Array";

    /// <summary>
    /// The CIDA bytes for these paths, or null when none of them resolve to a shell item — a
    /// missing file is not worth failing a drag over, and the caller has other formats to offer.
    /// </summary>
    public static byte[]? Build(IReadOnlyList<string> paths)
    {
        var children = new List<byte[]>(paths.Count);
        foreach (var path in paths)
        {
            if (ParseToPidlBytes(path) is { } bytes)
            {
                children.Add(bytes);
            }
        }

        return children.Count == 0 ? null : Pack(children);
    }

    /// <summary>
    /// Lays a parsed set of child PIDLs out as a CIDA rooted at the desktop. Separate from
    /// <see cref="Build"/> because this half is arithmetic and can be checked without a shell.
    /// </summary>
    internal static byte[] Pack(IReadOnlyList<byte[]> children)
    {
        // The desktop's PIDL is the empty ID list: just the two-byte terminator.
        var parent = new byte[] { 0, 0 };

        // cidl, then one offset for the parent and one per child.
        var header = sizeof(uint) * (children.Count + 2);
        var total = header + parent.Length + children.Sum(child => child.Length);

        var buffer = new byte[total];
        WriteUInt32(buffer, 0, (uint)children.Count);

        var cursor = header;
        WriteUInt32(buffer, sizeof(uint), (uint)cursor);
        parent.CopyTo(buffer, cursor);
        cursor += parent.Length;

        for (var i = 0; i < children.Count; i++)
        {
            WriteUInt32(buffer, sizeof(uint) * (i + 2), (uint)cursor);
            children[i].CopyTo(buffer, cursor);
            cursor += children[i].Length;
        }

        return buffer;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), value);

    /// <summary>
    /// The shell's own PIDL for a path, copied out into managed bytes and freed immediately, so
    /// nothing here outlives the call — the drag carries a snapshot, not a pointer.
    /// </summary>
    private static byte[]? ParseToPidlBytes(string path)
    {
        var pidl = IntPtr.Zero;
        try
        {
            SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            return pidl == IntPtr.Zero ? null : CopyPidl(pidl);
        }
        catch (Exception)
        {
            // The path does not name a shell item (gone, or on a device that will not answer).
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                // SHParseDisplayName allocates with the task allocator, which is what ILFree is.
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    /// <summary>
    /// A PIDL is a run of SHITEMIDs — a two-byte length that counts itself, then that many bytes —
    /// ending in a length of zero. Walking it is the only way to learn how long it is; there is no
    /// header saying so.
    /// </summary>
    private static byte[] CopyPidl(IntPtr pidl)
    {
        var offset = 0;
        while (true)
        {
            var cb = (ushort)Marshal.ReadInt16(pidl, offset);
            if (cb == 0)
            {
                break;
            }

            offset += cb;
        }

        // Include the terminator: a child PIDL in a CIDA is a complete ID list of its own.
        var size = offset + sizeof(ushort);
        var bytes = new byte[size];
        Marshal.Copy(pidl, bytes, 0, size);
        return bytes;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(
        string name,
        IntPtr bindContext,
        out IntPtr pidl,
        uint sfgaoIn,
        out uint sfgaoOut);
}
