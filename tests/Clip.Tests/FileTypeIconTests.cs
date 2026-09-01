using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The two pure halves of the file-type icon path: what gets printed on the drawn document, and
/// how "Windows handed back its generic blank page" is decided. The shell call between them is not
/// testable and is not tested here.
/// </summary>
public sealed class FileTypeIconTests
{
    [Theory]
    [InlineData("vsdm", "VSD")]
    [InlineData(".vsdm", "VSD")]
    [InlineData("PDF", "PDF")]
    [InlineData("Docx", "DOC")]
    // A whole file name is accepted, and only the last segment is the extension.
    [InlineData("archive.tar.gz", "GZ")]
    [InlineData("report.final.v2.xlsx", "XLS")]
    // Three characters is the cap; anything longer is cut, never ellipsised.
    [InlineData("kicad_pcb", "KIC")]
    [InlineData(".gitignore", "GIT")]
    [InlineData("blend1", "BLE")]
    [InlineData("json", "JSO")]
    // Two and three come through whole.
    [InlineData("7z", "7Z")]
    [InlineData("cs", "CS")]
    [InlineData("mp3", "MP3")]
    public void LabelIsUppercaseAndCapped(string input, string expected)
    {
        Assert.Equal(expected, MainWindow.FileExtensionLabel(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("...")]
    public void NoExtensionGetsNoLabel(string? input)
    {
        // An empty label is what makes the glyph fall back to the plain ruled document.
        Assert.Equal(string.Empty, MainWindow.FileExtensionLabel(input));
    }

    [Fact]
    public void LabelKeepsUnicodeIntact()
    {
        Assert.Equal("ÜBE", MainWindow.FileExtensionLabel("über"));

        // Four astral-plane characters are four text elements, not eight chars: a cap counted in
        // chars would slice a surrogate pair in half and print a replacement box.
        var astral = char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0x1F601) +
                     char.ConvertFromUtf32(0x1F602) + char.ConvertFromUtf32(0x1F603);
        var kept = char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0x1F601) +
                   char.ConvertFromUtf32(0x1F602);
        Assert.Equal(kept, MainWindow.FileExtensionLabel(astral));

        // e + combining acute is one text element, so this is two and survives whole - counting
        // chars would have cut it to "E" plus a stranded accent.
        Assert.Equal("ÉÉ", MainWindow.FileExtensionLabel("éé"));
    }

    [Fact]
    public void IdenticalPixelsMeanWindowsHadNothing()
    {
        var reference = new byte[] { 1, 2, 3, 4 };
        Assert.True(MainWindow.IsUnknownDocumentIcon([1, 2, 3, 4], reference));
        Assert.False(MainWindow.IsUnknownDocumentIcon([1, 2, 3, 5], reference));
    }

    [Fact]
    public void DifferentSizesAreNotTheSamePicture()
    {
        // A candidate resolved at another size cannot be compared to the reference at all, so it
        // counts as a real icon rather than as the blank page.
        Assert.False(MainWindow.IsUnknownDocumentIcon([1, 2, 3, 4], [1, 2, 3, 4, 1, 2, 3, 4]));
    }

    [Fact]
    public void AnUnaskableQuestionNeverBlanksAnIcon()
    {
        // No reference, no pixels, or an empty icon: never claim Windows had nothing. Showing a
        // real icon we could not verify beats hiding one we could.
        Assert.False(MainWindow.IsUnknownDocumentIcon([1, 2, 3, 4], null));
        Assert.False(MainWindow.IsUnknownDocumentIcon(null, [1, 2, 3, 4]));
        Assert.False(MainWindow.IsUnknownDocumentIcon(null, null));
        Assert.False(MainWindow.IsUnknownDocumentIcon([], []));
    }
}
