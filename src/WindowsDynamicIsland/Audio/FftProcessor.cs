namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Pure spectrum math (no NAudio / WPF types, unit-testable): radix-2 FFT, periodic Hann window,
/// logarithmically spaced bands, dB mapping with frequency tilt compensation, slow automatic gain control
/// and attack/release smoothing. Produces normalized band values (0..1) and an RMS level (0..1).
/// <para>
/// All buffers are allocated in the constructor; <see cref="AnalyzeWindow"/>, <see cref="Process"/> and
/// <see cref="AnalyzeLatest"/> are allocation-free. An instance is <b>not thread-safe</b> — use it from one thread.
/// </para>
/// </summary>
public sealed class FftProcessor
{
    /// <summary>Default lowest band edge, Hz.</summary>
    public const float DefaultMinFrequency = 40f;

    /// <summary>Default highest band edge, Hz (clamped to Nyquist).</summary>
    public const float DefaultMaxFrequency = 16000f;

    /// <summary>Band dB value that maps to 0 (after tilt and gain).</summary>
    public const float FloorDb = -70f;

    /// <summary>Band dB value that maps to 1 (after tilt and gain).</summary>
    public const float CeilingDb = -10f;

    /// <summary>RMS dBFS that maps to level 0.</summary>
    public const float LevelFloorDb = -60f;

    /// <summary>RMS dBFS that maps to level 1.</summary>
    public const float LevelCeilingDb = -6f;

    /// <summary>Frequency at which the tilt compensation is 0 dB.</summary>
    public const float TiltReferenceFrequency = 1000f;

    private const float SilenceDb = -100f;
    private const float BandDbFloorForMean = -120f;
    private const float AgcTargetMeanDb = -30f;
    private const float AgcMinGainDb = -12f;
    private const float AgcMaxGainDb = 24f;
    private const float AgcAttack = 0.3f;
    private const float AgcRelease = 0.005f;
    private const float AgcTrackerMinDb = -80f;

    private readonly float[] _window;
    private readonly float[] _hann;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _magnitudes;
    private readonly float[] _bands;
    private readonly int[] _bandLoBin;
    private readonly int[] _bandHiBin;
    private readonly float[] _bandCenterBin;
    private readonly float[] _bandTiltDb;
    private readonly float[] _bandLowHz;
    private readonly float[] _bandHighHz;
    private readonly SampleRingBuffer _history;
    private int _samplesSinceFrame;
    private int _hopSize;
    private float _level;
    private float _agcTrackerDb = -45f; // mid-range start: typical music settles within ~1–3 s

    /// <summary>Creates a processor with default band range (<see cref="DefaultMinFrequency"/>..<see cref="DefaultMaxFrequency"/>).</summary>
    /// <param name="fftSize">FFT length, power of two between 64 and 65536 (e.g. 2048).</param>
    /// <param name="bandCount">Number of output bands (e.g. 32).</param>
    /// <param name="sampleRate">Sample rate of the mono input, Hz.</param>
    public FftProcessor(int fftSize, int bandCount, int sampleRate)
        : this(fftSize, bandCount, sampleRate, DefaultMinFrequency, DefaultMaxFrequency)
    {
    }

