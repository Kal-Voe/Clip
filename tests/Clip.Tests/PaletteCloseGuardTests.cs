using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The palette runs under ShutdownMode.OnExplicitShutdown, so a window destroyed by a stray
/// WM_CLOSE left Clip alive in the tray with Alt+V unregistered and no way to reopen it.
/// </summary>
public class PaletteCloseGuardTests
{
    [Fact]
    public void StrayCloseIsRefused()
    {
        Assert.False(MainWindow.ShouldHonorCloseRequest(appIsExiting: false, paletteSessionMode: false));
    }

    [Fact]
    public void DeliberateExitCloses()
    {
        Assert.True(MainWindow.ShouldHonorCloseRequest(appIsExiting: true, paletteSessionMode: false));
    }

    [Fact]
    public void HarnessWindowStillCloses()
    {
        Assert.True(MainWindow.ShouldHonorCloseRequest(appIsExiting: false, paletteSessionMode: true));
    }
}
