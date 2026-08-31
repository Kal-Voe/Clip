using System.Windows.Input;
using Clip.Shell;

namespace Clip.Tests;

public sealed class HotkeySettingsTests
{
    [Fact]
    public void HotkeySettingsUseClipDefaults()
    {
        var hotkeys = new ClipHotkeySettings();

        Assert.Equal("Alt+V", hotkeys.OpenClip);
        Assert.Equal("Enter", hotkeys.PasteSelected);
        Assert.Equal("Ctrl+C", hotkeys.CopySelected);
        Assert.Equal("Ctrl+P", hotkeys.PinSelected);
        Assert.Equal("Ctrl+K", hotkeys.OpenActions);
        Assert.Equal("Ctrl+O", hotkeys.OpenSelected);
        Assert.Equal("Ctrl+E", hotkeys.EditSelected);
        Assert.Equal("Ctrl+Shift+L", hotkeys.SaveDebugLog);
        Assert.Equal("Delete", hotkeys.DeleteSelected);
        Assert.Equal("Esc", hotkeys.CloseClip);
    }

    [Fact]
    public void ResetRestoresDefaultHotkeys()
    {
        var hotkeys = new ClipHotkeySettings
        {
            OpenClip = "Ctrl+Space",
            PasteSelected = "Ctrl+Enter",
            SaveDebugLog = "Ctrl+Alt+L",
        };

        hotkeys.ResetToDefaults();

        Assert.Equal("Alt+V", hotkeys.OpenClip);
        Assert.Equal("Enter", hotkeys.PasteSelected);
        Assert.Equal("Ctrl+Shift+L", hotkeys.SaveDebugLog);
    }

    [Fact]
    public void HotkeyGestureParsesWindowsAndWpfValues()
    {
        var parsed = ClipHotkeyGesture.TryParse("Ctrl+Shift+L", out var gesture);

        Assert.True(parsed);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, gesture.WpfModifiers);
        Assert.Equal(Key.L, gesture.WpfKey);
        Assert.Equal("Ctrl+Shift+L", gesture.DisplayText);
    }

    [Fact]
    public void HotkeyGestureAllowsSingleActionKeys()
    {
        var parsed = ClipHotkeyGesture.TryParse("Delete", out var gesture);

        Assert.True(parsed);
        Assert.Equal(Key.Delete, gesture.WpfKey);
        Assert.Equal("Delete", gesture.DisplayText);
    }

    [Fact]
    public void GlobalHotkeysRequireModifier()
    {
        Assert.True(ClipHotkeyGesture.TryParseGlobal("Alt+V", out _));
        Assert.False(ClipHotkeyGesture.TryParseGlobal("V", out _));
    }

    [Fact]
    public void EscAliasParsesToEscape()
    {
        // The default CloseClip value is "Esc", which WPF's Key enum does not know — without
        // the alias the close binding is dead on every install.
        var parsed = ClipHotkeyGesture.TryParse("Esc", out var gesture);

        Assert.True(parsed);
        Assert.Equal(Key.Escape, gesture.WpfKey);
        Assert.Equal("Esc", gesture.DisplayText);
    }

    [Fact]
    public void DelAliasParsesToDelete()
    {
        var parsed = ClipHotkeyGesture.TryParse("del", out var gesture);

        Assert.True(parsed);
        Assert.Equal(Key.Delete, gesture.WpfKey);
        Assert.Equal("Delete", gesture.DisplayText);
    }

    [Fact]
    public void NormalizeKeepsDefaultCloseClipValue()
    {
        // Normalize snaps an unparseable value back to the default; "Esc" must parse and
        // round-trip unchanged so existing settings.json files stay as they are.
        var hotkeys = new ClipHotkeySettings();

        hotkeys.Normalize();

        Assert.Equal("Esc", hotkeys.CloseClip);
        Assert.Equal("Delete", hotkeys.DeleteSelected);
    }

    [Theory]
    [InlineData("Enter", "Shift+Enter")]
    [InlineData("Ctrl+Enter", "Ctrl+Shift+Enter")]
    [InlineData("Alt+V", "Shift+Alt+V")]
    // Aliases must come back in the canonical spelling, or the footer cap would not match the
    // gesture the key handler actually compares against.
    [InlineData("del", "Shift+Delete")]
    public void PasteAndStayIsThePasteHotkeyPlusShift(string paste, string expected)
    {
        Assert.Equal(expected, MainWindow.PasteAndStayGesture(paste));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Not+A+Key")]
    // Already using Shift: there is no free variant left, so no hint and no binding.
    [InlineData("Ctrl+Shift+V")]
    [InlineData("Shift+Enter")]
    public void PasteAndStayIsUnavailableWhenThereIsNoShiftVariantToOffer(string? paste)
    {
        Assert.Null(MainWindow.PasteAndStayGesture(paste));
    }
}
