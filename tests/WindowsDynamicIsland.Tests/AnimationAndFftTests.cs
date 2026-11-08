using WindowsDynamicIsland.Animation;
using WindowsDynamicIsland.Audio;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class SpringTests
{
    [Theory]
    [InlineData(0.42, 0.86)]
    [InlineData(0.50, 0.68)]
    [InlineData(0.30, 1.00)]
    [InlineData(0.60, 1.40)]
    public void Spring_converges_and_settles(double response, double damping)
    {
        var spring = new Spring(0, SpringParameters.FromResponse(response, damping)) { Target = 100 };
        for (var i = 0; i < 600 && !spring.IsSettled; i++) spring.Step(1.0 / 60);
        Assert.True(spring.IsSettled);
        Assert.InRange(spring.Value, 99.8, 100.2);
    }

    [Fact]
    public void Spring_is_stable_with_huge_steps()
    {
        var spring = new Spring(0, SpringPresets.Bouncy) { Target = 1 };
        for (var i = 0; i < 50; i++)
        {
            spring.Step(5.0);
            Assert.False(double.IsNaN(spring.Value) || double.IsInfinity(spring.Value));
        }
        Assert.Equal(1, spring.Value, 3);
    }

    [Fact]
    public void Underdamped_spring_overshoots()
    {
        var spring = new Spring(0, SpringParameters.FromResponse(0.5, 0.5)) { Target = 1 };
        var max = 0.0;
        for (var i = 0; i < 120; i++)
        {
            spring.Step(1.0 / 60);
            max = Math.Max(max, spring.Value);
        }
        Assert.True(max > 1.01);
    }
}

public class FftTests
{
    [Fact]
    public void Fft_of_cosine_peaks_at_its_bin()
    {
        const int n = 1024;
        var re = new float[n];
        var im = new float[n];
        for (var i = 0; i < n; i++) re[i] = MathF.Cos(2 * MathF.PI * 37 * i / n);
        FftProcessor.Fft(re, im);
        var mags = Enumerable.Range(0, n / 2).Select(i => MathF.Sqrt(re[i] * re[i] + im[i] * im[i])).ToArray();
        Assert.Equal(37, Array.IndexOf(mags, mags.Max()));
    }

    [Fact]
    public void Sine_1khz_lights_the_band_containing_1khz()
    {
        const int rate = 48000;
        var fft = new FftProcessor(2048, 32, rate) { AutoGainEnabled = false };
        var samples = new float[2048];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
        for (var k = 0; k < 20; k++) fft.AnalyzeWindow(samples);

        var bands = fft.Bands.ToArray();
        var peak = Array.IndexOf(bands, bands.Max());
        Assert.InRange(1000f, fft.GetBandLowFrequency(peak) * 0.8f, fft.GetBandHighFrequency(peak) * 1.25f);
        Assert.True(bands[peak] > 0.3f);
    }

    [Fact]
    public void Silence_produces_near_zero_bands()
    {
        var fft = new FftProcessor(2048, 32, 48000);
        var silence = new float[2048];
        for (var k = 0; k < 30; k++) fft.AnalyzeWindow(silence);
        Assert.All(fft.Bands.ToArray(), b => Assert.True(b < 0.02f));
        Assert.True(fft.Level < 0.02f);
    }
}
