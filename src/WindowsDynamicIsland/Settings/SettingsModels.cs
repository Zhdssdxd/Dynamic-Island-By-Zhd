using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Settings;

/// <summary>
/// Root of persisted user settings (JSON at %AppData%\WindowsDynamicIsland\settings.json).
/// Plain mutable POCOs: mutate ONLY inside <see cref="SettingsManager.Update"/>; everyone else reads
/// the immutable-by-convention snapshot from <see cref="SettingsManager.Current"/> / <see cref="Core.Models.SettingsChanged"/>.
/// Adding a property: give it a sensible default so old settings files keep working.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public AppearanceSettings Appearance { get; set; } = new();
    public FeatureSettings Features { get; set; } = new();
    public IndicatorSettings Indicators { get; set; } = new();
    public SystemSettings System { get; set; } = new();
    public TimerSettings Timer { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
}

public enum IslandColorScheme
{
    /// <summary>Pure black capsule (classic Dynamic Island).</summary>
    Black,
    /// <summary>Very dark graphite with slightly lighter surfaces.</summary>
    Graphite,
    /// <summary>Black capsule with a tinted accent taken from album art.</summary>
    Adaptive,
}

public enum IslandHorizontalPosition
{
    Left,
    Center,
    Right,
}

/// <summary>How the compact island looks while it sits inside a top bar.</summary>
public enum TopBarFill
{
    /// <summary>Black capsule, as usual.</summary>
    Black,
    /// <summary>Transparent capsule — only its outline is drawn, so it blends with the bar.</summary>
    Outline,
}

public sealed class AppearanceSettings
{
    /// <summary>Compact (idle) capsule width in DIPs.</summary>
    public double CompactWidth { get; set; } = 168;
    /// <summary>Compact capsule height in DIPs.</summary>
    public double CompactHeight { get; set; } = 34;
    /// <summary>Corner radius in DIPs for the compact capsule. Values ≥ height/2 give a perfect pill.</summary>
    public double CornerRadius { get; set; } = 17;
    /// <summary>Corner radius for expanded states.</summary>
    public double ExpandedCornerRadius { get; set; } = 30;
    /// <summary>Capsule background opacity 0.5..1.</summary>
    public double Opacity { get; set; } = 0.97;
    public IslandColorScheme ColorScheme { get; set; } = IslandColorScheme.Black;
    /// <summary>Animation speed multiplier: 0.5 (slow) .. 2.0 (fast). 1 = default.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    public IslandHorizontalPosition HorizontalPosition { get; set; } = IslandHorizontalPosition.Center;
    /// <summary>Gap between the top edge of the monitor and the capsule, DIPs.</summary>
    public double TopOffset { get; set; } = 6;
    /// <summary>Extra horizontal offset (DIPs, can be negative).</summary>
    public double HorizontalOffset { get; set; }
    /// <summary>Show the idle capsule when nothing is happening. When false, the island hides until an event.</summary>
    public bool ShowWhenIdle { get; set; } = true;
    /// <summary>Expand on hover (after a short delay) in addition to click.</summary>
    public bool ExpandOnHover { get; set; }
    /// <summary>When expanded with music/video playing, the bottom edge of the capsule bounces to the beat.</summary>
    public bool PulseToBeat { get; set; } = true;
    /// <summary>Live sound wave between the artwork and the bars in the compact music view.</summary>
    public bool CompactWave { get; set; } = true;
    /// <summary>Soft sound wave along the bottom of the expanded music view.</summary>
    public bool ExpandedWave { get; set; } = true;
    /// <summary>Paint the sound waves with a heavily blurred copy of the album art instead of a flat colour.</summary>
    public bool WaveArtworkColors { get; set; } = true;
    /// <summary>
    /// Top-bar mode: the island sits at the very top of the monitor and the compact capsule is exactly as tall as the
    /// bar docked there (taskbar on top, YASB, a macOS-style menu bar…). Expanded states still open below it.
    /// </summary>
    public bool TopBarMode { get; set; }
    /// <summary>Compact capsule look in top-bar mode.</summary>
    public TopBarFill TopBarFill { get; set; } = TopBarFill.Black;
    /// <summary>Bar height in DIPs for top-bar mode; 0 = detect from the work area (32 when nothing is docked on top).</summary>
    public double TopBarHeight { get; set; }
}

