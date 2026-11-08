using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Settings;

/// <summary>
/// Loads, validates, persists and broadcasts settings.
/// <see cref="Current"/> is a snapshot that is replaced (never mutated) on every update,
/// so readers on any thread always see a consistent object.
/// </summary>
public sealed class SettingsManager : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IEventBus _bus;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private volatile AppSettings _current;
    private bool _disposed;

    public SettingsManager(IEventBus bus, string? filePath = null)
    {
        _bus = bus;
        _filePath = filePath ?? AppPaths.SettingsFile;
        _current = new AppSettings();
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Current settings snapshot. Treat as read-only.</summary>
    public AppSettings Current => _current;

    /// <summary>Loads settings from disk (or defaults) and publishes the first <see cref="SettingsChanged"/>.</summary>
    public void Load()
    {
        AppSettings loaded;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                loaded = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings file is corrupt, using defaults. A backup copy is kept.", ex);
            TryBackupCorrupt();
            loaded = new AppSettings();
        }

        Normalize(loaded);
        _current = loaded;
        _bus.Publish(new SettingsChanged(loaded));
    }

    /// <summary>
    /// Applies a mutation to a copy of the current settings, validates it, swaps the snapshot,
    /// publishes <see cref="SettingsChanged"/> and schedules a debounced save.
    /// </summary>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings next;
        lock (_gate)
        {
            next = Clone(_current);
            mutate(next);
            Normalize(next);
            _current = next;
        }
        _bus.Publish(new SettingsChanged(next));
        ScheduleSave();
    }

    /// <summary>Restores defaults (keeps nothing).</summary>
    public void ResetToDefaults() => Update(s =>
    {
        var d = new AppSettings();
        s.Appearance = d.Appearance;
        s.Features = d.Features;
        s.Indicators = d.Indicators;
        s.System = d.System;
        s.Timer = d.Timer;
        s.Hotkeys = d.Hotkeys;
    });

    private void ScheduleSave()
    {
        if (_disposed) return;
        try { _saveTimer.Change(400, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    /// <summary>Writes the current snapshot to disk atomically (temp file + replace).</summary>
    public void SaveNow()
    {
        try
        {
            var snapshot = _current;
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }

    public static AppSettings Clone(AppSettings s)
        => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;

    /// <summary>Clamps every numeric value into a safe range and repairs null sub-objects.</summary>
    internal static void Normalize(AppSettings s)
    {
        s.Appearance ??= new AppearanceSettings();
        s.Features ??= new FeatureSettings();
        s.Indicators ??= new IndicatorSettings();
        s.System ??= new SystemSettings();
        s.Timer ??= new TimerSettings();
        s.Hotkeys ??= new HotkeySettings();

        var a = s.Appearance;
        a.CompactWidth = Clamp(a.CompactWidth, 90, 400, 168);
        a.CompactHeight = Clamp(a.CompactHeight, 24, 60, 34);
        a.CornerRadius = Clamp(a.CornerRadius, 0, 30, 17);
        a.ExpandedCornerRadius = Clamp(a.ExpandedCornerRadius, 8, 48, 30);
        a.Opacity = Clamp(a.Opacity, 0.5, 1.0, 0.97);
        a.AnimationSpeed = Clamp(a.AnimationSpeed, 0.5, 2.0, 1.0);
        a.TopOffset = Clamp(a.TopOffset, 0, 200, 6);
        a.HorizontalOffset = Clamp(a.HorizontalOffset, -2000, 2000, 0);

        var f = s.Features;
        f.NotificationDurationSeconds = Clamp(f.NotificationDurationSeconds, 2, 30, 5);
        f.VisualizerSensitivity = Clamp(f.VisualizerSensitivity, 0.5, 2.0, 1.0);
        f.DownloadsFolder ??= "";
        f.VisualizerDeviceId ??= "";

        var i = s.Indicators;
        i.DurationSeconds = Clamp(i.DurationSeconds, 0.8, 10, 1.8);
        i.LowBatteryThreshold = Clamp(i.LowBatteryThreshold, 0.05, 0.5, 0.2);

        s.System.MonitorDeviceName ??= "";

        var t = s.Timer;
        t.DefaultCountdownMinutes = Clamp(t.DefaultCountdownMinutes, 0.1, 24 * 60, 5);
        t.Presets = (t.Presets ?? new List<double>()).Where(p => p > 0 && p <= 24 * 60).Distinct().OrderBy(p => p).Take(8).ToList();
        if (t.Presets.Count == 0) t.Presets = new TimerSettings().Presets;

        var h = s.Hotkeys;
        h.ToggleExpand ??= "";
        h.ToggleVisibility ??= "";
        h.MediaPlayPause ??= "";
        h.StartStopwatch ??= "";
        h.OpenSettings ??= "";
    }

    private static double Clamp(double v, double min, double max, double fallback)
        => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    private void TryBackupCorrupt()
    {
        try { File.Copy(_filePath, _filePath + ".corrupt", overwrite: true); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Dispose();
        SaveNow();
    }
}
