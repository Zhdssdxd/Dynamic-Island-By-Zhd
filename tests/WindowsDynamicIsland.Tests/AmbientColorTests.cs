using System;
using System.Windows.Media;
using WindowsDynamicIsland.UI;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class AmbientColorTests
{
    private static double Hue(Color c)
    {
        double r = c.R, g = c.G, b = c.B;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d == 0) return 0;
        double h = max == r ? ((g - b) / d % 6 + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return h * 60;
    }

    [Theory]
    [InlineData(90, 160, 240)]
    [InlineData(250, 170, 90)]
    [InlineData(70, 40, 110)]
    [InlineData(40, 160, 90)]
    public void Vivid_KeepsHue_AndBoostsSaturation(byte r, byte g, byte b)
    {
        var src = Color.FromRgb(r, g, b);
        var v = IslandWindow.Vivid(src);
        Assert.InRange(Math.Abs(Hue(v) - Hue(src)), 0, 4);
        var srcSpread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        var vSpread = Math.Max(v.R, Math.Max(v.G, v.B)) - Math.Min(v.R, Math.Min(v.G, v.B));
        Assert.True(vSpread >= srcSpread * 0.9);
    }

    [Fact]
    public void Vivid_KeepsGreyGrey_AndVisible()
    {
        var v = IslandWindow.Vivid(Color.FromRgb(0, 0, 0));
        Assert.Equal(v.R, v.G);
        Assert.Equal(v.G, v.B);
        Assert.True(v.R >= 130);
        var w = IslandWindow.Vivid(Colors.White);
        Assert.True(w.R <= 240);
    }
}