public sealed class FeatureSettings
{
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Seconds a notification stays expanded.</summary>
    public double NotificationDurationSeconds { get; set; } = 5;
    public bool MusicModeEnabled { get; set; } = true;
    public bool VideoModeEnabled { get; set; } = true;
    /// <summary>Show synchronized lyrics (from lrclib.net) in the expanded music view.</summary>
    public bool LyricsEnabled { get; set; } = true;
    public bool VisualizerEnabled { get; set; } = true;
    public VisualizerStyle VisualizerStyle { get; set; } = VisualizerStyle.MinimalBars;
    /// <summary>Visualizer sensitivity multiplier 0.5..2.</summary>
    public double VisualizerSensitivity { get; set; } = 1.0;
    /// <summary>
    /// Render endpoint ID captured by the visualizer; empty = automatic: the default output device, switching to
    /// whichever endpoint is actually playing (virtual mixers such as SteelSeries Sonar or Voicemeeter route apps
    /// to different endpoints).
    /// </summary>
    public string VisualizerDeviceId { get; set; } = "";
    public bool DownloadsEnabled { get; set; } = true;
    /// <summary>Remember copied text / images / files and show them in the clipboard history window.</summary>
    public bool ClipboardHistoryEnabled { get; set; } = true;
    /// <summary>How many unpinned items the history keeps (10..200).</summary>
    public int ClipboardMaxItems { get; set; } = 50;
    /// <summary>Show a short "Скопировано" indicator in the island on every copy.</summary>
    public bool ClipboardIndicator { get; set; } = true;
    /// <summary>Folder watched for downloads; empty = the user's Downloads known folder.</summary>
    public string DownloadsFolder { get; set; } = "";
}

public sealed class IndicatorSettings
{
    public bool Volume { get; set; } = true;
    public bool Brightness { get; set; } = true;
    public bool Battery { get; set; } = true;
    public bool Charger { get; set; } = true;
    public bool Usb { get; set; } = true;
    public bool Bluetooth { get; set; } = true;
    public bool Microphone { get; set; } = true;
    /// <summary>Seconds an indicator HUD stays visible.</summary>
    public double DurationSeconds { get; set; } = 1.8;
    /// <summary>Battery level (0..1) at which a low-battery warning is shown.</summary>
    public double LowBatteryThreshold { get; set; } = 0.2;
}

public sealed class SystemSettings
{
    public bool LaunchAtStartup { get; set; }
    /// <summary>Start without showing anything except the island (no settings window).</summary>
    public bool StartInBackground { get; set; } = true;
    /// <summary>WinForms Screen.DeviceName of the target monitor; empty = primary monitor.</summary>
    public string MonitorDeviceName { get; set; } = "";
    /// <summary>Hide the island while a full-screen app/game runs on its monitor.</summary>
    public bool HideInFullscreen { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
}

public sealed class TimerSettings
{
    public double DefaultCountdownMinutes { get; set; } = 5;
    /// <summary>Play the system notification sound when a countdown finishes.</summary>
    public bool PlaySoundOnFinish { get; set; } = true;
    /// <summary>Show a notification when a countdown finishes.</summary>
    public bool NotifyOnFinish { get; set; } = true;
    /// <summary>Quick presets (minutes) offered in the UI.</summary>
    public List<double> Presets { get; set; } = new() { 1, 3, 5, 10, 15, 25 };
}

/// <summary>Hotkeys in the form "Ctrl+Alt+I". Empty string = disabled.</summary>
public sealed class HotkeySettings
{
    public bool Enabled { get; set; } = true;
    public string ToggleExpand { get; set; } = "Ctrl+Alt+I";
    public string ToggleVisibility { get; set; } = "Ctrl+Alt+H";
    public string MediaPlayPause { get; set; } = "Ctrl+Alt+P";
    public string StartStopwatch { get; set; } = "Ctrl+Alt+T";
    public string OpenSettings { get; set; } = "Ctrl+Alt+S";
    public string ClipboardHistory { get; set; } = "Ctrl+Alt+V";
}
