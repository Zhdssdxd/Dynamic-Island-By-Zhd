using Microsoft.Win32;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Settings;

/// <summary>
/// Keeps HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WindowsDynamicIsland in sync with
/// <see cref="SystemSettings.LaunchAtStartup"/>. Per-user key: no administrator rights needed. Never throws.
///
/// The installer's optional "start with Windows" task writes the same value. When the app starts and finds
/// that value while the setting is off, it adopts it (sets LaunchAtStartup = true) so the installer's choice
/// shows up in settings; from then on the setting is the source of truth.
/// </summary>
public sealed class AutostartManager : IIslandModule
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "WindowsDynamicIsland";
    public const string AutostartArgument = "--autostart";

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly object _gate = new();
    private IDisposable? _settingsSub;
    private bool? _applied;
    private bool _disposed;

    public AutostartManager(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public string Name => "Автозапуск";

    /// <summary>Command line written to the Run key: "&lt;exe path&gt;" --autostart. Null when the path is unknown.</summary>
    public static string? ExpectedCommand
    {
        get
        {
            var exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\" {AutostartArgument}";
        }
    }

    /// <summary>True when the Run value exists (whatever path it points to). Never throws.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart registry read failed", ex);
            return false;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Current.System.LaunchAtStartup && IsRegistered())
            {
                Log.Info("Autostart value found in registry (installer task) — enabling LaunchAtStartup");
                _settings.Update(s => s.System.LaunchAtStartup = true);
            }

            Sync(_settings.Current.System.LaunchAtStartup, force: true);
            _settingsSub = _bus.Subscribe<SettingsChanged>(e => Sync(e.Settings.System.LaunchAtStartup, force: false));
        }
        catch (Exception ex)
        {
            Log.Error("Autostart manager failed to start", ex);
            _bus.Publish(new ModuleStatusChanged(Name, false, "Ошибка автозапуска: " + ex.Message));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settingsSub?.Dispose();
        _settingsSub = null;
    }

    private void Sync(bool enable, bool force)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!force && _applied == enable) return;
            _applied = enable;

            var (ok, message) = enable ? TryRegister() : TryUnregister();
            _bus.Publish(new ModuleStatusChanged(Name, ok, message));
        }
    }

    private static (bool Ok, string? Message) TryRegister()
    {
        var command = ExpectedCommand;
        if (command is null)
            return (false, "Не удалось определить путь к программе");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return (false, "Нет доступа к разделу автозапуска в реестре");

            if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
                Log.Info("Autostart enabled: " + command);
            }
            return (true, "Запуск вместе с Windows включён");
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart registration failed", ex);
            return (false, "Не удалось включить автозапуск: " + ex.Message);
        }
    }

    private static (bool Ok, string? Message) TryUnregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is not null && key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Autostart disabled");
            }
            return (true, "Автозапуск выключен");
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart removal failed", ex);
            return (false, "Не удалось выключить автозапуск: " + ex.Message);
        }
    }
}
