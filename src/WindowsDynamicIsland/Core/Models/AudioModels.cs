namespace WindowsDynamicIsland.Core.Models;

public enum VisualizerStyle
{
    /// <summary>Vertical frequency bars.</summary>
    Equalizer,
    /// <summary>Smooth wave line built from the spectrum.</summary>
    Waveform,
    /// <summary>A few minimal rounded bars (compact live-activity look).</summary>
    MinimalBars,
    /// <summary>Layered flowing waves rising from the bottom, driven by the bass (expanded island background).</summary>
    BassWave,
}

/// <summary>
/// Real-time spectrum produced by the audio analyzer (WASAPI loopback + FFT).
/// Implemented by the Audio module and registered in <see cref="ServiceRegistry"/>.
/// Consumers (the visualizer control) poll it from the UI thread at render rate — no events per frame.
/// </summary>
public interface ISpectrumSource
{
    /// <summary>Number of logarithmic frequency bands produced (constant, e.g. 32).</summary>
    int BandCount { get; }

    /// <summary>True while loopback capture is running.</summary>
    bool IsCapturing { get; }

    /// <summary>Increments every time a new analysis frame is produced.</summary>
    long FrameNumber { get; }

    /// <summary>
    /// Copies the latest band magnitudes (normalized 0..1, already smoothed) into <paramref name="destination"/>
    /// (length ≥ 1; if it differs from BandCount the source resamples). Returns false when no data is available.
    /// <paramref name="level"/> receives overall RMS loudness 0..1. Thread-safe, allocation-free.
    /// </summary>
    bool TryGetBands(Span<float> destination, out float level);

    /// <summary>
    /// Registers interest in spectrum data. Capture runs only while at least one consumer is registered
    /// AND media is playing (saves CPU). Dispose the token to unregister.
    /// </summary>
    IDisposable AcquireConsumer();
}

/// <summary>Published by the audio module when capture starts/stops or fails.</summary>
public sealed record AudioCaptureStateChanged(bool IsCapturing, string? Error);