    /// <summary>Creates a processor with an explicit band range.</summary>
    /// <param name="fftSize">FFT length, power of two between 64 and 65536.</param>
    /// <param name="bandCount">Number of output bands (1..min(1024, fftSize/2)).</param>
    /// <param name="sampleRate">Sample rate of the mono input, Hz (≥ 8000).</param>
    /// <param name="minFrequency">Lowest band edge, Hz.</param>
    /// <param name="maxFrequency">Highest band edge, Hz; clamped to Nyquist.</param>
    public FftProcessor(int fftSize, int bandCount, int sampleRate, float minFrequency, float maxFrequency)
    {
        if (fftSize < 64 || fftSize > 65536 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fftSize), "FFT size must be a power of two in 64..65536.");
        if (bandCount < 1 || bandCount > Math.Min(1024, fftSize / 2)) throw new ArgumentOutOfRangeException(nameof(bandCount));
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!(minFrequency > 0f) || !float.IsFinite(minFrequency)) throw new ArgumentOutOfRangeException(nameof(minFrequency));
        if (!float.IsFinite(maxFrequency)) throw new ArgumentOutOfRangeException(nameof(maxFrequency));

        FftSize = fftSize;
        BandCount = bandCount;
        SampleRate = sampleRate;

        var nyquist = sampleRate / 2f;
        MaxFrequency = Math.Min(maxFrequency, nyquist);
        MinFrequency = Math.Min(minFrequency, MaxFrequency / 2f);

        _window = new float[fftSize];
        _hann = CreateHannWindow(fftSize);
        _re = new float[fftSize];
        _im = new float[fftSize];
        _magnitudes = new float[fftSize / 2 + 1];
        _bands = new float[bandCount];
        _bandLoBin = new int[bandCount];
        _bandHiBin = new int[bandCount];
        _bandCenterBin = new float[bandCount];
        _bandTiltDb = new float[bandCount];
        _bandLowHz = new float[bandCount];
        _bandHighHz = new float[bandCount];
        _history = new SampleRingBuffer(fftSize * 2);
        _hopSize = Math.Max(1, sampleRate / 60);

        BuildBands();
    }

    /// <summary>FFT length in samples.</summary>
    public int FftSize { get; }

    /// <summary>Number of output bands.</summary>
    public int BandCount { get; }

    /// <summary>Input sample rate, Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Effective lowest band edge, Hz.</summary>
    public float MinFrequency { get; }

    /// <summary>Effective highest band edge, Hz (≤ Nyquist).</summary>
    public float MaxFrequency { get; }

    /// <summary>Frequency resolution of one FFT bin, Hz.</summary>
    public float BinWidth => (float)SampleRate / FftSize;

    /// <summary>
    /// Number of new samples <see cref="Process"/> waits for before analysing again (default sampleRate/60,
    /// i.e. at most ~60 frames per second of audio).
    /// </summary>
    public int HopSize
    {
        get => _hopSize;
        set => _hopSize = Math.Max(1, value);
    }

    /// <summary>Multiplier applied to normalized band values and level (clamped to 0..1 afterwards). Default 1.</summary>
    public float Sensitivity { get; set; } = 1f;

    /// <summary>Per-frame smoothing coefficient used when a value rises (0..1, 1 = instant). Default 0.6.</summary>
    public float AttackCoefficient { get; set; } = 0.6f;

    /// <summary>Per-frame smoothing coefficient used when a value falls (0..1, 1 = instant). Default 0.15.</summary>
    public float ReleaseCoefficient { get; set; } = 0.15f;

    /// <summary>High-frequency emphasis in dB per octave relative to <see cref="TiltReferenceFrequency"/>. Default 3.</summary>
    public float TiltDbPerOctave
    {
        get => _tiltDbPerOctave;
        set
        {
            _tiltDbPerOctave = float.IsFinite(value) ? value : 0f;
            for (var b = 0; b < BandCount; b++)
                _bandTiltDb[b] = ComputeTilt(MathF.Sqrt(_bandLowHz[b] * _bandHighHz[b]));
        }
    }
    private float _tiltDbPerOctave = 3f;

    /// <summary>
    /// When true (default) a tracker of the mean band level (fast up, ~3 s down) steers the gain (−12..+24 dB) so the
    /// average band sits around the middle of the range: quiet tracks still move, loud ones do not saturate, and a
    /// single dominant tone does not squash everything else.
    /// </summary>
    public bool AutoGainEnabled { get; set; } = true;

    /// <summary>Current automatic gain in dB (0 when <see cref="AutoGainEnabled"/> is false).</summary>
    public float CurrentGainDb => AutoGainEnabled ? Math.Clamp(AgcTargetMeanDb - _agcTrackerDb, AgcMinGainDb, AgcMaxGainDb) : 0f;

    /// <summary>Latest smoothed band values, 0..1, low → high frequency.</summary>
    public ReadOnlySpan<float> Bands => _bands;

    /// <summary>Latest smoothed RMS level, 0..1.</summary>
    public float Level => _level;

    /// <summary>Number of analysis frames computed so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Lower edge of <paramref name="band"/>, Hz.</summary>
    public float GetBandLowFrequency(int band) => _bandLowHz[band];

    /// <summary>Upper edge of <paramref name="band"/>, Hz.</summary>
    public float GetBandHighFrequency(int band) => _bandHighHz[band];

    /// <summary>
    /// Appends mono samples to the internal history and, once at least <see cref="HopSize"/> new samples have
    /// arrived, analyses the newest <see cref="FftSize"/> samples (once, however many hops were crossed).
    /// </summary>
    /// <returns>True when a new frame was computed.</returns>
    public bool Process(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return false;
        _history.Write(samples);
        _samplesSinceFrame += samples.Length;
        if (_samplesSinceFrame < _hopSize) return false;
        _samplesSinceFrame = 0;
        AnalyzeLatest();
        return true;
    }

    /// <summary>Analyses the newest <see cref="FftSize"/> samples of the internal history right now.</summary>
    public void AnalyzeLatest()
    {
        _history.CopyWindow(_history.TotalWritten, _window);
        AnalyzeWindow(_window);
    }

    /// <summary>
    /// Analyses one block of exactly <see cref="FftSize"/> mono samples (oldest first): Hann window → FFT →
    /// log bands → dB + tilt + gain → 0..1 → sensitivity → attack/release smoothing. Also updates <see cref="Level"/>.
    /// The input is not modified.
    /// </summary>
    public void AnalyzeWindow(ReadOnlySpan<float> samples)
    {
        if (samples.Length != FftSize)
            throw new ArgumentException($"Expected exactly {FftSize} samples.", nameof(samples));

        var n = FftSize;
        var re = _re.AsSpan();
        var im = _im.AsSpan();

        double sumSquares = 0;
        for (var i = 0; i < n; i++)
        {
            var s = samples[i];
            if (!float.IsFinite(s)) s = 0f;
            sumSquares += (double)s * s;
            re[i] = s * _hann[i];
        }
        im.Clear();

        Fft(re, im);

        // Single-sided amplitude spectrum. Hann coherent gain is 0.5, so a full-scale sine peaks at 1.0 (0 dBFS).
        var scale = 4f / n;
        var half = n / 2;
        for (var k = 0; k <= half; k++)
            _magnitudes[k] = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) * scale;

        // Band magnitudes in dB (with tilt) — stored temporarily in the FFT scratch (re) to stay allocation-free.
        var bandDb = re.Slice(0, BandCount);
        var framePeakDb = float.NegativeInfinity;
        var frameDbSum = 0f;
        for (var b = 0; b < BandCount; b++)
        {
            float amplitude;
            var lo = _bandLoBin[b];
            var hi = _bandHiBin[b];
            if (hi - lo >= 2)
            {
                amplitude = 0f;
                for (var k = lo; k < hi; k++)
                    if (_magnitudes[k] > amplitude) amplitude = _magnitudes[k];
            }
            else
            {
                // Band narrower than ~2 bins (low frequencies): interpolate the spectrum at the band centre.
                var c = _bandCenterBin[b];
                var k0 = Math.Clamp((int)c, 0, half);
                var k1 = Math.Min(k0 + 1, half);
                var frac = c - k0;
                amplitude = _magnitudes[k0] + (_magnitudes[k1] - _magnitudes[k0]) * frac;
            }

            var db = 20f * MathF.Log10(amplitude + 1e-10f) + _bandTiltDb[b];
            bandDb[b] = db;
            if (db > framePeakDb) framePeakDb = db;
            frameDbSum += Math.Max(db, BandDbFloorForMean);
        }

        // Automatic gain: follows louder material fast, relaxes slowly (~3 s); (near) digital silence does not move it.
        if (framePeakDb > SilenceDb)
        {
            var frameMeanDb = frameDbSum / BandCount;
            var coefficient = frameMeanDb > _agcTrackerDb ? AgcAttack : AgcRelease;
            _agcTrackerDb += (frameMeanDb - _agcTrackerDb) * coefficient;
            _agcTrackerDb = Math.Clamp(_agcTrackerDb, AgcTrackerMinDb, 0f);
        }

        var gainDb = CurrentGainDb;
        var sensitivity = float.IsFinite(Sensitivity) ? Math.Max(0f, Sensitivity) : 1f;
        var attack = Math.Clamp(AttackCoefficient, 0f, 1f);
        var release = Math.Clamp(ReleaseCoefficient, 0f, 1f);
        const float range = CeilingDb - FloorDb;

        for (var b = 0; b < BandCount; b++)
        {
            var v = (bandDb[b] + gainDb - FloorDb) / range;
            v = Math.Clamp(v * sensitivity, 0f, 1f);
            var current = _bands[b];
            _bands[b] = current + (v - current) * (v > current ? attack : release);
        }

        var rmsDb = 10f * MathF.Log10((float)(sumSquares / n) + 1e-12f);
        var level = Math.Clamp((rmsDb - LevelFloorDb) / (LevelCeilingDb - LevelFloorDb) * sensitivity, 0f, 1f);
        _level += (level - _level) * (level > _level ? attack : release);

        FrameCount++;
    }

    /// <summary>Copies the latest bands into <paramref name="destination"/>, resampling when the lengths differ.</summary>
    public void CopyBands(Span<float> destination)
    {
        if (destination.Length == BandCount) _bands.AsSpan().CopyTo(destination);
        else Resample(_bands, destination);
    }

    /// <summary>
    /// Forgets the signal history and smoothed output (bands and level restart from 0) but keeps the automatic
    /// gain estimate, so playback that resumes after a pause starts from rest at the right sensitivity.
    /// </summary>
    public void ResetSignal()
    {
        _history.Clear();
        _samplesSinceFrame = 0;
        Array.Clear(_bands);
        _level = 0f;
    }

    /// <summary>
    /// In-place iterative radix-2 complex FFT (forward, unnormalized: X[k] = Σ x[n]·e^(−2πikn/N)).
    /// </summary>
    /// <param name="re">Real parts; length must be a power of two.</param>
    /// <param name="im">Imaginary parts; same length as <paramref name="re"/>.</param>
    public static void Fft(Span<float> re, Span<float> im)
    {
        var n = re.Length;
        if (im.Length != n) throw new ArgumentException("Real and imaginary spans must have the same length.", nameof(im));
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.", nameof(re));
        if (n == 1) return;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                var tmpRe = re[i];
                re[i] = re[j];
                re[j] = tmpRe;
                var tmpIm = im[i];
                im[i] = im[j];
                im[j] = tmpIm;
            }
        }

        // Butterflies; twiddles via a double-precision rotation recurrence (accurate for the sizes used here).
        for (var len = 2; len <= n; len <<= 1)
        {
            var halfLen = len >> 1;
            var theta = -2.0 * Math.PI / len;
            var stepRe = Math.Cos(theta);
            var stepIm = Math.Sin(theta);
            double wRe = 1.0, wIm = 0.0;
            for (var k = 0; k < halfLen; k++)
            {
                var wr = (float)wRe;
                var wi = (float)wIm;
                for (var start = k; start < n; start += len)
                {
                    var odd = start + halfLen;
                    var tr = re[odd] * wr - im[odd] * wi;
                    var ti = re[odd] * wi + im[odd] * wr;
                    re[odd] = re[start] - tr;
                    im[odd] = im[start] - ti;
                    re[start] += tr;
                    im[start] += ti;
                }

                var nextRe = wRe * stepRe - wIm * stepIm;
                wIm = wRe * stepIm + wIm * stepRe;
                wRe = nextRe;
            }
        }
    }

    /// <summary>Creates a periodic Hann window of length <paramref name="length"/> (the FFT-analysis variant).</summary>
    public static float[] CreateHannWindow(int length)
    {
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length));
        var window = new float[length];
        for (var i = 0; i < length; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / length));
        return window;
    }

    /// <summary>
    /// Resamples <paramref name="source"/> into <paramref name="destination"/>: averaging when shrinking
    /// (each destination cell averages the overlapping source cells, fractional overlap weighted),
    /// linear interpolation when stretching, plain copy when equal. Allocation-free.
    /// </summary>
    public static void Resample(ReadOnlySpan<float> source, Span<float> destination)
    {
        var srcLen = source.Length;
        var dstLen = destination.Length;
        if (dstLen == 0) return;
        if (srcLen == 0)
        {
            destination.Clear();
            return;
        }
        if (srcLen == dstLen)
        {
            source.CopyTo(destination);
            return;
        }

        if (dstLen < srcLen)
        {
            var ratio = (double)srcLen / dstLen;
            for (var j = 0; j < dstLen; j++)
            {
                var from = j * ratio;
                var to = from + ratio;
                double sum = 0, weight = 0;
                for (var i = (int)from; i < srcLen && i < to; i++)
                {
                    var overlap = Math.Min(to, i + 1) - Math.Max(from, i);
                    if (overlap <= 0) continue;
                    sum += source[i] * overlap;
                    weight += overlap;
                }
                destination[j] = weight > 0 ? (float)(sum / weight) : 0f;
            }
            return;
        }

        if (srcLen == 1)
        {
            destination.Fill(source[0]);
            return;
        }

        var scale = (double)(srcLen - 1) / (dstLen - 1);
        for (var j = 0; j < dstLen; j++)
        {
            var x = j * scale;
            var i0 = Math.Min((int)x, srcLen - 1);
            var i1 = Math.Min(i0 + 1, srcLen - 1);
            var t = (float)(x - i0);
            destination[j] = source[i0] + (source[i1] - source[i0]) * t;
        }
    }

    private void BuildBands()
    {
        var binHz = (double)SampleRate / FftSize;
        var half = FftSize / 2;
        var ratio = (double)MaxFrequency / MinFrequency;

        for (var b = 0; b < BandCount; b++)
        {
            var low = MinFrequency * Math.Pow(ratio, (double)b / BandCount);
            var high = MinFrequency * Math.Pow(ratio, (double)(b + 1) / BandCount);
            var center = Math.Sqrt(low * high);

            _bandLowHz[b] = (float)low;
            _bandHighHz[b] = (float)high;
            _bandLoBin[b] = Math.Clamp((int)Math.Ceiling(low / binHz), 1, half);
            _bandHiBin[b] = Math.Clamp((int)Math.Ceiling(high / binHz), _bandLoBin[b], half + 1);
            _bandCenterBin[b] = (float)Math.Clamp(center / binHz, 1.0, half);
            _bandTiltDb[b] = ComputeTilt((float)center);
        }
    }

    private float ComputeTilt(float centerHz)
        => _tiltDbPerOctave * MathF.Log2(Math.Max(centerHz, 1f) / TiltReferenceFrequency);
}
