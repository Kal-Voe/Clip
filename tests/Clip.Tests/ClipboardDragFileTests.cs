using System.Text;
using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardDragFileExtensionTests
{
    [Theory]
    [InlineData(ClipboardItemKind.Text, ".txt")]
    [InlineData(ClipboardItemKind.Color, ".txt")]     // a colour is the hex string, so it is text
    [InlineData(ClipboardItemKind.Link, ".url")]      // the Windows internet shortcut
    public void EachKindThatCanBecomeAFileGetsItsOwnExtension(ClipboardItemKind kind, string expected)
    {
        Assert.Equal(expected, ClipboardDragFile.ExtensionFor(kind));
    }

    [Theory]
    [InlineData(ClipboardItemKind.Image)]
    [InlineData(ClipboardItemKind.Files)]
    public void WhatIsAlreadyAFileHasNothingToMaterialise(ClipboardItemKind kind)
    {
        // Null is the signal to leave the existing FileDrop alone: an image already carries its
        // stored asset and a Files clip already is files.
        Assert.Null(ClipboardDragFile.ExtensionFor(kind));
    }

    [Fact]
    public void ALinkBecomesAnInternetShortcut()
    {
        Assert.Equal(
            "[InternetShortcut]\r\nURL=https://example.com/a\r\n",
            ClipboardDragFile.BodyFor(ClipboardItemKind.Link, "  https://example.com/a  "));
    }

    [Fact]
    public void TextIsWrittenOutExactlyAsItStands()
    {
        Assert.Equal("  keep\tmy   spacing  ", ClipboardDragFile.BodyFor(ClipboardItemKind.Text, "  keep\tmy   spacing  "));
    }

    [Fact]
    public void TextGetsAByteOrderMarkAndAShortcutDoesNot()
    {
        // The whole point: Notepad reads a BOM-less .txt as ANSI and mangles anything non-ASCII,
        // while a BOM in front of "[InternetShortcut]" hides the section header from the INI
        // parser and leaves a shortcut pointing nowhere.
        Assert.NotEmpty(ClipboardDragFile.EncodingFor(ClipboardItemKind.Text).GetPreamble());
        Assert.NotEmpty(ClipboardDragFile.EncodingFor(ClipboardItemKind.Color).GetPreamble());
        Assert.Empty(ClipboardDragFile.EncodingFor(ClipboardItemKind.Link).GetPreamble());
    }
}

public sealed class ClipboardDragFileNameTests
{
    private static string Text(string content) => ClipboardDragFile.DeriveBaseName(ClipboardItemKind.Text, content);

    private static string Link(string content) => ClipboardDragFile.DeriveBaseName(ClipboardItemKind.Link, content);

    [Fact]
    public void ATextClipIsNamedAfterWhatItSays()
    {
        // The outcome this whole function exists for: a readable name on the desktop rather than
        // "Untitled.txt" or a guid.
        Assert.Equal("the quarterly numbers", Text("the quarterly numbers"));
    }

    [Fact]
    public void TheFirstFewWordsAreEnough()
    {
        var name = Text("the quarterly numbers are up eleven percent on last year and rising still");

        Assert.Equal("the quarterly numbers are up eleven", name);
        Assert.True(name.Length <= 40);
    }

    [Fact]
    public void OneEndlessWordIsCutWhereverItHasTo()
    {
        // No space to break on, so a hard cut is the only option; the alternative is a name of
        // almost nothing.
        Assert.Equal(new string('a', 40), Text(new string('a', 200)));
    }

    [Fact]
    public void AWrappedParagraphDoesNotBecomeANameFullOfGaps()
    {
        Assert.Equal("first line second line", Text("first line\r\n\r\n   second   line"));
    }

    [Theory]
    [InlineData(@"a/b\c:d*e?f""g<h>i|j", "a b c d e f g h i j")]
    [InlineData("tab\there", "tab here")]
    public void CharactersWindowsRefusesBecomeSpaces(string content, string expected)
    {
        // Spaces rather than nothing, so "a/b" reads as two things and not as "ab".
        Assert.Equal(expected, Text(content));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("///")]
    [InlineData("\0\0")]
    public void ContentThatSanitisesAwayToNothingStillGetsAName(string content)
    {
        Assert.Equal("Clip", Text(content));
    }

