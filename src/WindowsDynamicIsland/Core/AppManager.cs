using System.Windows;
using WindowsDynamicIsland.Animation;
using WindowsDynamicIsland.Audio;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Downloads;
using WindowsDynamicIsland.Media;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.SystemIntegration;
using WindowsDynamicIsland.Timing;
using WindowsDynamicIsland.UI;
using WindowsDynamicIsland.UI.Shell;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// Composition root. Creates the bus, settings, controller, modules and windows, wires them together
/// and tears everything down in the right order on exit.
/// </summary>
public sealed class AppManager : IAsyncDisposable
{
    private readonly App _app;
    private readonly SubscriptionBag _subs = new();
    private EventBus? _bus;
    private SettingsManager? _settings;
    private IslandController? _controller;
    private ModuleManager? _modules;
    private ModuleStatusRegistry? _statusRegistry;
    private IslandWindow? _island;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;
    private bool _disposed;

    public AppManager(App app)
    {
        _app = app;
    }

    public async Task StartAsync(string[] args)
    {
        var dispatcher = _app.Dispatcher;
        var firstRun = !File.Exists(AppPaths.SettingsFile);

        _bus = new EventBus(dispatcher);
        _statusRegistry = new ModuleStatusRegistry(_bus);

        _settings = new SettingsManager(_bus);
        _settings.Load();
        if (firstRun) _settings.SaveNow();

        // Animation speed follows settings.
        AnimationEngine.Instance.SpeedMultiplier = _settings.Current.Appearance.AnimationSpeed;
        _subs.Add(_bus.Subscribe<SettingsChanged>(e =>
            AnimationEngine.Instance.SpeedMultiplier = e.Settings.Appearance.AnimationSpeed, onUiThread: true));

        _controller = new IslandController(_bus, dispatcher);
        _controller.Start(_settings.Current);

        // ── Modules. Order = start order; stop happens in reverse.
        _modules = new ModuleManager(_bus);
        var audio = new AudioAnalyzer(_bus, _settings);
        ServiceRegistry.Register<ISpectrumSource>(audio);

        _modules.Register(new MediaManager(_bus, _settings));
        _modules.Register(new LyricsProvider(_bus, _settings));
        _modules.Register(audio);
        _modules.Register(new NotificationManager(_bus, _settings));
        _modules.Register(new VolumeMonitor(_bus, _settings));
        _modules.Register(new BrightnessMonitor(_bus, _settings));
        _modules.Register(new DeviceMonitor(_bus, _settings));
        _modules.Register(new BatteryMonitor(_bus, _settings));
        _modules.Register(new MicrophoneMonitor(_bus, _settings));
        _modules.Register(new FullscreenMonitor(_bus, _settings));
        _modules.Register(new DownloadMonitor(_bus, _settings));
        _modules.Register(new TimerManager(_bus, _settings));
        _modules.Register(new HotkeyManager(_bus, _settings));
        _modules.Register(new AutostartManager(_bus, _settings));

        // ── Shell
        _subs.Add(_bus.Subscribe<OpenSettingsRequested>(_ => ShowSettings(), onUiThread: true));
        _subs.Add(_bus.Subscribe<ExitRequested>(e => _ = _app.ShutdownAppAsync(), onUiThread: true));

        _island = new IslandWindow(_controller, _settings);
        _island.Show();

        _tray = new TrayIcon(_bus, _settings);

        await _modules.StartAllAsync();

        if (firstRun)
        {
            _bus.Publish(new NotificationReceived(new IslandNotification
            {
                Id = "welcome",
                AppName = "Dynamic Island",
                Glyph = Glyphs.Check,
                Title = "Dynamic Island запущен",
                Body = "Нажмите на капсулу, чтобы развернуть. Правый клик — меню и настройки.",
                Source = NotificationSource.App,
                DisplayDuration = TimeSpan.FromSeconds(7),
            }));
        }

        if (!_settings.Current.System.StartInBackground || args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            ShowSettings();
    }

    /// <summary>Opens (or focuses) the single settings window.</summary>
    public void ShowSettings()
    {
        if (_disposed || _settings is null || _bus is null || _statusRegistry is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            BringToFront(_settingsWindow);
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _bus, _statusRegistry);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        BringToFront(_settingsWindow);
    }

    /// <summary>
    /// Windows' focus-steal protection ignores Activate() when another process owns the foreground
    /// (e.g. the settings were opened from a hotkey or a second launch). A brief Topmost toggle raises the window anyway.
    /// </summary>
    private static void BringToFront(Window window)
    {
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
        window.Focus();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _subs.Dispose();

        try { _settingsWindow?.Close(); } catch (Exception ex) { Log.Warn("Settings window close failed", ex); }
        _tray?.Dispose();

        if (_modules is not null)
        {
            await _modules.StopAllAsync(TimeSpan.FromMilliseconds(1500));
            _modules.Dispose();
        }

        _controller?.Dispose();
        try { _island?.Close(); } catch (Exception ex) { Log.Warn("Island window close failed", ex); }

        _statusRegistry?.Dispose();
        _settings?.Dispose();
        ServiceRegistry.Clear();
    }
}
