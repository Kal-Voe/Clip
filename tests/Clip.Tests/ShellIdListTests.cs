using Clip.Core;

namespace Clip.Tests;

public sealed class ShellIdListTests
{
    /// <summary>Reads the header back the way the shell does: cidl, then one offset each.</summary>
    private static (uint Count, uint[] Offsets) Header(byte[] cida)
    {
        var count = BitConverter.ToUInt32(cida, 0);
        var offsets = new uint[count + 1];
        for (var i = 0; i < offsets.Length; i++)
        {
            offsets[i] = BitConverter.ToUInt32(cida, sizeof(uint) * (i + 1));
        }

        return (count, offsets);
    }

    [Fact]
    public void TheParentIsTheDesktopSoEveryChildIsFullyQualified()
    {
        var cida = ShellIdList.Pack([[4, 0, 1, 2, 0, 0]]);
        var (count, offsets) = Header(cida);

        Assert.Equal(1u, count);

        // The desktop's PIDL is the empty ID list: the terminator and nothing else. That is what
        // lets the children be absolute, and so lets one CIDA name files in different folders.
        Assert.Equal([0, 0], cida[(int)offsets[0]..((int)offsets[0] + 2)]);
    }

    [Fact]
    public void EachChildLandsWhereItsOffsetSaysItDoes()
    {
        byte[] first = [4, 0, 9, 9, 0, 0];
        byte[] second = [6, 0, 7, 7, 7, 7, 0, 0];

        var cida = ShellIdList.Pack([first, second]);
        var (count, offsets) = Header(cida);

        Assert.Equal(2u, count);
        Assert.Equal(first, cida[(int)offsets[1]..((int)offsets[1] + first.Length)]);
        Assert.Equal(second, cida[(int)offsets[2]..((int)offsets[2] + second.Length)]);

        // Nothing before the first PIDL and nothing after the last: a CIDA is exactly its header
        // plus its ID lists, and a shell that reads past the end reads somebody else's memory.
        Assert.Equal(sizeof(uint) * 4, (int)offsets[0]);
        Assert.Equal(cida.Length, (int)offsets[2] + second.Length);
    }

    [Fact]
    public void ARealFileParsesIntoSomethingTheShellCouldFollow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClipShellIdListTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "note.txt");
        File.WriteAllText(path, "hello");

        try
        {
            var cida = ShellIdList.Build([path]);
            Assert.NotNull(cida);

            var (count, offsets) = Header(cida);
            Assert.Equal(1u, count);

            // A real PIDL for a file three folders deep is never trivially short, and it ends in
            // the two-byte terminator that makes it a complete ID list rather than a fragment.
            var child = cida[(int)offsets[1]..];
            Assert.True(child.Length > 8);
            Assert.Equal([0, 0], child[^2..]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void APathThatNamesNothingIsNoCidaAtAll()
    {
        // Null rather than an empty CIDA: the caller's answer to "I could not build this" is to
        // leave the format off the drag entirely, and a cidl of zero would just puzzle the shell.
        Assert.Null(ShellIdList.Build([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gone.txt")]));
    }
}