    [Theory]
    [InlineData(".hidden.", "hidden")]
    [InlineData("  padded  ", "padded")]
    [InlineData("trailing dot.", "trailing dot")]
    public void LeadingAndTrailingDotsAndSpacesGoAway(string content, string expected)
    {
        // The classic Windows trap: the shell strips these silently, so "notes ." and "notes"
        // would collide and a name of nothing but dots would be no name at all.
        Assert.Equal(expected, Text(content));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    public void ReservedDeviceNamesAreDefused(string content)
    {
        // Windows reserves these whatever the extension, so "CON.txt" is as unopenable as "CON".
        var name = Text(content);

        Assert.Equal(content + "_", name);
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("COM")]
    [InlineData("LPT")]
    public void NamesThatMerelyLookReservedAreLeftAlone(string content)
    {
        Assert.Equal(content, Text(content));
    }

    [Theory]
    [InlineData("café résumé", "café résumé")]
    [InlineData("日本語のテキスト", "日本語のテキスト")]
    [InlineData("emoji 🎉 party", "emoji 🎉 party")]
    public void UnicodeSurvives(string content, string expected)
    {
        // NTFS is happy with all of this, and mangling it would defeat the point of naming the
        // file after its content.
        Assert.Equal(expected, Text(content));
    }

    [Fact]
    public void ALinkIsNamedAfterItsHost()
    {
        Assert.Equal("example.com", Link("https://example.com/some/very/long/path?q=1#frag"));
    }

    [Fact]
    public void TheWwwNobodyReadsIsDropped()
    {
        Assert.Equal("example.com", Link("https://www.example.com/"));
    }

    [Fact]
    public void ABareHostStillParses()
    {
        // Clipboards are full of URLs with no scheme; they are not absolute URIs, so the parser
        // needs one lent to it before it gives up.
        Assert.Equal("example.com", Link("example.com/a/b"));
    }

    [Fact]
    public void SomethingUnparseableFallsBackToTheTextItself()
    {
        // A link clip is only ever as well-formed as what was copied, and half a URL still names
        // the file better than "Clip" does.
        Assert.Equal("not a url at all", Link("not a url at all"));
    }
}

public sealed class ClipboardDragFileUniqueNameTests
{
    [Fact]
    public void AFreeNameIsUsedAsItIs()
    {
        Assert.Equal("notes.txt", ClipboardDragFile.UniqueFileName("notes", ".txt", _ => false));
    }

    [Fact]
    public void ATakenNameGainsACounter()
    {
        Assert.Equal("notes (2).txt", ClipboardDragFile.UniqueFileName("notes", ".txt", n => n == "notes.txt"));
    }

    [Fact]
    public void TheCounterKeepsClimbingPastEveryTakenName()
    {
        var taken = new HashSet<string> { "notes.txt", "notes (2).txt", "notes (3).txt" };

        Assert.Equal("notes (4).txt", ClipboardDragFile.UniqueFileName("notes", ".txt", taken.Contains));
    }
}

public sealed class ClipboardDragFileMaterialiseTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "clip-drag-file-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            // TestTemp.Delete retries the handle-still-open case rather than giving up on the
            // first throw. The bare catch below already made this teardown safe; going through
            // the shared helper makes it actually clean up.
            TestTemp.Delete(_folder);
        }
        catch
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }

    [Fact]
    public void TextLandsOnDiskUnderAReadableNameWithABom()
    {
        var path = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "the quarterly numbers");

        Assert.NotNull(path);
        Assert.Equal("the quarterly numbers.txt", Path.GetFileName(path));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path!).Take(3).ToArray());
        Assert.Equal("the quarterly numbers", File.ReadAllText(path!, Encoding.UTF8));
    }

    [Fact]
    public void NonAsciiTextRoundTripsThroughTheBom()
    {
        var path = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "café — 日本語");

        Assert.Equal("café — 日本語", File.ReadAllText(path!, Encoding.UTF8));
    }

    [Fact]
    public void ALinkLandsAsAnInternetShortcutWithNoBomInFrontOfTheSectionHeader()
    {
        var path = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Link, "https://www.example.com/a");

        Assert.Equal("example.com.url", Path.GetFileName(path));
        Assert.Equal((byte)'[', File.ReadAllBytes(path!)[0]);
        Assert.Equal("[InternetShortcut]\r\nURL=https://www.example.com/a\r\n", File.ReadAllText(path!));
    }

    [Fact]
    public void DraggingTheSameClipTwiceReusesItsFile()
    {
        var first = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "same thing");
        var second = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "same thing");

        // Otherwise five drags of one row leave "same thing (2)" through "same thing (5)" behind.
        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void TwoClipsThatWantTheSameNameBothGetOne()
    {
        var first = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "notes");
        var second = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "notes and more");

        Assert.Equal("notes.txt", Path.GetFileName(first));
        Assert.Equal("notes and more.txt", Path.GetFileName(second));

        // Now one that really does collide: the trailing dot is stripped from the name but not
        // from the content, so this wants "notes.txt" while holding something else.
        var third = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "notes.");

        Assert.Equal("notes (2).txt", Path.GetFileName(third));
    }

    [Theory]
    [InlineData(ClipboardItemKind.Image)]
    [InlineData(ClipboardItemKind.Files)]
    public void KindsThatAreAlreadyFilesWriteNothing(ClipboardItemKind kind)
    {
        Assert.Null(ClipboardDragFile.Materialize(_folder, kind, "anything"));
        Assert.False(Directory.Exists(_folder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyContentWritesNothing(string text)
    {
        Assert.Null(ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, text));
    }

    [Fact]
    public void TheSweepTakesTheStaleAndLeavesTheRest()
    {
        var fresh = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "fresh")!;
        var stale = ClipboardDragFile.Materialize(_folder, ClipboardItemKind.Text, "stale")!;
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - ClipboardDragFile.StaleAfter - TimeSpan.FromMinutes(1));

        ClipboardDragFile.CleanStale(_folder, DateTime.UtcNow);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void SweepingAFolderThatWasNeverUsedIsFine()
    {
        // The first drag of a fresh install sweeps before it writes.
        ClipboardDragFile.CleanStale(Path.Combine(_folder, "never"), DateTime.UtcNow);
    }
}
