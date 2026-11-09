namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Turns the analyzer's logarithmic bands into what the visuals draw, the way the iPhone island does it:
/// every compact bar listens to its own interval on a logarithmic 20 Hz–20 kHz axis.
/// Bass beat detection remains available separately for the expanded wave animation.
/// Pure math, allocation-free, unit-testable.
/// </summary>
public static class SpectrumShaper
{
    /// <summary>Lowest analysed frequency (edge of band 0), Hz.</summary>
    public const double MinHz = FftProcessor.DefaultMinFrequency;

    /// <summary>Highest analysed frequency (edge of the last band), Hz.</summary>
    public const double MaxHz = FftProcessor.DefaultMaxFrequency;

    /// <summary>Upper edge of the bass region used by beat and wave analysis, Hz.</summary>
    public const double BassHighHz = 150;

    /// <summary>Upper edge of the analysed spectrum, Hz.</summary>
    public const double TopHz = FftProcessor.DefaultMaxFrequency;

    /// <summary>
    /// Fractional band index (0 … bandCount−1) whose centre is <paramref name="hz"/>, for <paramref name="bandCount"/>
    /// bands spaced logarithmically between <see cref="MinHz"/> and <see cref="MaxHz"/>.
    /// </summary>
    public static double BandPosition(double hz, int bandCount)
    {
        if (bandCount <= 1 || !(hz > 0)) return 0;
        var t = Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz);
        return Math.Clamp(t * bandCount - 0.5, 0, bandCount - 1);
    }

    /// <summary>Value at an exact frequency (linear interpolation between the two neighbouring bands).</summary>
    public static float SampleAt(ReadOnlySpan<float> bands, double hz)
    {
        if (bands.IsEmpty) return 0f;
        var p = BandPosition(hz, bands.Length);
        var lo = (int)p;
        var hi = Math.Min(lo + 1, bands.Length - 1);
        var frac = (float)(p - lo);
        return Clamp01(bands[lo] + (bands[hi] - bands[lo]) * frac);
    }

    /// <summary>Loudest band whose centre lies in [loHz, hiHz] (at least one band).</summary>
    public static float PeakBetween(ReadOnlySpan<float> bands, double loHz, double hiHz)
    {
        if (bands.IsEmpty) return 0f;
        var a = (int)Math.Floor(BandPosition(loHz, bands.Length) + 0.5);
        var b = (int)Math.Floor(BandPosition(hiHz, bands.Length) + 0.5);
        if (b < a) (a, b) = (b, a);
        var peak = 0f;
        for (var i = a; i <= b; i++)
            if (bands[i] > peak) peak = bands[i];
        return Clamp01(peak);
    }

    /// <summary>
    /// Maps the complete audible spectrum (20 Hz–20 kHz) to equally spaced logarithmic slots.
    /// Every bar represents its own frequency interval; no dedicated bass bar or bass-only boost is used.
    /// This makes a logarithmic sweep travel naturally from the low-frequency bar to the high-frequency bar.
    /// </summary>
    public static void FixedFrequencies(ReadOnlySpan<float> bands, Span<float> destination)
    {
        var n = destination.Length;
        if (n == 0) return;
        if (bands.IsEmpty)
        {
            destination.Clear();
            return;
        }

        var ratio = TopHz / MinHz;
        for (var i = 0; i < n; i++)
        {
            var f0 = MinHz * Math.Pow(ratio, (double)i / n);
            var f1 = MinHz * Math.Pow(ratio, (double)(i + 1) / n);
            var peak = PeakBetween(bands, f0, f1);
            var centre = SampleAt(bands, Math.Sqrt(f0 * f1));
            // Peak keeps a narrow sweep tone visible; centre sampling prevents a single FFT bin
            // from making adjacent bars flicker or appear completely disconnected.
            destination[i] = Clamp01(0.75f * peak + 0.25f * centre);
        }
    }

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
}

/// <summary>
/// Kick detector for bass-driven wave motion: compares the bass level with its slow running average and turns the
/// excess into a punchy 0..1 value (instant attack, ~150 ms release). Not thread-safe; one instance per consumer.
/// </summary>
public sealed class BassPunch
{
    private const double AverageSeconds = 0.6;
    private const double ReleaseRate = 7.0; // 1/s
    private const float KickThreshold = 1.04f;
    private const float KickRange = 0.18f;

    private float _average;
    private float _value;

    /// <summary>Current output 0..1.</summary>
    public float Value => _value;

    /// <summary>Feeds the current bass level (0..1) and returns the punch value.</summary>
    public float Update(float bass, double dt)
    {
        if (!float.IsFinite(bass)) bass = 0f;
        dt = Math.Clamp(dt, 0.0, 0.1);
        _average += (bass - _average) * (float)(1.0 - Math.Exp(-dt / AverageSeconds));

        var kick = Math.Clamp((bass - _average * KickThreshold) / KickRange, 0f, 1f);
        // Bass alone already moves the first bar a lot; a kick on top slams it to the top.
        var target = Math.Clamp(0.55f * MathF.Pow(bass, 0.8f) + 0.85f * kick, 0f, 1f);

        _value = target >= _value ? target : Math.Max(target, _value * (float)Math.Exp(-ReleaseRate * dt));
        return _value;
    }

    /// <summary>Back to silence.</summary>
    public void Reset()
    {
        _average = 0f;
        _value = 0f;
    }
}
