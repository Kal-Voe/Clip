using System.Globalization;
using System.Text.RegularExpressions;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The six header filter chips are two different controls — three split pills wrapped in a shell
/// Border, three bare buttons — that have to be indistinguishable to the eye. Nothing at runtime
/// notices when they drift apart, which is how the row ended up 30px outlined boxes next to 28px
/// floating labels, so the agreement is pinned here instead.
/// </summary>
public sealed class FilterChipTests
{
    [Fact]
    public void SelectedChipsTakeTheSelectedFillAndBorderWhicheverControlDrawsThem()
    {
        var (fill, border, foreground) = MainWindow.FilterChipBrushKeys(true);
        Assert.Equal("Selected", fill);
        Assert.Equal("SelectedBorder", border);
        Assert.Equal("Text", foreground);
    }

    [Fact]
    public void UnselectedChipsStillDrawTheirOutline()
    {
        // The load-bearing half: v1.1.14 asked the split pills to keep their full rectangle when
        // unselected. A null fill is transparent, but the border key must never be — a chip that
        // only outlines itself once chosen is the staggered header this replaced.
        var (fill, border, foreground) = MainWindow.FilterChipBrushKeys(false);
        Assert.Null(fill);
        Assert.Equal("Line2", border);
        Assert.Equal("Muted", foreground);
    }

    [Fact]
    public void EveryChipRoutesThroughSetFilterVisual()
    {
        // Three of these are pills and three are plain buttons; the plain ones are the easy ones to
        // forget, and forgetting one means selecting "Text" looks nothing like selecting "All".
        var source = File.ReadAllText(RepoPath("src", "Clip.Shell", "MainWindow.xaml.cs"));
        var start = source.IndexOf("private void UpdateFilterVisuals()", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow no longer has an UpdateFilterVisuals method.");

        var body = source[start..source.IndexOf("private void SetFilterVisual", StringComparison.Ordinal)];
        foreach (var chip in new[] { "AllButton", "TextButton", "ImageButton", "LinksButton", "ColorButton", "FilesButton" })
        {
            Assert.Contains($"SetFilterVisual({chip}", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void APillIsExactlyAsTallAsAPlainChip()
    {
        // A pill's outer height is its segment plus the shell's outline on both edges. The two
        // numbers live in different styles, so this is what stops a bump to one from putting the
        // row back out of alignment — and with it the label baselines, which land at half the
        // shared height either way only while the total matches.
        var xaml = File.ReadAllText(RepoPath("src", "Clip.Shell", "MainWindow.xaml"));
        var chip = StyleHeight(xaml, "FilterButton");

        foreach (var segment in new[] { "PillSegmentLeft", "PillSegmentRight" })
        {
            Assert.Equal(chip, StyleHeight(xaml, segment) + 2);
        }

        foreach (var shell in new[] { "AllFilterShell", "MediaFilterShell", "FilesFilterShell" })
        {
            Assert.Equal(1, Attribute(xaml, $"x:Name=\"{shell}\"", "BorderThickness"));
        }
    }

    [Fact]
    public void PlainChipsInheritTheSameOutlineBrushThePillShellsUse()
    {
        var xaml = File.ReadAllText(RepoPath("src", "Clip.Shell", "MainWindow.xaml"));
        Assert.Equal("{DynamicResource Line2}", Setter(xaml, "FilterButton", "BorderBrush"));

        // The icon buttons ride on FilterButton for its template and are not chips; boxing the
        // settings gear and the expand-image overlay was the one regression this could cause.
        Assert.Equal("Transparent", Setter(xaml, "IconButton", "BorderBrush"));
    }

    private static double StyleHeight(string xaml, string key) =>
        double.Parse(Setter(xaml, key, "Height"), CultureInfo.InvariantCulture);

    private static string Setter(string xaml, string styleKey, string property) =>
        Value(xaml, $"x:Key=\"{styleKey}\"", $"<Setter Property=\"{property}\" Value=\"(?<value>[^\"]+)\"");

    private static double Attribute(string xaml, string anchor, string name) =>
        double.Parse(Value(xaml, anchor, $"{name}=\"(?<value>[^\"]+)\""), CultureInfo.InvariantCulture);

    private static string Value(string xaml, string anchor, string pattern)
    {
        var start = xaml.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"MainWindow.xaml no longer contains {anchor}.");

        var match = Regex.Match(xaml[start..], pattern);
        Assert.True(match.Success, $"{anchor} no longer declares {pattern}.");
        return match.Groups["value"].Value;
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} above {AppContext.BaseDirectory}.");
    }
}
