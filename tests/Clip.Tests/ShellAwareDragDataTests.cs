using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using System.Windows;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The two facts the shell-aware drag rests on: this object flips FileDrop on and off with the
/// pointer, and WPF's own DataObject asks it again when the shell comes calling. The second one
/// is the load-bearing one — if <see cref="DataObject"/> answered QueryGetData from a snapshot
/// instead of from the wrapped object, the drop target would see one fixed set of formats and
/// the whole design would be dead. That is checked here through the same COM interface the OLE
/// drag loop uses.
/// </summary>
public sealed class ShellAwareDragDataTests
{
    private const short CfHdrop = 15;
    private const int DvEFormatEtc = unchecked((int)0x80040064);

    [Fact]
    public void FileDropAppearsAndDisappearsWithThePointer()
    {
        RunSta(() =>
        {
            var overShell = false;
            var data = Wrap(TextObject(), () => overShell);

            Assert.False(data.GetDataPresent(DataFormats.FileDrop));
            Assert.Null(data.GetData(DataFormats.FileDrop));
            Assert.DoesNotContain(DataFormats.FileDrop, data.GetFormats());

            overShell = true;

            Assert.True(data.GetDataPresent(DataFormats.FileDrop));
            Assert.Equal([@"C:\drag\note.txt"], (string[])data.GetData(DataFormats.FileDrop)!);
            Assert.Contains(DataFormats.FileDrop, data.GetFormats());
        });
    }

    /// <summary>The text is never hidden: that is the format that has to arrive everywhere.</summary>
    [Fact]
    public void TextIsOfferedWhereverThePointerIs()
    {
        RunSta(() =>
        {
            var data = Wrap(TextObject(), () => false);

            Assert.True(data.GetDataPresent(DataFormats.UnicodeText));
            Assert.Equal("hello", data.GetData(DataFormats.UnicodeText));
        });
    }

    /// <summary>A throwing pointer test must not take the drag down; no file is the safe answer.</summary>
    [Fact]
    public void APointerTestThatThrowsOffersNoFile()
    {
        RunSta(() =>
        {
            var data = Wrap(TextObject(), () => throw new InvalidOperationException("boom"));

            Assert.False(data.GetDataPresent(DataFormats.FileDrop));
            Assert.True(data.GetDataPresent(DataFormats.UnicodeText));
        });
    }

    /// <summary>
    /// The one that decides whether any of this reaches the shell: a live QueryGetData for
    /// CF_HDROP, asked of the WPF DataObject that DragDrop.DoDragDrop wraps this in, has to
    /// change its answer between calls.
    /// </summary>
    [Fact]
    public void WpfAsksAgainOnEveryComQuery()
    {
        RunSta(() =>
        {
            var overShell = false;
            var com = (ComDataObject)new DataObject(Wrap(TextObject(), () => overShell));
            var request = new FORMATETC
            {
                cfFormat = CfHdrop,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };

            Assert.Equal(DvEFormatEtc, com.QueryGetData(ref request));
            Assert.DoesNotContain(CfHdrop, EnumeratedFormats(com));

            overShell = true;

            Assert.Equal(0, com.QueryGetData(ref request));
            Assert.Contains(CfHdrop, EnumeratedFormats(com));
        });
    }

    private static List<short> EnumeratedFormats(ComDataObject com)
    {
        var enumerator = com.EnumFormatEtc(DATADIR.DATADIR_GET);
        var formats = new List<short>();
        var one = new FORMATETC[1];
        var fetched = new int[1];
        while (enumerator.Next(1, one, fetched) == 0 && fetched[0] == 1)
        {
            formats.Add(one[0].cfFormat);
        }

        return formats;
    }

    private static DataObject TextObject()
    {
        var inner = new DataObject();
        inner.SetText("hello", TextDataFormat.UnicodeText);
        return inner;
    }

    private static ShellAwareDragData Wrap(DataObject inner, Func<bool> overShell) =>
        new(inner, [@"C:\drag\note.txt"], overShell);

    /// <summary>WPF data objects want a single-threaded apartment; xUnit runs MTA, so hop threads.</summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }
}
