using WindowsDynamicIsland.Audio;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class SpectrumShaperTests
{
    [Fact]
    public void BandPosition_IsLogarithmic()
    {
        Assert.Equal(0, SpectrumShaper.BandPosition(40, 32));
        Assert.Equal(31, SpectrumShaper.BandPosition(16000, 32), 3);
        // One octave is the same distance anywhere on the axis.
        var a = SpectrumShaper.BandPosition(200, 32) - SpectrumShaper.BandPosition(100, 32);
        var b = SpectrumShaper.BandPosition(4000, 32) - SpectrumShaper.BandPosition(2000, 32);
        Assert.Equal(a, b, 6);
    }

    [Fact]
    public void FixedFrequencies_FirstSlotIsBass()
    {
        var bands = new float[32];
        bands[2] = 0.9f; // ~55 Hz
        var dest = new float[5];
        SpectrumShaper.FixedFrequencies(bands, dest);
        Assert.Equal(0.9f, dest[0], 3);
        for (var i = 1; i < dest.Length; i++) Assert.True(dest[i] < 0.2f, $"slot {i} = {dest[i]}");
    }

    [Fact]
    public void FixedFrequencies_HighsLandInLastSlot()
    {
        var bands = new float[32];
        var hi = (int)Math.Round(SpectrumShaper.BandPosition(6000, 32));
        bands[hi] = 1f;
        var dest = new float[5];
        SpectrumShaper.FixedFrequencies(bands, dest);
        Assert.Equal(0f, dest[0]);
        Assert.True(dest[4] > 0.6f, $"last slot = {dest[4]}");
    }

    [Fact]
    public void BassPunch_JumpsOnKickAndDecays()
    {
        var punch = new BassPunch();
        for (var i = 0; i < 120; i++) punch.Update(0.3f, 1 / 60.0); // steady bass
        var steady = punch.Value;
        var kick = punch.Update(0.9f, 1 / 60.0);
        Assert.True(kick > steady + 0.3f, $"kick {kick} vs steady {steady}");
        for (var i = 0; i < 30; i++) punch.Update(0f, 1 / 60.0);
        Assert.True(punch.Value < 0.05f);
    }
}
