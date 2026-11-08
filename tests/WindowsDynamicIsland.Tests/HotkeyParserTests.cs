using System.Windows.Input;
using WindowsDynamicIsland.Settings;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Alt+I", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.I)]
    [InlineData("ctrl + shift + f5", HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.F5)]
    [InlineData("Win+Alt+Space", HotkeyModifiers.Win | HotkeyModifiers.Alt, Key.Space)]
    [InlineData("Ctrl+Alt+7", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.D7)]
    [InlineData("F9", HotkeyModifiers.None, Key.F9)]
    public void Parses_valid_combinations(string text, HotkeyModifiers mods, Key key)
    {
        Assert.True(HotkeyParser.TryParse(text, out var m, out var k));
        Assert.Equal(mods, m);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Alt")]
    [InlineData("Q")]
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid_combinations(string? text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _, out _));
    }

    [Fact]
    public void Format_round_trips()
    {
        Assert.True(HotkeyParser.TryParse("alt+ctrl+i", out var m, out var k));
        var formatted = HotkeyParser.Format(m, k);
        Assert.True(HotkeyParser.TryParse(formatted, out var m2, out var k2));
        Assert.Equal(m, m2);
        Assert.Equal(k, k2);
    }
}
