using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Settings;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window (HWND_MESSAGE) owned by the UI thread.
/// Bindings come from <see cref="HotkeySettings"/> and are re-registered whenever they change.
/// A combination already taken by another application is reported in diagnostics; the others keep working.
/// Registered in <see cref="ServiceRegistry"/> so the settings window can suspend hotkeys while capturing a new one.
/// </summary>
public sealed class HotkeyManager : IIslandModule
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private enum HotkeyAction
    {
        ToggleExpand = 1,
        ToggleVisibility = 2,
        MediaPlayPause = 3,
        StartStopwatch = 4,
        OpenSettings = 5,
    }

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly List<int> _registeredIds = new();
    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private IDisposable? _settingsSub;
    private string? _appliedSignature;
    private int _suspendCount;
    private bool _stopped;
    private bool _disposed;

    public HotkeyManager(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public string Name => "Горячие клавиши";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            _bus.Publish(new ModuleStatusChanged(Name, false, "Нет UI-потока для регистрации горячих клавиш"));
            return;
        }
        _dispatcher = dispatcher;

        try
        {
            if (dispatcher.CheckAccess()) Initialize();
            else await dispatcher.InvokeAsync(Initialize).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey manager failed to start", ex);
            _bus.Publish(new ModuleStatusChanged(Name, false, "Не удалось создать окно для горячих клавиш: " + ex.Message));
        }
    }

    /// <summary>UI thread: creates the message-only window, registers hotkeys, subscribes to settings.</summary>
    private void Initialize()
    {
        if (_disposed || _stopped) return;

        var parameters = new HwndSourceParameters("DynamicIslandHotkeys")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        ServiceRegistry.Register(this);
        Apply(_settings.Current.Hotkeys, force: true);

        // SettingsManager.Update may run on any thread; marshal so registration stays on the window's thread.
        _settingsSub = _bus.Subscribe<SettingsChanged>(e => Apply(e.Settings.Hotkeys, force: false), onUiThread: true);
    }

    public Task StopAsync()
    {
        _stopped = true;
        RunOnUi(Teardown);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopped = true;
        RunOnUi(Teardown);
    }

    /// <summary>
    /// Temporarily unregisters all hotkeys (e.g. while the user records a new combination in settings,
    /// so pressing an existing combo reaches the text box instead of firing). Calls nest; UI thread only.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (_dispatcher is null || !_dispatcher.CheckAccess()) return;
        if (suspended)
        {
            if (_suspendCount++ == 0) UnregisterAll();
        }
        else if (_suspendCount > 0)
        {
            if (--_suspendCount == 0) Apply(_settings.Current.Hotkeys, force: true);
        }
    }

    // ───────────────────────────── Registration ─────────────────────────────

    private void Apply(HotkeySettings hotkeys, bool force)
    {
        if (_source is null || _stopped) return;

        var signature = string.Join("|", hotkeys.Enabled, hotkeys.ToggleExpand, hotkeys.ToggleVisibility,
            hotkeys.MediaPlayPause, hotkeys.StartStopwatch, hotkeys.OpenSettings);
        if (!force && signature == _appliedSignature) return;
        _appliedSignature = signature;

        if (_suspendCount > 0) return;   // re-applied when capture ends
        UnregisterAll();

        if (!hotkeys.Enabled)
        {
            _bus.Publish(new ModuleStatusChanged(Name, true, "Горячие клавиши отключены"));
            return;
        }

        var bindings = new (HotkeyAction Action, string Text)[]
        {
            (HotkeyAction.ToggleExpand, hotkeys.ToggleExpand),
            (HotkeyAction.ToggleVisibility, hotkeys.ToggleVisibility),
            (HotkeyAction.MediaPlayPause, hotkeys.MediaPlayPause),
            (HotkeyAction.StartStopwatch, hotkeys.StartStopwatch),
            (HotkeyAction.OpenSettings, hotkeys.OpenSettings),
        };

        var problems = new List<string>();
        var used = new HashSet<(HotkeyModifiers, uint)>();
        var registered = 0;

        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (!HotkeyParser.TryParse(text, out Hotkey hotkey))
            {
                problems.Add($"Не удалось распознать сочетание «{text}»");
                continue;
            }

            var display = hotkey.ToString();
            var vk = hotkey.VirtualKey;
            if (vk == 0)
            {
                problems.Add($"Клавиша в сочетании {display} не поддерживается");
                continue;
            }
            if (!used.Add((hotkey.Modifiers, vk)))
            {
                problems.Add($"Сочетание {display} назначено нескольким действиям");
                continue;
            }

            var id = (int)action;
            if (RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | MOD_NOREPEAT, vk))
            {
                _registeredIds.Add(id);
                registered++;
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                Log.Warn($"RegisterHotKey({display}) failed, Win32 error {error}");
                problems.Add(error == ERROR_HOTKEY_ALREADY_REGISTERED || error == 0
                    ? $"Сочетание {display} занято другим приложением"
                    : $"Не удалось зарегистрировать {display} (ошибка {error})");
            }
        }

        if (problems.Count > 0)
            _bus.Publish(new ModuleStatusChanged(Name, false, string.Join("; ", problems)));
        else
            _bus.Publish(new ModuleStatusChanged(Name, true, registered > 0 ? $"Активно сочетаний: {registered}" : "Сочетания не назначены"));
    }

    private void UnregisterAll()
    {
        if (_source is null) return;
        var handle = _source.Handle;
        foreach (var id in _registeredIds)
        {
            try { UnregisterHotKey(handle, id); }
            catch (Exception ex) { Log.Warn("UnregisterHotKey failed", ex); }
        }
        _registeredIds.Clear();
    }

    private void Teardown()
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        try
        {
            UnregisterAll();
            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Hotkey manager teardown failed", ex);
        }
    }

    private void RunOnUi(Action action)
    {
        var d = _dispatcher;
        if (d is null)
        {
            _settingsSub?.Dispose();
            return;
        }
        try
        {
            if (d.CheckAccess()) action();
            else if (!d.HasShutdownStarted) d.InvokeAsync(action);
        }
        catch (Exception ex)
        {
            Log.Warn("Hotkey manager UI call failed", ex);
        }
    }

    // ───────────────────────────── Messages ─────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        try
        {
            OnHotkey((HotkeyAction)wParam.ToInt32());
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey action failed", ex);
        }
        return IntPtr.Zero;
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleExpand:
                _bus.Publish(new IslandActionRequested(IslandUserAction.ToggleExpanded));
                break;
            case HotkeyAction.ToggleVisibility:
                _bus.Publish(new IslandActionRequested(IslandUserAction.ToggleVisibility));
                break;
            case HotkeyAction.MediaPlayPause:
                _bus.Publish(new MediaCommand(MediaCommandKind.TogglePlayPause));
                break;
            case HotkeyAction.StartStopwatch:
                var timerActive = _bus.GetLast<TimerStateChanged>()?.IsActive == true;
                _bus.Publish(new TimerCommand(timerActive ? TimerCommandKind.TogglePause : TimerCommandKind.StartStopwatch));
                break;
            case HotkeyAction.OpenSettings:
                _bus.Publish(new OpenSettingsRequested());
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
