using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using WpfDataObject = System.Windows.DataObject;
using WpfDataFormats = System.Windows.DataFormats;
using WpfIDataObject = System.Windows.IDataObject;

namespace Clip.Shell;

/// <summary>
/// A drag payload that answers "which formats do you have?" differently depending on what the
/// pointer is over at the moment it is asked.
///
/// The problem this solves: a text clip wants to arrive as text in a text field and as a .txt on
/// the desktop, and one static data object cannot be both. A drop target picks whichever format
/// it likes best out of everything advertised, and the apps people drag into — Chromium (Claude,
/// ChatGPT, Slack), VS Code — prefer a file whenever one is on offer, so the text drop turned
/// into an attachment. Format ordering cannot steer that choice: WPF keeps its formats in a hash
/// table, so enumeration order is not insertion order and is not even stable between runs, and
/// those apps ask for CF_HDROP by name anyway. The CIDA/shell-id-list route was tried and Explorer
/// refused it three times out of three.
///
/// What is left is the one degree of freedom OLE actually gives a drag source: the target asks
/// these questions <em>live</em>, through the data object, while the pointer is already over it —
/// <c>QueryGetData</c>, <c>EnumFormatEtc</c> and <c>GetData</c> are all calls into this object
/// during the drag. So the answer can depend on the window under the cursor at call time.
///
/// The default is "yes, there is a file", and that is the part that was got wrong first and is
/// load-bearing. Not every target asks live. Chromium does — it calls in on every DragOver, so
/// answering "no file" while the pointer is over it is what makes the text land in the input
/// field. Explorer does not: it takes one look at the format list at the moment the drag starts,
/// while the cursor is still over the palette, and never asks again. Measured, not assumed —
/// with the answer keyed on the class alone, Explorer made <em>zero</em> calls into this object
/// for the whole drag and refused the drop 3/3, and the same drag with the file always advertised
/// dropped a .txt 3/3. So the pointer being over one of Clip's own windows answers yes, which is
/// what puts the file in the snapshot Explorer reads. Over anything that is not the shell and not
/// ours, the answer is no — and the only targets that ever see that answer are the ones that
/// bothered to ask.
///
/// Only the <em>materialised</em> file is hidden this way — the .txt or .url Clip invented for the
/// drop. Clips that genuinely are files (a Files clip, an image's stored asset) keep advertising
/// their paths to everyone, exactly as before, because dragging a screenshot into Slack is meant
/// to attach the screenshot.
///
/// Implemented as WPF's <see cref="System.Windows.IDataObject"/> rather than the COM one on
/// purpose. WPF wraps a non-COM IDataObject in its own <see cref="System.Windows.DataObject"/>,
/// whose COM side answers every one of those live calls by asking this object again — so the
/// dynamic answer reaches the shell while all the HGLOBAL and HDROP marshalling stays WPF's
/// problem, and the drag itself is still the ordinary <see cref="System.Windows.DragDrop"/> one
/// with its GiveFeedback tick, its preview window and its cancel path intact.
/// </summary>
internal sealed class ShellAwareDragData : WpfIDataObject
{
    private readonly WpfDataObject _inner;
    private readonly string[] _shellOnlyFiles;
    private readonly Func<bool> _pointerOverShell;

    internal ShellAwareDragData(WpfDataObject inner, string shellOnlyFile)
        : this(inner, [shellOnlyFile], PointerIsOverShellTarget)
    {
    }

    /// <summary>The seam the tests use: the "is the pointer over Explorer" question, injected.</summary>
    internal ShellAwareDragData(WpfDataObject inner, string[] shellOnlyFiles, Func<bool> pointerOverShell)
    {
        _inner = inner;
        _shellOnlyFiles = shellOnlyFiles;
        _pointerOverShell = pointerOverShell;
    }

    /// <summary>
    /// Whether the file is on offer right now. Guarded because this runs inside the OLE loop on
    /// every DragOver: an exception escaping here would take the drag down with it, and "no file"
    /// is the safe answer — the text still drops.
    /// </summary>
    private bool FilesOffered()
    {
        try
        {
            return _pointerOverShell();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileDrop(string format) =>
        string.Equals(format, WpfDataFormats.FileDrop, StringComparison.Ordinal);

    public string[] GetFormats() => GetFormats(autoConvert: true);

    public string[] GetFormats(bool autoConvert)
    {
        var formats = _inner.GetFormats(autoConvert);
        return FilesOffered() ? [.. formats, WpfDataFormats.FileDrop] : formats;
    }

    public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);

    public bool GetDataPresent(string format, bool autoConvert) =>
        IsFileDrop(format) ? FilesOffered() : _inner.GetDataPresent(format, autoConvert);

    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);

    public object? GetData(string format) => GetData(format, autoConvert: true);

    public object? GetData(string format, bool autoConvert) =>
        IsFileDrop(format)
            ? FilesOffered() ? _shellOnlyFiles : null
            : _inner.GetData(format, autoConvert);

    public object? GetData(Type format) => GetData(format.FullName!);

    public void SetData(object data) => _inner.SetData(data);

    public void SetData(string format, object data) => _inner.SetData(format, data);

    public void SetData(Type format, object data) => _inner.SetData(format, data);

    public void SetData(string format, object data, bool autoConvert) =>
        _inner.SetData(format, data, autoConvert);

    /// <summary>
    /// Whether a file is worth offering to whatever is under the cursor: the shell — an Explorer
    /// folder window or the desktop, where a dropped file becomes a real file — or one of Clip's
    /// own windows, which is where the cursor is when the drag starts and the snapshot is taken.
    ///
    /// The cursor position is read live rather than taken from the drag, because the whole point
    /// is that this is asked again each time the pointer enters a new target. The drag preview
    /// flies under the cursor but is WS_EX_TRANSPARENT, so hit testing looks straight through it.
    ///
    /// The classes were checked against the real thing on this machine rather than taken from a
    /// list: a folder window's root is CabinetWClass (ExploreWClass is the old two-pane window,
    /// kept because it still exists), and every point on the desktop hit-tests to a SysListView32
    /// whose root is Progman. WorkerW is the same desktop when the wallpaper host has taken the
    /// listview over — the arrangement Windows switches to for slideshows and Spotlight.
    /// </summary>
    internal static bool PointerIsOverShellTarget()
    {
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        // The hit is on a child — DesktopChildSiteBridge inside a folder window, SysListView32 on
        // the desktop — and it is the top-level window that carries the class name worth reading.
        var root = GetAncestor(window, GaRoot);
        if (root == IntPtr.Zero)
        {
            root = window;
        }

        // Clip's own palette is what the cursor is over when the drag starts, and that is when the
        // shell takes its one snapshot. Anything of ours answers yes.
        GetWindowThreadProcessId(root, out var owner);
        if (owner == CurrentProcessId)
        {
            return true;
        }

        return ClassNameOf(root) is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";
    }

    private static string ClassNameOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(window, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private const uint GaRoot = 2;

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder buffer, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
