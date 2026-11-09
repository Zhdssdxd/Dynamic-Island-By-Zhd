using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using Forms = System.Windows.Forms;

namespace WindowsDynamicIsland.Settings;

/// <summary>Item of a settings ComboBox. ToString() is the label (used by the selection box and items).</summary>
public sealed class SettingsOption
{
    public SettingsOption(string label, object value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public object Value { get; }

    public override string ToString() => Label;
}

/// <summary>One row of the diagnostics page.</summary>
public sealed class ModuleStatusRow
{
    public ModuleStatusRow(string name, bool isWorking, string? message)
    {
        Name = name;
        IsWorking = isWorking;
        Message = string.IsNullOrWhiteSpace(message) ? (isWorking ? "Работает" : "Недоступно") : message!;
    }

    public string Name { get; }
    public bool IsWorking { get; }
    public string Message { get; }
}

/// <summary>One editable hotkey binding (Settings → Система).</summary>
public sealed class HotkeyEntry : ObservableObject
{
    private string _value = "";
    private bool _isCapturing;
    private string? _partial;
    private string? _error;

    public HotkeyEntry(string id, string label)
    {
        Id = id;
        Label = label;
    }

    /// <summary>Property name in <see cref="HotkeySettings"/>.</summary>
    public string Id { get; }
    public string Label { get; }

    public string Value
    {
        get => _value;
        set { if (SetProperty(ref _value, value ?? "")) OnPropertyChanged(nameof(DisplayText)); }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set { if (SetProperty(ref _isCapturing, value)) OnPropertyChanged(nameof(DisplayText)); }
    }

    /// <summary>Modifiers held so far while capturing ("Ctrl+Alt+…").</summary>
    public string? Partial
    {
        get => _partial;
        set { if (SetProperty(ref _partial, value)) OnPropertyChanged(nameof(DisplayText)); }
    }

    public string? Error
    {
        get => _error;
        set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string DisplayText => _isCapturing
        ? (string.IsNullOrEmpty(_partial) ? "Нажмите сочетание…" : _partial + "+…")
        : (_value.Length == 0 ? "Не задано" : _value);
}

/// <summary>
/// View model of the settings window. Every property writes through <see cref="SettingsManager.Update"/>
/// immediately (sliders debounced ~150 ms) so the island previews changes live. Settings changed elsewhere
/// (tray, other modules) are re-read without feedback loops: values with a pending local edit are not overwritten.
/// Must be created and used on the UI thread.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    private readonly SettingsManager _settings;
    private readonly IEventBus _bus;
    private readonly ModuleStatusRegistry _statuses;
    private readonly Dispatcher _dispatcher;
    private readonly SubscriptionBag _subs = new();
    private readonly DispatcherTimer _debounce;
    private readonly Dictionary<string, Action<AppSettings>> _pending = new();
    private bool _loading;
    private bool _disposed;

    public SettingsViewModel(SettingsManager settings, IEventBus bus, ModuleStatusRegistry statuses, Dispatcher dispatcher)
    {
        _settings = settings;
        _bus = bus;
        _statuses = statuses;
        _dispatcher = dispatcher;

        _debounce = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = DebounceDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Flush();
        };

        Hotkeys = new ObservableCollection<HotkeyEntry>
        {
            new(nameof(HotkeySettings.ToggleExpand), "Развернуть / свернуть остров"),
            new(nameof(HotkeySettings.ToggleVisibility), "Показать / скрыть остров"),
            new(nameof(HotkeySettings.MediaPlayPause), "Воспроизведение / пауза"),
            new(nameof(HotkeySettings.StartStopwatch), "Секундомер / пауза таймера"),
            new(nameof(HotkeySettings.OpenSettings), "Открыть настройки"),
            new(nameof(HotkeySettings.ClipboardHistory), "История буфера обмена"),
        };

        StartTimerCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.StartCountdown)));
        StartPresetCommand = new RelayCommand(p =>
        {
            if (p is double minutes && minutes > 0)
                _bus.Publish(new TimerCommand(TimerCommandKind.StartCountdown, TimeSpan.FromMinutes(minutes)));
        });
        StartStopwatchCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.StartStopwatch)));
        TogglePauseCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.TogglePause)));
        StopTimerCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.Stop)));
        ExtendTimerCommand = new RelayCommand(() => _bus.Publish(new TimerCommand(TimerCommandKind.Extend, TimeSpan.FromMinutes(1))));

        var version = typeof(SettingsViewModel).Assembly.GetName().Version;
        AppVersion = version is null ? "—" : version.ToString(3);

        RefreshMonitors(settings.Current.System.MonitorDeviceName);
        RefreshAudioDevices(settings.Current.Features.VisualizerDeviceId);
        Load(settings.Current);
        UpdateTimerState(bus.GetLast<TimerStateChanged>());
        RefreshStatuses();

        _subs.Add(bus.Subscribe<SettingsChanged>(e => { if (!_disposed) Load(e.Settings); }, onUiThread: true));
        _subs.Add(bus.Subscribe<TimerStateChanged>(e => { if (!_disposed) UpdateTimerState(e); }, onUiThread: true));
        _statuses.Changed += OnStatusesChanged;
    }

    // ───────────────────────────── Option lists ─────────────────────────────

    public IReadOnlyList<SettingsOption> ColorSchemeOptions { get; } = new[]
    {
        new SettingsOption("Чёрный", IslandColorScheme.Black),
        new SettingsOption("Графит", IslandColorScheme.Graphite),
        new SettingsOption("Адаптивный", IslandColorScheme.Adaptive),
    };

    public IReadOnlyList<SettingsOption> TopBarFillOptions { get; } = new[]
    {
        new SettingsOption("Чёрная", global::WindowsDynamicIsland.Settings.TopBarFill.Black),
        new SettingsOption("Только контур", global::WindowsDynamicIsland.Settings.TopBarFill.Outline),
    };

    public IReadOnlyList<SettingsOption> PositionOptions { get; } = new[]
    {
        new SettingsOption("Слева", IslandHorizontalPosition.Left),
        new SettingsOption("По центру", IslandHorizontalPosition.Center),
        new SettingsOption("Справа", IslandHorizontalPosition.Right),
    };

    public IReadOnlyList<SettingsOption> VisualizerStyleOptions { get; } = new[]
    {
        new SettingsOption("Эквалайзер", VisualizerStyle.Equalizer),
        new SettingsOption("Волна", VisualizerStyle.Waveform),
        new SettingsOption("Минимальные полосы", VisualizerStyle.MinimalBars),
    };

    private IReadOnlyList<SettingsOption> _monitorOptions = Array.Empty<SettingsOption>();
    public IReadOnlyList<SettingsOption> MonitorOptions { get => _monitorOptions; private set => SetProperty(ref _monitorOptions, value); }

    // ───────────────────────────── Appearance ─────────────────────────────

    private double _compactWidth;
    public double CompactWidth
    {
        get => _compactWidth;
        set => Set(ref _compactWidth, value, (s, v) => s.Appearance.CompactWidth = v, true);
    }

    private double _compactHeight;
    public double CompactHeight
    {
        get => _compactHeight;
        set
        {
            Set(ref _compactHeight, value, (s, v) => s.Appearance.CompactHeight = v, true);
            OnPropertyChanged(nameof(PreviewCornerRadius));
        }
    }

    private double _cornerRadius;
    public double CornerRadius
    {
        get => _cornerRadius;
        set
        {
            Set(ref _cornerRadius, value, (s, v) => s.Appearance.CornerRadius = v, true);
            OnPropertyChanged(nameof(PreviewCornerRadius));
        }
    }

    private double _expandedCornerRadius;
    public double ExpandedCornerRadius
    {
        get => _expandedCornerRadius;
        set
        {
            Set(ref _expandedCornerRadius, value, (s, v) => s.Appearance.ExpandedCornerRadius = v, true);
            OnPropertyChanged(nameof(PreviewExpandedCornerRadius));
        }
    }

    private double _capsuleOpacity;
    public double CapsuleOpacity
    {
        get => _capsuleOpacity;
        set
        {
            Set(ref _capsuleOpacity, value, (s, v) => s.Appearance.Opacity = v, true);
            OnPropertyChanged(nameof(PreviewBrush));
        }
    }

    private IslandColorScheme _colorScheme;
    public IslandColorScheme ColorScheme
    {
        get => _colorScheme;
        set
        {
            Set(ref _colorScheme, value, (s, v) => s.Appearance.ColorScheme = v);
            OnPropertyChanged(nameof(PreviewBrush));
        }
    }

    private double _animationSpeed;
    public double AnimationSpeed
    {
        get => _animationSpeed;
        set => Set(ref _animationSpeed, value, (s, v) => s.Appearance.AnimationSpeed = v, true);
    }

    private IslandHorizontalPosition _horizontalPosition;
    public IslandHorizontalPosition HorizontalPosition
    {
        get => _horizontalPosition;
        set
        {
            Set(ref _horizontalPosition, value, (s, v) => s.Appearance.HorizontalPosition = v);
            OnPropertyChanged(nameof(PreviewAlignment));
        }
    }

    private double _topOffset;
    public double TopOffset
    {
        get => _topOffset;
        set
        {
            Set(ref _topOffset, value, (s, v) => s.Appearance.TopOffset = v, true);
            OnPropertyChanged(nameof(PreviewMargin));
        }
    }

    private double _horizontalOffset;
    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            Set(ref _horizontalOffset, value, (s, v) => s.Appearance.HorizontalOffset = v, true);
            OnPropertyChanged(nameof(PreviewMargin));
        }
    }

    private bool _showWhenIdle;
    public bool ShowWhenIdle
    {
        get => _showWhenIdle;
        set => Set(ref _showWhenIdle, value, (s, v) => s.Appearance.ShowWhenIdle = v);
    }

    private bool _expandOnHover;
    private bool _pulseToBeat;
    public bool PulseToBeat
    {
        get => _pulseToBeat;
        set => Set(ref _pulseToBeat, value, (s, v) => s.Appearance.PulseToBeat = v);
    }

    private bool _compactWave;
    public bool CompactWave
    {
        get => _compactWave;
        set => Set(ref _compactWave, value, (s, v) => s.Appearance.CompactWave = v);
    }

    private bool _expandedWave;
    public bool ExpandedWave
    {
        get => _expandedWave;
        set => Set(ref _expandedWave, value, (s, v) => s.Appearance.ExpandedWave = v);
    }

    private bool _waveArtworkColors;
    public bool WaveArtworkColors
    {
        get => _waveArtworkColors;
        set => Set(ref _waveArtworkColors, value, (s, v) => s.Appearance.WaveArtworkColors = v);
    }

    private bool _topBarMode;
    public bool TopBarMode
    {
        get => _topBarMode;
        set => Set(ref _topBarMode, value, (s, v) => s.Appearance.TopBarMode = v);
    }

    private TopBarFill _topBarFill;
    public TopBarFill TopBarFill
    {
        get => _topBarFill;
        set => Set(ref _topBarFill, value, (s, v) => s.Appearance.TopBarFill = v);
    }

    private double _topBarHeight;
    public double TopBarHeight
    {
        get => _topBarHeight;
        set
        {
            Set(ref _topBarHeight, value, (s, v) => s.Appearance.TopBarHeight = v, true);
            OnPropertyChanged(nameof(TopBarHeightText));
        }
    }

    /// <summary>"авто" for 0, otherwise the height in px.</summary>
    public string TopBarHeightText => TopBarHeight <= 0 ? "авто" : $"{TopBarHeight:0} px";

    public bool ExpandOnHover
    {
        get => _expandOnHover;
        set => Set(ref _expandOnHover, value, (s, v) => s.Appearance.ExpandOnHover = v);
    }

    // Live preview helpers
    public System.Windows.CornerRadius PreviewCornerRadius => new(Math.Min(_cornerRadius, _compactHeight / 2));

    public System.Windows.CornerRadius PreviewExpandedCornerRadius => new(_expandedCornerRadius * 0.6);

    public HorizontalAlignment PreviewAlignment => _horizontalPosition switch
    {
        IslandHorizontalPosition.Left => HorizontalAlignment.Left,
        IslandHorizontalPosition.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    public Thickness PreviewMargin
    {
        get
        {
            // Scaled-down hint of the real offsets so the preview stays inside its frame.
            var top = Math.Clamp(_topOffset, 0, 40);
            var shift = Math.Clamp(_horizontalOffset / 4, -60, 60);
            return new Thickness(16 + Math.Max(0, shift), top, 16 + Math.Max(0, -shift), 0);
        }
    }

    public Brush PreviewBrush
    {
        get
        {
            var alpha = (byte)Math.Round(Math.Clamp(_capsuleOpacity, 0, 1) * 255);
            Brush brush = _colorScheme switch
            {
                IslandColorScheme.Graphite => new SolidColorBrush(Color.FromArgb(alpha, 0x0E, 0x0E, 0x10)),
                IslandColorScheme.Adaptive => new LinearGradientBrush(
                    Color.FromArgb(alpha, 0x00, 0x00, 0x00),
                    Color.FromArgb(alpha, 0x0B, 0x1A, 0x33),
                    0),
                _ => new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
            };
            brush.Freeze();
            return brush;
        }
    }

    // ───────────────────────────── Features ─────────────────────────────

    private bool _notificationsEnabled;
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set => Set(ref _notificationsEnabled, value, (s, v) => s.Features.NotificationsEnabled = v);
    }

    private double _notificationDuration;
    public double NotificationDuration
    {
        get => _notificationDuration;
        set => Set(ref _notificationDuration, value, (s, v) => s.Features.NotificationDurationSeconds = v, true);
    }

    private bool _musicMode;
    public bool MusicMode
    {
        get => _musicMode;
        set => Set(ref _musicMode, value, (s, v) => s.Features.MusicModeEnabled = v);
    }

    private bool _videoMode;
    public bool VideoMode
    {
        get => _videoMode;
        set => Set(ref _videoMode, value, (s, v) => s.Features.VideoModeEnabled = v);
    }

    private bool _lyricsEnabled;
    public bool LyricsEnabled
    {
        get => _lyricsEnabled;
        set => Set(ref _lyricsEnabled, value, (s, v) => s.Features.LyricsEnabled = v);
    }

    private bool _visualizerEnabled;
    public bool VisualizerEnabled
    {
        get => _visualizerEnabled;
        set => Set(ref _visualizerEnabled, value, (s, v) => s.Features.VisualizerEnabled = v);
    }

    private VisualizerStyle _visualizerStyle;
    public VisualizerStyle VisualizerStyle
    {
        get => _visualizerStyle;
        set => Set(ref _visualizerStyle, value, (s, v) => s.Features.VisualizerStyle = v);
    }

    private string _visualizerDeviceId = "";
    /// <summary>Output device captured by the visualizer ("" = Windows default).</summary>
    public string VisualizerDeviceId
    {
        get => _visualizerDeviceId;
        set => Set(ref _visualizerDeviceId, value ?? "", (s, v) => s.Features.VisualizerDeviceId = v);
    }

    private IReadOnlyList<SettingsOption> _audioDeviceOptions = Array.Empty<SettingsOption>();
    public IReadOnlyList<SettingsOption> AudioDeviceOptions { get => _audioDeviceOptions; private set => SetProperty(ref _audioDeviceOptions, value); }

    private double _visualizerSensitivity;
    public double VisualizerSensitivity
    {
        get => _visualizerSensitivity;
        set => Set(ref _visualizerSensitivity, value, (s, v) => s.Features.VisualizerSensitivity = v, true);
    }

    private bool _downloadsEnabled;
    private bool _clipboardHistoryEnabled;
    public bool ClipboardHistoryEnabled
    {
        get => _clipboardHistoryEnabled;
        set => Set(ref _clipboardHistoryEnabled, value, (s, v) => s.Features.ClipboardHistoryEnabled = v);
    }

    private bool _clipboardIndicator;
    public bool ClipboardIndicator
    {
        get => _clipboardIndicator;
        set => Set(ref _clipboardIndicator, value, (s, v) => s.Features.ClipboardIndicator = v);
    }

    private double _clipboardMaxItems;
    public double ClipboardMaxItems
    {
        get => _clipboardMaxItems;
        set => Set(ref _clipboardMaxItems, value, (s, v) => s.Features.ClipboardMaxItems = (int)Math.Round(v), true);
    }

    public ICommand OpenClipboardHistoryCommand => _openClipboardHistory ??= new RelayCommand(() => _bus.Publish(new OpenClipboardHistoryRequested()));
    private ICommand? _openClipboardHistory;

    public bool DownloadsEnabled
    {
        get => _downloadsEnabled;
        set => Set(ref _downloadsEnabled, value, (s, v) => s.Features.DownloadsEnabled = v);
    }

    private string _downloadsFolder = "";
    public string DownloadsFolder
    {
        get => _downloadsFolder;
        set
        {
            Set(ref _downloadsFolder, value ?? "", (s, v) => s.Features.DownloadsFolder = v);
            OnPropertyChanged(nameof(DownloadsFolderDisplay));
            OnPropertyChanged(nameof(HasCustomDownloadsFolder));
        }
    }

    public string DownloadsFolderDisplay => _downloadsFolder.Length == 0 ? "Стандартная папка «Загрузки»" : _downloadsFolder;

    public bool HasCustomDownloadsFolder => _downloadsFolder.Length > 0;

    // ───────────────────────────── Indicators ─────────────────────────────

    private bool _indVolume;
    public bool IndicatorVolume { get => _indVolume; set => Set(ref _indVolume, value, (s, v) => s.Indicators.Volume = v); }

    private bool _indBrightness;
    public bool IndicatorBrightness { get => _indBrightness; set => Set(ref _indBrightness, value, (s, v) => s.Indicators.Brightness = v); }

    private bool _indBattery;
    public bool IndicatorBattery { get => _indBattery; set => Set(ref _indBattery, value, (s, v) => s.Indicators.Battery = v); }

    private bool _indCharger;
    public bool IndicatorCharger { get => _indCharger; set => Set(ref _indCharger, value, (s, v) => s.Indicators.Charger = v); }

    private bool _indUsb;
    public bool IndicatorUsb { get => _indUsb; set => Set(ref _indUsb, value, (s, v) => s.Indicators.Usb = v); }

    private bool _indBluetooth;
    public bool IndicatorBluetooth { get => _indBluetooth; set => Set(ref _indBluetooth, value, (s, v) => s.Indicators.Bluetooth = v); }

    private bool _indMicrophone;
    public bool IndicatorMicrophone { get => _indMicrophone; set => Set(ref _indMicrophone, value, (s, v) => s.Indicators.Microphone = v); }

    private double _hudDuration;
    public double HudDuration
    {
        get => _hudDuration;
        set => Set(ref _hudDuration, value, (s, v) => s.Indicators.DurationSeconds = v, true);
    }

    private double _lowBatteryThreshold;
    public double LowBatteryThreshold
    {
        get => _lowBatteryThreshold;
        set => Set(ref _lowBatteryThreshold, value, (s, v) => s.Indicators.LowBatteryThreshold = v, true);
    }

    // ───────────────────────────── Timer ─────────────────────────────

    private double _defaultCountdownMinutes;
    public double DefaultCountdownMinutes
    {
        get => _defaultCountdownMinutes;
        set => Set(ref _defaultCountdownMinutes, value, (s, v) => s.Timer.DefaultCountdownMinutes = v, true);
    }

    private string _presetsText = "";
    /// <summary>Comma/semicolon/space separated minutes, e.g. "1, 3, 5, 10". Invalid entries are dropped.</summary>
    public string PresetsText
    {
        get => _presetsText;
        set => Set(ref _presetsText, value ?? "", (s, v) => s.Timer.Presets = ParsePresets(v));
    }

    private IReadOnlyList<double> _presets = Array.Empty<double>();
    public IReadOnlyList<double> Presets { get => _presets; private set => SetProperty(ref _presets, value); }

    private bool _timerSound;
    public bool TimerSound { get => _timerSound; set => Set(ref _timerSound, value, (s, v) => s.Timer.PlaySoundOnFinish = v); }

    private bool _timerNotify;
    public bool TimerNotify { get => _timerNotify; set => Set(ref _timerNotify, value, (s, v) => s.Timer.NotifyOnFinish = v); }

    private string _timerStatusText = "Таймер не запущен";
    public string TimerStatusText { get => _timerStatusText; private set => SetProperty(ref _timerStatusText, value); }

    private bool _isTimerActive;
    public bool IsTimerActive { get => _isTimerActive; private set => SetProperty(ref _isTimerActive, value); }

    private bool _isCountdownActive;
    public bool IsCountdownActive { get => _isCountdownActive; private set => SetProperty(ref _isCountdownActive, value); }

    private string _pauseButtonText = "Пауза";
    public string PauseButtonText { get => _pauseButtonText; private set => SetProperty(ref _pauseButtonText, value); }

    public ICommand StartTimerCommand { get; }
    public ICommand StartPresetCommand { get; }
    public ICommand StartStopwatchCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand StopTimerCommand { get; }
    public ICommand ExtendTimerCommand { get; }

    // ───────────────────────────── System ─────────────────────────────

    private bool _launchAtStartup;
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value, (s, v) => s.System.LaunchAtStartup = v); }

    private bool _startInBackground;
    public bool StartInBackground { get => _startInBackground; set => Set(ref _startInBackground, value, (s, v) => s.System.StartInBackground = v); }

    private string _monitorDeviceName = "";
    public string MonitorDeviceName
    {
        get => _monitorDeviceName;
        set => Set(ref _monitorDeviceName, value ?? "", (s, v) => s.System.MonitorDeviceName = v);
    }

    private bool _hideInFullscreen;
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value, (s, v) => s.System.HideInFullscreen = v); }

    private bool _showTrayIcon;
    public bool ShowTrayIcon { get => _showTrayIcon; set => Set(ref _showTrayIcon, value, (s, v) => s.System.ShowTrayIcon = v); }

    private bool _hotkeysEnabled;
    public bool HotkeysEnabled { get => _hotkeysEnabled; set => Set(ref _hotkeysEnabled, value, (s, v) => s.Hotkeys.Enabled = v); }

    public ObservableCollection<HotkeyEntry> Hotkeys { get; }

    // ───────────────────────────── Diagnostics / About ─────────────────────────────

    public ObservableCollection<ModuleStatusRow> Statuses { get; } = new();

    private bool _hasStatuses;
    public bool HasStatuses
    {
        get => _hasStatuses;
        private set { if (SetProperty(ref _hasStatuses, value)) OnPropertyChanged(nameof(NoStatuses)); }
    }

    public bool NoStatuses => !_hasStatuses;

    public string AppVersion { get; }

    public string SettingsFilePath => AppPaths.SettingsFile;

    public string LogsFolder => Path.Combine(AppPaths.LocalDataDir, "logs");

    public string RuntimeDescription => $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {Environment.OSVersion.VersionString}";

    // ───────────────────────────── Actions ─────────────────────────────

    /// <summary>Assigns a hotkey (already validated/normalized, or "" to clear) and saves it.</summary>
    public void SetHotkey(HotkeyEntry entry, string value)
    {
        entry.Value = value;
        entry.Error = null;
        var id = entry.Id;
        _pending["Hotkey." + id] = s => AssignHotkey(s.Hotkeys, id, value);
        Flush();
    }

    /// <summary>Restores default settings (keeps nothing).</summary>
    public void ResetToDefaults()
    {
        _pending.Clear();
        _debounce.Stop();
        _settings.ResetToDefaults();
    }

    public void RefreshMonitors(string? selectedDeviceName = null)
    {
        var options = new List<SettingsOption> { new("Основной монитор", "") };
        try
        {
            var screens = Forms.Screen.AllScreens;
            for (var i = 0; i < screens.Length; i++)
            {
                var sc = screens[i];
                var label = $"Монитор {i + 1} ({sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? ", основной" : "")})";
                options.Add(new SettingsOption(label, sc.DeviceName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Monitor enumeration failed", ex);
        }

        var selected = selectedDeviceName ?? _monitorDeviceName;
        if (!string.IsNullOrEmpty(selected) && !options.Any(o => string.Equals((string)o.Value, selected, StringComparison.Ordinal)))
            options.Add(new SettingsOption($"Отключённый монитор ({selected})", selected));

        MonitorOptions = options;
    }

    public void RefreshAudioDevices(string? selectedId = null)
    {
        var options = new List<SettingsOption> { new("Автоматически — где играет звук", "") };
        foreach (var device in WindowsDynamicIsland.Audio.AudioAnalyzer.GetRenderDevices())
            options.Add(new SettingsOption(device.Value, device.Key));

        var selected = selectedId ?? _visualizerDeviceId;
        if (!string.IsNullOrEmpty(selected) && !options.Any(o => string.Equals((string)o.Value, selected, StringComparison.Ordinal)))
            options.Add(new SettingsOption("Отключённое устройство", selected));

        AudioDeviceOptions = options;
    }

    /// <summary>Writes all pending edits in one settings update.</summary>
    public void Flush()
    {
        _debounce.Stop();
        if (_pending.Count == 0) return;
        var actions = _pending.Values.ToList();
        _pending.Clear();
        try
        {
            _settings.Update(s =>
            {
                foreach (var apply in actions) apply(s);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Applying settings failed", ex);
        }
    }

    // ───────────────────────────── Internals ─────────────────────────────

    /// <summary>
    /// Property write helper. User edits mark the property as pending and are flushed (now or debounced);
    /// values coming from <see cref="Load"/> never overwrite a pending local edit and never write back.
    /// </summary>
    private void Set<T>(ref T field, T value, Action<AppSettings, T> apply, bool debounce = false,
        [CallerMemberName] string name = "")
    {
        if (_loading)
        {
            if (_pending.ContainsKey(name)) return;
            SetProperty(ref field, value, name);
            return;
        }

        if (!SetProperty(ref field, value, name)) return;
        _pending[name] = s => apply(s, value);
        if (debounce)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        else
        {
            Flush();
        }
    }

    private void Load(AppSettings s)
    {
        _loading = true;
        try
        {
            var a = s.Appearance;
            CompactWidth = a.CompactWidth;
            CompactHeight = a.CompactHeight;
            CornerRadius = a.CornerRadius;
            ExpandedCornerRadius = a.ExpandedCornerRadius;
            CapsuleOpacity = a.Opacity;
            ColorScheme = a.ColorScheme;
            AnimationSpeed = a.AnimationSpeed;
            HorizontalPosition = a.HorizontalPosition;
            TopOffset = a.TopOffset;
            HorizontalOffset = a.HorizontalOffset;
            ShowWhenIdle = a.ShowWhenIdle;
            ExpandOnHover = a.ExpandOnHover;
            PulseToBeat = a.PulseToBeat;
            CompactWave = a.CompactWave;
            ExpandedWave = a.ExpandedWave;
            WaveArtworkColors = a.WaveArtworkColors;
            TopBarMode = a.TopBarMode;
            TopBarFill = a.TopBarFill;
            TopBarHeight = a.TopBarHeight;

            var f = s.Features;
            NotificationsEnabled = f.NotificationsEnabled;
            NotificationDuration = f.NotificationDurationSeconds;
            MusicMode = f.MusicModeEnabled;
            VideoMode = f.VideoModeEnabled;
            LyricsEnabled = f.LyricsEnabled;
            VisualizerEnabled = f.VisualizerEnabled;
            VisualizerStyle = f.VisualizerStyle;
            VisualizerDeviceId = f.VisualizerDeviceId;
            VisualizerSensitivity = f.VisualizerSensitivity;
            DownloadsEnabled = f.DownloadsEnabled;
            ClipboardHistoryEnabled = f.ClipboardHistoryEnabled;
            ClipboardIndicator = f.ClipboardIndicator;
            ClipboardMaxItems = f.ClipboardMaxItems;
            DownloadsFolder = f.DownloadsFolder;

            var i = s.Indicators;
            IndicatorVolume = i.Volume;
            IndicatorBrightness = i.Brightness;
            IndicatorBattery = i.Battery;
            IndicatorCharger = i.Charger;
            IndicatorUsb = i.Usb;
            IndicatorBluetooth = i.Bluetooth;
            IndicatorMicrophone = i.Microphone;
            HudDuration = i.DurationSeconds;
            LowBatteryThreshold = i.LowBatteryThreshold;

            var t = s.Timer;
            DefaultCountdownMinutes = t.DefaultCountdownMinutes;
            PresetsText = FormatPresets(t.Presets);
            Presets = t.Presets.ToArray();
            TimerSound = t.PlaySoundOnFinish;
            TimerNotify = t.NotifyOnFinish;

            var sys = s.System;
            LaunchAtStartup = sys.LaunchAtStartup;
            StartInBackground = sys.StartInBackground;
            if (!MonitorOptions.Any(o => string.Equals((string)o.Value, sys.MonitorDeviceName, StringComparison.Ordinal)))
                RefreshMonitors(sys.MonitorDeviceName);
            MonitorDeviceName = sys.MonitorDeviceName;
            HideInFullscreen = sys.HideInFullscreen;
            ShowTrayIcon = sys.ShowTrayIcon;

            var h = s.Hotkeys;
            HotkeysEnabled = h.Enabled;
            foreach (var entry in Hotkeys)
            {
                if (entry.IsCapturing || _pending.ContainsKey("Hotkey." + entry.Id)) continue;
                entry.Value = ReadHotkey(h, entry.Id);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateTimerState(TimerStateChanged? e)
    {
        if (e is null || e.State == TimerRunState.Idle)
        {
            TimerStatusText = "Таймер не запущен";
            IsTimerActive = false;
            IsCountdownActive = false;
            PauseButtonText = "Пауза";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        IsTimerActive = e.IsActive;
        IsCountdownActive = e.Kind == TimerKind.Countdown && e.IsActive;
        PauseButtonText = e.State == TimerRunState.Paused ? "Продолжить" : "Пауза";

        if (e.State == TimerRunState.Finished)
        {
            TimerStatusText = "Таймер завершён";
            return;
        }

        string text;
        if (e.Kind == TimerKind.Countdown)
        {
            var remaining = e.GetRemaining(now) ?? TimeSpan.Zero;
            // Countdown digits use ceil so "00:01" is shown during the last second.
            var shown = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds) - 0.001));
            text = $"Обратный отсчёт: осталось {FormatClock(shown)}";
        }
        else
        {
            var elapsed = e.GetElapsed(now);
            text = $"Секундомер: {FormatClock(TimeSpan.FromSeconds(Math.Floor(elapsed.TotalSeconds)))}";
        }
        if (e.State == TimerRunState.Paused) text += " (пауза)";
        TimerStatusText = text;
    }

    private void OnStatusesChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshStatuses));
    }

    private void RefreshStatuses()
    {
        if (_disposed) return;
        Statuses.Clear();
        foreach (var s in _statuses.Snapshot())
            Statuses.Add(new ModuleStatusRow(s.ModuleName, s.IsWorking, s.Message));
        HasStatuses = Statuses.Count > 0;
    }

    internal static string FormatClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    internal static List<double> ParsePresets(string text)
    {
        var result = new List<double>();
        foreach (var token in (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
                double.IsFinite(v) && v > 0)
                result.Add(v);
        }
        return result;
    }

    internal static string FormatPresets(IEnumerable<double> presets)
        => string.Join(", ", presets.Select(p => p.ToString("0.##", CultureInfo.InvariantCulture)));

    private static string ReadHotkey(HotkeySettings h, string id) => id switch
    {
        nameof(HotkeySettings.ToggleExpand) => h.ToggleExpand,
        nameof(HotkeySettings.ToggleVisibility) => h.ToggleVisibility,
        nameof(HotkeySettings.MediaPlayPause) => h.MediaPlayPause,
        nameof(HotkeySettings.StartStopwatch) => h.StartStopwatch,
        nameof(HotkeySettings.OpenSettings) => h.OpenSettings,
        nameof(HotkeySettings.ClipboardHistory) => h.ClipboardHistory,
        _ => "",
    };

    private static void AssignHotkey(HotkeySettings h, string id, string value)
    {
        switch (id)
        {
            case nameof(HotkeySettings.ToggleExpand): h.ToggleExpand = value; break;
            case nameof(HotkeySettings.ToggleVisibility): h.ToggleVisibility = value; break;
            case nameof(HotkeySettings.MediaPlayPause): h.MediaPlayPause = value; break;
            case nameof(HotkeySettings.StartStopwatch): h.StartStopwatch = value; break;
            case nameof(HotkeySettings.OpenSettings): h.OpenSettings = value; break;
            case nameof(HotkeySettings.ClipboardHistory): h.ClipboardHistory = value; break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _debounce.Stop();
        _statuses.Changed -= OnStatusesChanged;
        _subs.Dispose();
    }
}
