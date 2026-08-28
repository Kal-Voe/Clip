namespace Clip.Core;

/// <summary>
/// Password managers (Bitwarden, KeePass, 1Password, ...) mark transient secrets on the
/// clipboard with registered formats that ask monitors not to record them. Both capture
/// paths — the headless Watcher and the Shell — must honor them, so the check lives here.
/// Callers hand in the data-object's own format probes so this stays free of any
/// WinForms/WPF dependency and unit-testable.
/// </summary>
public static class ClipboardPrivacyFormats
{
    /// <summary>Mere presence of this format means "do not process"; the value is irrelevant.</summary>
    public const string ExcludeFromMonitorProcessing = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>
    /// Presence with DWORD 0 means "do not add to history"; a nonzero DWORD explicitly allows it.
    /// (This is the Windows cloud-clipboard opt-out contract — see the Win32 clipboard format docs.)
    /// </summary>
    public const string CanIncludeInClipboardHistory = "CanIncludeInClipboardHistory";

    /// <summary>Older de-facto convention (KeePass et al.): presence alone means "ignore this copy".</summary>
    public const string ClipboardViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>
    /// True when the current clipboard contents ask not to be recorded. <paramref name="isFormatPresent"/>
    /// and <paramref name="getFormatData"/> are typically the data object's GetDataPresent/GetData
    /// method groups (identical shape on WinForms and WPF IDataObject).
    /// </summary>
    public static bool ShouldExcludeFromHistory(Func<string, bool> isFormatPresent, Func<string, object?> getFormatData)
    {
        if (isFormatPresent(ExcludeFromMonitorProcessing) || isFormatPresent(ClipboardViewerIgnore))
        {
            return true;
        }

        // Presence with an unreadable value fails closed: an app that bothered to set the
        // format clearly intended an opt-out, and recording a password by accident is the
        // worse failure mode.
        return isFormatPresent(CanIncludeInClipboardHistory) &&
            IsDwordZeroOrUnreadable(getFormatData(CanIncludeInClipboardHistory));
    }

    private static bool IsDwordZeroOrUnreadable(object? data)
    {
        switch (data)
        {
            case int value:
                return value == 0;
            case uint value:
                return value == 0;
            case byte[] bytes:
                return bytes.Length < 4 || BitConverter.ToUInt32(bytes, 0) == 0;
            case Stream stream:
                // Custom registered formats come back from GetData as a MemoryStream over the
                // raw HGLOBAL bytes; the DWORD is the first four little-endian bytes.
                var buffer = new byte[4];
                return stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) < 4 ||
                    BitConverter.ToUInt32(buffer, 0) == 0;
            default:
                return true;
        }
    }
}
