using System.Windows.Input;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// Throws hostile and half-typed strings at the hotkey parser. Settings.json is user-editable,
/// so TryParse must never throw on any input — it either returns a gesture whose virtual key
/// registers (> 0) and whose DisplayText re-parses to the same keys, or it returns false.
/// </summary>
public sealed class HotkeyGestureFuzzTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("++")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++")]
    [InlineData("+A")]
    [InlineData("Esc")]
    [InlineData("Escape")]
    [InlineData("Del")]
    [InlineData("Delete")]
    [InlineData("Enter")]
    [InlineData("Return")]
    [InlineData("Ctrl+Shift+Alt+Win+F24")]
    [InlineData("ctrl + shift + p")]
    [InlineData("garbage")]
    [InlineData("Ctrl+Garbage")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("A+B")]
    [InlineData("\U0001F680")]
    [InlineData("Ctrl+\U0001F680")]
    [InlineData("123")]
    [InlineData("-1")]
    [InlineData("999999")]
    [InlineData("F25")]
    [InlineData("Ctrl+Shift+")]
    [InlineData("None")]
    public void TryParseNeverThrowsAndSuccessesAreSane(string? text)
    {
        var parsed = ClipHotkeyGesture.TryParse(text, out var gesture);

        if (parsed)
        {
            Assert.True(gesture.VirtualKey > 0);
            Assert.False(string.IsNullOrWhiteSpace(gesture.DisplayText));
            // The DisplayText is what Normalize writes back into settings.json, so it must
            // survive a round trip to the same keys or a valid binding would decay on save.
            Assert.True(ClipHotkeyGesture.TryParse(gesture.DisplayText, out var reparsed));
            Assert.Equal(gesture.WpfKey, reparsed.WpfKey);
            Assert.Equal(gesture.WpfModifiers, reparsed.WpfModifiers);
        }
        else
        {
            Assert.Equal(default, gesture);
        }
    }

    [Fact]
    public void TenThousandCharacterGarbageParsesFalseWithoutThrowing()
    {
        Assert.False(ClipHotkeyGesture.TryParse(new string('+', 10_000), out _));
        Assert.False(ClipHotkeyGesture.TryParse(new string('x', 10_000), out _));
        Assert.False(ClipHotkeyGesture.TryParse(string.Join('+', Enumerable.Repeat("Ctrl", 1_000)), out _));
    }

    [Theory]
    [InlineData("Esc", Key.Escape)]
    [InlineData("ESC", Key.Escape)]
    [InlineData("Escape", Key.Escape)]
    [InlineData("Del", Key.Delete)]
    [InlineData("Enter", Key.Enter)]
    [InlineData("Return", Key.Return)]
    public void ActionKeyAliasesAllLandOnTheRightKey(string text, Key expected)
    {
        Assert.True(ClipHotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture.WpfKey);
    }

    [Fact]
    public void FullModifierStackOnF24Parses()
    {
        Assert.True(ClipHotkeyGesture.TryParse("Ctrl+Shift+Alt+Win+F24", out var gesture));

        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows, gesture.WpfModifiers);
        Assert.Equal(Key.F24, gesture.WpfKey);
        Assert.True(ClipHotkeyGesture.TryParseGlobal("Ctrl+Shift+Alt+Win+F24", out _));
    }

    [Fact]
    public void DanglingModifierIsNotAKey()
    {
        // "Ctrl+" splits down to just "Ctrl", which must not be accepted as a key by itself —
        // a modifier-only binding can never fire.
        Assert.False(ClipHotkeyGesture.TryParse("Ctrl+", out _));
        Assert.False(ClipHotkeyGesture.TryParse("Shift+", out _));
        Assert.False(ClipHotkeyGesture.TryParse("Ctrl+Shift", out _));
    }
}
