using System.Windows;
using System.Windows.Media;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Lightweight audio visualizer drawn directly with <see cref="DrawingContext"/> (no child visuals).
/// Reads <see cref="ISpectrumSource"/> from <see cref="ServiceRegistry"/>; holds a consumer token only while
/// <see cref="IsActive"/> and visible and loaded, and hooks <see cref="CompositionTarget.Rendering"/> only while
/// it is animating (active, decaying to rest, or cross-fading styles) — zero CPU when paused or hidden.
/// Without a source or without audio it draws the resting state (never random values).
/// </summary>
public sealed class VisualizerControl : FrameworkElement
{
    private const int MaxBars = 128;
    private const int DefaultEqualizerBars = 16;
    private const int DefaultMinimalBars = 5;
    private const int DefaultWaveformPoints = 24;
    private const int MaxSourceBands = 256;

    private const double RiseRate = 26.0;          // 1/s — display follows rising values quickly
    private const double FallRate = 9.0;           // 1/s — ≈ 96 % of the way to rest after 350 ms
    private const float SettleEpsilon = 0.0015f;
    private const double CrossfadeSeconds = 0.25;
    private const double MaxFrameDelta = 0.1;

    private const double MinimalBarWidth = 3.0;
    private const double MinimalBarGap = 2.5;
    private const double MinimalRestFraction = 0.15;
    private const double WaveStrokeThickness = 1.5;
    private const double WaveFillOpacity = 0.7;
    /// <summary>Share of the width kept quiet at each end of the wave (the curve rises from the centre line there).</summary>
    private const double WaveEdge = 0.07;

    private static readonly Brush EqualizerOpacityMask = CreateEqualizerMask();

