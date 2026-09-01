using System.Windows;
using System.Windows.Controls;
using Clip.Shell;

namespace Clip.Tests;

// Settings takes the palette window over rather than floating on it, which means the palette's
// own chrome is hidden while Settings is up and put back when it closes. These cover the two
// rules that are easy to get wrong: what gets hidden, and what is safe to restore.
public sealed class HostedSettingsPaletteContentTests
{
    [Fact]
    public void HidingCoversVisibleChromeAndSkipsTheSettingsPanelAndCollapsedElements()
    {
        RunSta(() =>
        {
            var host = new Grid();
            var search = new Border();
            var toast = new Border { Visibility = Visibility.Collapsed };
            var overlay = new Border();
            var prewarmed = new Border();
            host.Children.Add(search);
            host.Children.Add(toast);
            host.Children.Add(overlay);
            host.Children.Add(prewarmed);
            var hidden = new List<UIElement>();

            MainWindow.HidePaletteContent(host, overlay, prewarmed, hidden);

            Assert.Equal(Visibility.Hidden, search.Visibility);
            // Hidden, never Collapsed: a collapsed element skips measure and arrange, and the
            // palette has to come back with the scroll offsets and rendered rows it had.
            Assert.NotEqual(Visibility.Collapsed, search.Visibility);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(Visibility.Visible, prewarmed.Visibility);
            Assert.Equal(Visibility.Collapsed, toast.Visibility);
            Assert.Equal(new UIElement[] { search }, hidden);
        });
    }

    [Fact]
    public void RestoringLeavesAloneAnythingThatCollapsedItselfWhileSettingsWasUp()
    {
        RunSta(() =>
        {
            var host = new Grid();
            var search = new Border();
            var expandedImage = new Border();
            host.Children.Add(search);
            host.Children.Add(expandedImage);
            var hidden = new List<UIElement>();
            MainWindow.HidePaletteContent(host, null, null, hidden);

            // Closing an expanded image while Settings is up collapses it. Restoring it to
            // Visible would strand it over the palette for good.
            expandedImage.Visibility = Visibility.Collapsed;

            MainWindow.RestorePaletteContent(hidden);

            Assert.Equal(Visibility.Visible, search.Visibility);
            Assert.Equal(Visibility.Collapsed, expandedImage.Visibility);
            Assert.Empty(hidden);
        });
    }

    /// <summary>WPF elements want a single-threaded apartment; xUnit runs MTA, so hop threads.</summary>
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
