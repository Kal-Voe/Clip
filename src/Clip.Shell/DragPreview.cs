using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace Clip.Shell;

/// <summary>
/// The visual that follows the cursor while a row is dragged out of the palette.
///
/// Ported from WinShot's <c>WinShot.Core.DragPreview</c>, where the two facts behind this design
/// were already paid for once: WPF has no built-in drag image, and the shell's
/// <c>IDragSourceHelper</c> is unusable from here — neither WPF's nor WinForms' DataObject
/// implements the COM <c>SetData</c> it calls back into, both return E_NOTIMPL. What is left is
/// this: a click-through topmost window, repositioned from the drag source's GiveFeedback event.
///
/// Built for smoothness. One instance is created per palette and reused — Show/Hide, never
/// create/close. It is fully opaque so WPF keeps the hardware render path: AllowsTransparency
/// would force software rendering and the preview would judder behind the cursor. Moves go
/// straight to SetWindowPos in physical pixels, which is also what keeps it under the cursor
/// across monitors at different scale factors — logical coordinates would drift the moment the
/// drag crossed onto a monitor with a different DPI.
///
/// WS_EX_TRANSPARENT is what makes it safe to fly under the cursor: the window is never hit-test
/// hit, so it can never become the drop target itself. WS_EX_NOACTIVATE and WS_EX_TOOLWINDOW keep
/// it out of activation, the taskbar and Alt+Tab.
///
/// Deliberately not owned by the palette. The palette cloaks itself on the first feedback tick of
/// every drag, and an owned window would go with it.
/// </summary>
internal sealed class DragPreview : IDisposable
{
    /// <summary>Longest edge of an image preview, in logical pixels. WinShot's number.</summary>
    internal const int MaxImageEdge = 180;

    /// <summary>The most of a clip's first line a text card keeps.</summary>
    internal const int MaxLabelWords = 5;
    internal const int MaxLabelChars = 28;

    /// <summary>Kept below-right of the hotspot so the cursor and its drop badge stay readable.</summary>
    private const int CursorOffsetX = 12;
    private const int CursorOffsetY = 12;

    private readonly Window _window;
    private IntPtr _handle;
    private bool _visible;
    private bool _disposed;

    public DragPreview()
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            // Placeholder only: every Show replaces it with the palette's own surface brush. A
            // window without AllowsTransparency has to have an opaque brush or it paints nothing.
            Background = WpfBrushes.Black,
        };

        // Realize the HWND up front so the first drag doesn't pay for window creation.
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        nint exStyle = GetWindowLongPtr(_handle, GwlExStyle);
        SetWindowLongPtr(_handle, GwlExStyle, exStyle | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>
    /// The label on a text card: the first line of a clip, cut to a few words.
    ///
    /// Pure, and the only part of the preview decidable without a desktop. Leading blank lines are
    /// skipped rather than producing an empty card — copied text routinely starts with a newline.
    /// The ellipsis is the honest signal that something was left out, so it appears whenever the
    /// label is not the whole clip: more words, more characters, or more lines. An empty result
    /// means there is nothing worth showing, which is the caller's signal to drag without a
    /// preview at all.
    /// </summary>
    internal static string CardLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var first = string.Empty;
        var moreLines = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (first.Length == 0)
            {
                first = trimmed;
                continue;
            }

            moreLines = true;
            break;
        }

        // Joining the split words rather than slicing the line also collapses runs of tabs and
        // spaces, so a pasted table doesn't arrive as a label with a hole in the middle.
        var words = first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var label = string.Join(' ', words.Take(MaxLabelWords));
        var clipped = moreLines || words.Length > MaxLabelWords;

        if (label.Length > MaxLabelChars)
        {
            // Cut at the last word boundary that fits so the label doesn't end mid-word — unless
            // the first word alone is already too long, which only a hard cut can shorten.
            var boundary = label.LastIndexOf(' ', MaxLabelChars);
            label = boundary > 0 ? label[..boundary] : label[..MaxLabelChars];
            clipped = true;
        }

        return clipped ? label + "…" : label;
    }

    /// <summary>
    /// Shows <paramref name="content"/> at the cursor. <paramref name="background"/> is what fills
    /// the window behind it: the window cannot be transparent (see the class comment), so a card
    /// with rounded corners has to be handed the same brush it paints itself with, or its corners
    /// would show as four bright notches.
    /// </summary>
    public void Show(FrameworkElement content, WpfBrush background)
    {
        if (_disposed)
        {
            return;
        }

        _window.Background = background;
        _window.Content = content;
        // Hiding goes through ShowWindow rather than WPF Visibility, so the window stays "visible"
        // to WPF and keeps laying out — but the swap above is queued, and showing before it runs
        // would flash one frame at the previous item's size.
        _window.UpdateLayout();

        MoveToCursor(activate: true);
        if (!_visible)
        {
            _window.Show();
            _visible = true;
        }
        else
        {
            ShowWindow(_handle, SwShowNoActivate);
        }
    }

    /// <summary>Snaps the preview to the cursor, in physical pixels so mixed-DPI setups don't drift.</summary>
    public void MoveToCursor() => MoveToCursor(activate: false);

    private void MoveToCursor(bool activate)
    {
        if (_disposed || _handle == IntPtr.Zero || !GetCursorPos(out PointL cursor))
        {
            return;
        }

        // Re-asserting topmost on every move churns the z-order for nothing; only do it on show.
        uint flags = SwpNoSize | SwpNoActivate | (activate ? 0 : SwpNoZOrder);
        SetWindowPos(_handle, activate ? HwndTopmost : IntPtr.Zero,
            cursor.X + CursorOffsetX, cursor.Y + CursorOffsetY, 0, 0, flags);
    }

    public void Hide()
    {
        if (_disposed || !_visible)
        {
            return;
        }

        ShowWindow(_handle, SwHide);
        // Drops the decoded bitmap along with the element holding it; the window itself stays.
        _window.Content = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle = IntPtr.Zero;
        _window.Content = null;
        _window.Close();
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointL point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr handle, int index, nint value);
}