    /// <summary>Visual style (named Mode to avoid clashing with <see cref="FrameworkElement.Style"/>).</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(VisualizerStyle), typeof(VisualizerControl),
        new FrameworkPropertyMetadata(VisualizerStyle.MinimalBars,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnModeChanged));

    /// <summary>True while audio should animate the bars; false decays smoothly to rest and stops rendering.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(VisualizerControl),
        new FrameworkPropertyMetadata(false, OnIsActiveChanged));

    /// <summary>Brush for bars / wave (default white). Pass an accent brush to tint, e.g. from album art.</summary>
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(VisualizerControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnBarBrushChanged));

    /// <summary>Number of bars (or wave points). 0 = automatic: Equalizer 16, MinimalBars 5, Waveform 24. Max 128.</summary>
    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(VisualizerControl),
        new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure),
        v => v is int i && i >= 0 && i <= MaxBars);

    /// <summary>Extra per-control multiplier on top of the analyzer's (settings) sensitivity. Default 1.</summary>
    public static readonly DependencyProperty SensitivityProperty = DependencyProperty.Register(
        nameof(Sensitivity), typeof(double), typeof(VisualizerControl),
        new FrameworkPropertyMetadata(1.0),
        v => v is double d && double.IsFinite(d) && d >= 0);

    private ISpectrumSource? _source;
    private IDisposable? _consumer;
    private float[] _target = new float[AudioAnalyzer.DefaultBandCount];
    private float[] _display = new float[AudioAnalyzer.DefaultBandCount];
    private bool _ticking;
    private bool _hasLastRenderingTime;
    private TimeSpan _lastRenderingTime;
    private VisualizerStyle? _fadeFromMode;
    private double _crossfade = 1.0;
    private readonly BassPunch _punch = new();
    private double _flowPhase;
    private float _bassLevel;
    private float _midLevel;
    private float _highLevel;
    private Pen? _strokePen;
    private Brush? _strokePenBrush;

    /// <summary>Creates the control (resting state until loaded and active).</summary>
    public VisualizerControl()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <inheritdoc cref="ModeProperty"/>
    public VisualizerStyle Mode
    {
        get => (VisualizerStyle)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <inheritdoc cref="IsActiveProperty"/>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <inheritdoc cref="BarBrushProperty"/>
    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <inheritdoc cref="BarCountProperty"/>
    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    /// <inheritdoc cref="SensitivityProperty"/>
    public double Sensitivity
    {
        get => (double)GetValue(SensitivityProperty);
        set => SetValue(SensitivityProperty, value);
    }

    // ───────────────────────── property / lifecycle callbacks ─────────────────────────

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VisualizerControl)d;
        if (control.IsLoaded && control.IsVisible && e.OldValue is VisualizerStyle previous)
        {
            control._fadeFromMode = previous;
            control._crossfade = 0.0;
            control.EnsureTicking();
        }
        else
        {
            control._fadeFromMode = null;
            control._crossfade = 1.0;
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VisualizerControl)d;
        control.UpdateConsumer();
        control.EnsureTicking(); // active → poll; inactive → keep ticking until decayed to rest
    }

    private static void OnBarBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VisualizerControl)d;
        control._strokePen = null;
        control._strokePenBrush = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _source ??= ServiceRegistry.Get<ISpectrumSource>();
        EnsureBuffers();
        UpdateConsumer();
        EnsureTicking();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReleaseConsumer();
        StopTicking();
        SnapToRest();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            UpdateConsumer();
            EnsureTicking();
        }
        else
        {
            // Hidden: release capture and stop all per-frame work; reappear from rest.
            ReleaseConsumer();
            StopTicking();
            SnapToRest();
        }
    }

    private void EnsureBuffers()
    {
        if (_source is null) return;
        int bands;
        try
        {
            bands = Math.Clamp(_source.BandCount, 1, MaxSourceBands);
        }
        catch (Exception ex)
        {
            Log.Warn("Spectrum source BandCount failed", ex);
            return;
        }
        if (_target.Length == bands) return;
        _target = new float[bands];
        _display = new float[bands];
    }

    private void UpdateConsumer()
    {
        var wanted = IsActive && IsLoaded && IsVisible && _source is not null;
        if (!wanted)
        {
            ReleaseConsumer();
            return;
        }
        if (_consumer is not null || _source is null) return;
        try
        {
            _consumer = _source.AcquireConsumer();
        }
        catch (Exception ex)
        {
            Log.Warn("Acquiring spectrum consumer failed", ex);
        }
    }

    private void ReleaseConsumer()
    {
        var consumer = _consumer;
        _consumer = null;
        if (consumer is null) return;
        try
        {
            consumer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing spectrum consumer failed", ex);
        }
    }

    private bool IsPolling => IsActive && _consumer is not null && _source is not null;

    private void EnsureTicking()
    {
        if (_ticking || !IsLoaded || !IsVisible) return;
        if (!IsPolling && IsAtRest() && _crossfade >= 1.0) return;
        _hasLastRenderingTime = false;
        CompositionTarget.Rendering += OnRendering;
        _ticking = true;
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        CompositionTarget.Rendering -= OnRendering;
        _ticking = false;
        _hasLastRenderingTime = false;
    }

    private void SnapToRest()
    {
        Array.Clear(_target);
        Array.Clear(_display);
        _punch.Reset();
        _bassLevel = _midLevel = _highLevel = 0f;
        _fadeFromMode = null;
        _crossfade = 1.0;
        InvalidateVisual();
    }

    private bool IsAtRest()
    {
        if (_punch.Value > SettleEpsilon || _bassLevel > SettleEpsilon || _midLevel > SettleEpsilon) return false;
        foreach (var v in _display)
            if (v > SettleEpsilon) return false;
        return true;
    }

    // ───────────────────────── per-frame update ─────────────────────────

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_ticking) return;

        // WPF may raise Rendering several times per composition frame; only advance once per frame.
        var now = e is RenderingEventArgs re ? re.RenderingTime : _lastRenderingTime + TimeSpan.FromSeconds(1.0 / 60.0);
        if (_hasLastRenderingTime && now == _lastRenderingTime) return;
        var dt = _hasLastRenderingTime ? (now - _lastRenderingTime).TotalSeconds : 1.0 / 60.0;
        if (!(dt > 0)) dt = 1.0 / 60.0;
        dt = Math.Min(dt, MaxFrameDelta);
        _lastRenderingTime = now;
        _hasLastRenderingTime = true;

        var polling = IsPolling;
        var hasData = false;
        if (polling)
        {
            try
            {
                hasData = _source!.TryGetBands(_target, out _);
            }
            catch (Exception ex)
            {
                Log.Warn("Spectrum source read failed", ex);
                hasData = false;
            }
        }

        if (!hasData)
        {
            Array.Clear(_target);
        }
        else
        {
            var sensitivity = (float)Sensitivity;
            for (var i = 0; i < _target.Length; i++)
            {
                var t = _target[i];
                if (!float.IsFinite(t)) t = 0f;
                _target[i] = Math.Clamp(t * sensitivity, 0f, 1f);
            }
        }

        var rise = (float)(1.0 - Math.Exp(-RiseRate * dt));
        var fall = (float)(1.0 - Math.Exp(-FallRate * dt));
        var changed = false;
        var moving = false;
        for (var i = 0; i < _display.Length; i++)
        {
            var target = _target[i];
            var current = _display[i];
            var diff = target - current;
            if (Math.Abs(diff) <= SettleEpsilon)
            {
                if (current != target)
                {
                    _display[i] = target;
                    changed = true;
                }
                continue;
            }
            _display[i] = current + diff * (diff > 0 ? rise : fall);
            changed = true;
            moving = true;
        }

        // Kick detector for the first (bass) bar / wave point.
        var previousPunch = _punch.Value;
        var bass = hasData ? SpectrumShaper.PeakBetween(_display, SpectrumShaper.MinHz, SpectrumShaper.BassHighHz) : 0f;
        var punch = _punch.Update(bass, dt);
        if (Math.Abs(punch - previousPunch) > SettleEpsilon || punch > SettleEpsilon)
        {
            changed |= punch != previousPunch;
            moving |= punch > SettleEpsilon;
        }

        // BassWave: smoothed band groups + a phase that flows faster on kicks (animates every frame while playing).
        if (Mode == VisualizerStyle.BassWave)
        {
            var span = (ReadOnlySpan<float>)_display;
            var lowMid = SpectrumShaper.PeakBetween(span, 150, 600);
            var high = SpectrumShaper.PeakBetween(span, 2000, 8000);
            var k = (float)(1.0 - Math.Exp(-12.0 * dt));
            _bassLevel += (bass - _bassLevel) * k;
            _midLevel += (lowMid - _midLevel) * k;
            _highLevel += (high - _highLevel) * k;
            var energy = Math.Max(_bassLevel, Math.Max(_midLevel, punch));
            if (polling || energy > SettleEpsilon)
            {
                _flowPhase += dt * (1.4 + 7.0 * punch);
                if (_flowPhase > 1000) _flowPhase -= 1000;
                changed = true;
                moving |= energy > SettleEpsilon;
            }
        }

        if (_crossfade < 1.0)
        {
            _crossfade = Math.Min(1.0, _crossfade + dt / CrossfadeSeconds);
            if (_crossfade >= 1.0) _fadeFromMode = null;
            changed = true;
        }

        // Unchanged frame and nothing decaying → no redraw.
        if (changed) InvalidateVisual();

        if (!polling && !moving && _crossfade >= 1.0) StopTicking();
    }

    // ───────────────────────── layout / rendering ─────────────────────────

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var mode = Mode;
        var count = ResolveCount(mode);
        var width = mode switch
        {
            VisualizerStyle.MinimalBars => count * MinimalBarWidth + (count - 1) * MinimalBarGap,
            VisualizerStyle.Equalizer => count * 5.0,
            _ => 64.0,
        };
        var height = mode switch
        {
            VisualizerStyle.MinimalBars => 16.0,
            VisualizerStyle.BassWave => 48.0,
            _ => 20.0,
        };
        return new Size(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (!(width >= 1) || !(height >= 1)) return;

        var brush = BarBrush;
        if (brush is null) return;

        if (_fadeFromMode is { } from && _crossfade < 1.0)
        {
            var t = SmoothStep(_crossfade);
            DrawMode(drawingContext, from, brush, width, height, 1.0 - t);
            DrawMode(drawingContext, Mode, brush, width, height, t);
        }
        else
        {
            DrawMode(drawingContext, Mode, brush, width, height, 1.0);
        }
    }

    private int ResolveCount(VisualizerStyle mode)
    {
        var requested = BarCount;
        if (requested > 0) return Math.Clamp(requested, mode == VisualizerStyle.Waveform ? 4 : 1, MaxBars);
        return mode switch
        {
            VisualizerStyle.Equalizer => DefaultEqualizerBars,
            VisualizerStyle.Waveform => DefaultWaveformPoints,
            _ => DefaultMinimalBars,
        };
    }

    private void DrawMode(DrawingContext dc, VisualizerStyle mode, Brush brush, double width, double height, double opacity)
    {
        if (opacity <= 0.001) return;

        var count = ResolveCount(mode);
        Span<float> buffer = stackalloc float[MaxBars];
        var values = buffer.Slice(0, count);
        SampleDisplay(values);

        var pushed = opacity < 0.999;
        if (pushed) dc.PushOpacity(opacity);
        switch (mode)
        {
            case VisualizerStyle.Equalizer:
                DrawEqualizer(dc, brush, width, height, values);
                break;
            case VisualizerStyle.Waveform:
                DrawWaveform(dc, brush, width, height, values);
                break;
            case VisualizerStyle.BassWave:
                DrawBassWave(dc, brush, width, height);
                break;
            default:
                DrawMinimalBars(dc, brush, width, height, values);
                break;
        }
        if (pushed) dc.Pop();
    }

    /// <summary>
    /// iPhone-style mapping: every bar / wave point listens to a fixed frequency on a logarithmic axis
    /// (<see cref="SpectrumShaper.FixedFrequencies"/>); the first one is the bass and jumps hard on kicks.
    /// </summary>
    private void SampleDisplay(Span<float> destination)
    {
        SpectrumShaper.FixedFrequencies(_display, destination);
        if (destination.Length > 0)
            destination[0] = Math.Clamp(0.4f * destination[0] + _punch.Value, 0f, 1f);
    }

    /// <summary>A few pill-shaped bars centred vertically; at rest they are small pills (≈15 % height).</summary>
    private static void DrawMinimalBars(DrawingContext dc, Brush brush, double width, double height, ReadOnlySpan<float> values)
    {
        var n = values.Length;
        var barWidth = MinimalBarWidth;
        var gap = MinimalBarGap;
        var total = n * barWidth + (n - 1) * gap;
        if (total > width)
        {
            var k = width / total;
            barWidth *= k;
            gap *= k;
            total = width;
        }

        var minHeight = Math.Min(height, Math.Max(barWidth, height * MinimalRestFraction));
        var radius = barWidth / 2;
        var x = (width - total) / 2;
        for (var j = 0; j < n; j++)
        {
            var barHeight = minHeight + (height - minHeight) * values[j];
            dc.DrawRoundedRectangle(brush, null, new Rect(x, (height - barHeight) / 2, barWidth, barHeight), radius, radius);
            x += barWidth + gap;
        }
    }

    /// <summary>More, thinner rounded bars growing from the bottom with a subtle vertical opacity gradient.</summary>
    private static void DrawEqualizer(DrawingContext dc, Brush brush, double width, double height, ReadOnlySpan<float> values)
    {
        var n = values.Length;
        var slot = width / n;
        var barWidth = Math.Clamp(slot * 0.55, 1.0, 4.0);
        var minHeight = Math.Min(height, barWidth);
        var radius = barWidth / 2;

        dc.PushOpacityMask(EqualizerOpacityMask);
        for (var j = 0; j < n; j++)
        {
            var barHeight = minHeight + (height - minHeight) * values[j];
            var x = j * slot + (slot - barWidth) / 2;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, height - barHeight, barWidth, barHeight), radius, radius);
        }
        dc.Pop();
    }

    /// <summary>
    /// Smooth symmetric wave: Catmull-Rom curve through the fixed-frequency points (bass first, on the left), rising
    /// from the centre line at both ends, mirrored around the centre. The fill paints the brush over the whole control
    /// clipped to the wave (so an album-art brush keeps a stable gradient), plus a 1.5 DIP outline. At rest: a flat line.
    /// </summary>
    private void DrawWaveform(DrawingContext dc, Brush brush, double width, double height, ReadOnlySpan<float> values)
    {
        var n = values.Length;
        if (n < 2) return;

        var pad = WaveStrokeThickness;
        var usableWidth = Math.Max(1.0, width - 2 * pad);
        var centerY = height / 2;
        var maxAmplitude = Math.Max(0.0, centerY - WaveStrokeThickness);

        // Quiet anchor points at both ends; the data points sit between them.
        var total = n + 2;
        Span<Point> upper = stackalloc Point[MaxBars + 2];
        upper = upper.Slice(0, total);
        upper[0] = new Point(pad, centerY);
        upper[total - 1] = new Point(pad + usableWidth, centerY);
        for (var i = 0; i < n; i++)
        {
            var u = WaveEdge + (1 - 2 * WaveEdge) * i / (n - 1);
            upper[i + 1] = new Point(pad + u * usableWidth, centerY - values[i] * maxAmplitude);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(upper[0], true, true);

            // Upper edge, left → right.
            for (var i = 0; i < total - 1; i++)
            {
                AppendCatmullRom(ctx,
                    upper[Math.Max(i - 1, 0)], upper[i], upper[i + 1], upper[Math.Min(i + 2, total - 1)],
                    centerY, isUpper: true);
            }

            // Lower edge (mirror image), right → left.
            for (var i = total - 1; i > 0; i--)
            {
                AppendCatmullRom(ctx,
                    Mirror(upper[Math.Min(i + 1, total - 1)], centerY), Mirror(upper[i], centerY),
                    Mirror(upper[i - 1], centerY), Mirror(upper[Math.Max(i - 2, 0)], centerY),
                    centerY, isUpper: false);
            }
        }
        geometry.Freeze();

        dc.PushClip(geometry);
        dc.PushOpacity(WaveFillOpacity);
        dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        dc.Pop();
        dc.Pop();
        dc.DrawGeometry(null, GetStrokePen(brush), geometry);
    }

    /// <summary>
    /// Three layered "liquid" waves rising from the bottom edge, like a water surface: the front one follows the bass and
    /// jumps on kicks, the middle one the low mids, the back one the highs. Each travels at its own speed and the whole
    /// flow speeds up on kicks. Filled with the brush over the full control (album-art gradient stays put).
    /// </summary>
    private void DrawBassWave(DrawingContext dc, Brush brush, double width, double height)
    {
        var punch = _punch.Value;
        var layers = new (double Cycles, double Speed, double Amp, double Opacity, double Offset)[]
        {
            (3.1, 1.9, 0.30 + 0.55 * _highLevel, 0.30, 0.9),
            (2.0, -1.3, 0.25 + 0.75 * _midLevel, 0.45, 2.1),
            (1.25, 1.0, 0.20 + 0.45 * _bassLevel + 0.55 * punch, 0.75, 0.0),
        };

        const int points = 56;
        var rect = new Rect(0, 0, width, height);
        foreach (var (cycles, speed, amp, opacity, offset) in layers)
        {
            var a = Math.Clamp(amp, 0, 1);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, height), true, true);
                for (var i = 0; i <= points; i++)
                {
                    var u = (double)i / points;
                    var envelope = 0.35 + 0.65 * Math.Sin(Math.PI * u);
                    var wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * cycles * u + _flowPhase * speed + offset);
                    // Rest level: a thin band at the bottom; louder → the surface rises and swells.
                    var rise = height * (0.08 + 0.92 * a * envelope * (0.35 + 0.65 * wave));
                    ctx.LineTo(new Point(u * width, height - rise), true, true);
                }
                ctx.LineTo(new Point(width, height), true, false);
            }
            geometry.Freeze();

            dc.PushClip(geometry);
            dc.PushOpacity(opacity);
            dc.DrawRectangle(brush, null, rect);
            dc.Pop();
            dc.Pop();
        }
    }

    private static void AppendCatmullRom(StreamGeometryContext ctx, Point p0, Point p1, Point p2, Point p3, double centerY, bool isUpper)
    {
        var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
        var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);

        // Never let the curve overshoot across the centre line.
        if (isUpper)
        {
            c1.Y = Math.Min(c1.Y, centerY);
            c2.Y = Math.Min(c2.Y, centerY);
        }
        else
        {
            c1.Y = Math.Max(c1.Y, centerY);
            c2.Y = Math.Max(c2.Y, centerY);
        }

        ctx.BezierTo(c1, c2, p2, true, true);
    }

    private static Point Mirror(Point p, double centerY) => new(p.X, 2 * centerY - p.Y);

    private Pen GetStrokePen(Brush brush)
    {
        if (_strokePen is not null && ReferenceEquals(_strokePenBrush, brush)) return _strokePen;

        var pen = new Pen(brush, WaveStrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        // A frozen brush allows a frozen pen; an animated/unfrozen caller brush keeps working unfrozen.
        if (pen.CanFreeze) pen.Freeze();
        _strokePen = pen;
        _strokePenBrush = brush;
        return pen;
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static Brush CreateEqualizerMask()
    {
        // Relative to the bars' bounding box: tops ≈ 65 % opacity, bottoms fully opaque.
        var mask = new LinearGradientBrush(Color.FromArgb(166, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
            new Point(0, 0), new Point(0, 1));
        mask.Freeze();
        return mask;
    }
}
