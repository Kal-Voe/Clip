using Clip.Shell;
using Xunit;

namespace Clip.Tests;

// The rules that decide which targets get the UI Automation verify-and-retry and the post-activation
// settle. Chromium hosts (browsers and every Electron app) are the ones that drop a Ctrl+V sent the
// instant they are re-activated; everything else takes the key as soon as its thread has focus.
public class PasteTargetKindTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_1", true)]
    [InlineData("Chrome_WidgetWin_0", true)]
    [InlineData("Notepad", false)]
    [InlineData("CabinetWClass", false)]
    [InlineData(null, false)]
    public void ChromiumHostsAreRecognisedByWindowClass(string? windowClass, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsChromiumWindowClass(windowClass));
    }

    [Fact]
    public void OnlyChromiumHostsGetASettleAfterActivation()
    {
        Assert.True(MainWindow.SettleAfterActivationMs("Chrome_WidgetWin_1") > 0);
        Assert.Equal(0, MainWindow.SettleAfterActivationMs("Notepad"));
        Assert.Equal(0, MainWindow.SettleAfterActivationMs(null));
    }
}
