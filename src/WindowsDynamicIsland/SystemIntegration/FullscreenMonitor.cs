using System.Runtime.InteropServices;
using System.Text;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.SystemIntegration.Native;
using Forms = System.Windows.Forms;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Detects a full-screen app, game or presentation on the island's monitor and publishes
/// <see cref="FullscreenStateChanged"/> on change only. Polls every 1.5 s while
/// <c>System.HideInFullscreen</c> is on (publishing <c>false</c> once when it is turned off).
///
/// A state is "full screen" when the foreground window is on the island's monitor
/// (<c>System.MonitorDeviceName</c>, empty = primary) and either
///  * <c>SHQueryUserNotificationState</c> reports busy / exclusive D3D full screen, or
///  * its window rectangle covers the whole monitor and it is not an ordinary maximized captioned window
///    (that case happens with an auto-hidden taskbar);
/// or when Windows is in presentation mode. The desktop (Progman/WorkerW), the taskbars and our own
/// windows are ignored — focusing the island keeps the previous state.
/// The process is Per-Monitor-V2 DPI aware, so window and monitor rectangles are both in physical pixels.
/// </summary>
public sealed class FullscreenMonitor : IIslandModule
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly StringBuilder _classBuffer = new(256);
    private System.Threading.Timer? _timer;
    private string _targetMonitor = "";
    private bool _running;
    private bool _lastFullscreen;
    private int _polling;

    public FullscreenMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.System.HideInFullscreen,
            s => s.System.MonitorDeviceName ?? "",
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Полноэкранный режим";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        lock (_gate)
        {
            _running = true;
            _targetMonitor = settings.System.MonitorDeviceName ?? "";
            _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
        return Task.FromResult(FeatureStatus.Ok());
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            string target;
            lock (_gate)
            {
                if (!_running) return;
                target = _targetMonitor;
            }

            var result = Detect(target);
            if (result is null) return; // our own window is in front: keep the previous state

            lock (_gate)
            {
                if (!_running || result.Value.IsFullscreen == _lastFullscreen) return;
                _lastFullscreen = result.Value.IsFullscreen;
                // Published under the lock so a concurrent Deactivate cannot interleave a stale "true".
                _bus.Publish(new FullscreenStateChanged(result.Value.IsFullscreen, result.Value.MonitorDeviceName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Fullscreen: poll failed", ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>Returns the current state, or null when the decision should be skipped (our own window focused).</summary>
    private (bool IsFullscreen, string? MonitorDeviceName)? Detect(string targetMonitor)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (false, null);

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownProcessId) return null;

        _classBuffer.Clear();
        if (NativeMethods.GetClassName(hwnd, _classBuffer, _classBuffer.Capacity) > 0 &&
            ShellClasses.Contains(_classBuffer.ToString()))
        {
            return (false, null);
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero) return (false, null);

        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = "" };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return (false, null);

        var onTarget = IsTargetMonitor(info, targetMonitor);

        var quns = 0;
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0) quns = state;
        }
        catch (Exception)
        {
            // shell32 export missing — rectangle check only.
        }

        if (quns == NativeMethods.QUNS_PRESENTATION_MODE) return (true, info.szDevice);
        if (!onTarget) return (false, info.szDevice);

        if (quns is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN) return (true, info.szDevice);

        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return (false, info.szDevice);
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return (false, info.szDevice);

        var m = info.rcMonitor;
        var covers = rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
        if (!covers) return (false, info.szDevice);

        // A maximized captioned window overhangs the monitor by its borders when the taskbar auto-hides.
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        var captioned = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
        if (NativeMethods.IsZoomed(hwnd) && captioned) return (false, info.szDevice);

        return (true, info.szDevice);
    }

    private static bool IsTargetMonitor(NativeMethods.MONITORINFOEX info, string targetMonitor)
    {
        var isPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
        if (string.IsNullOrEmpty(targetMonitor)) return isPrimary;
        if (string.Equals(info.szDevice, targetMonitor, StringComparison.OrdinalIgnoreCase)) return true;

        // The configured monitor is gone: the island falls back to the primary one.
        try
        {
            var exists = Forms.Screen.AllScreens.Any(s => string.Equals(s.DeviceName, targetMonitor, StringComparison.OrdinalIgnoreCase));
            return !exists && isPrimary;
        }
        catch (Exception)
        {
            return isPrimary;
        }
    }

    private void Deactivate()
    {
        System.Threading.Timer? timer;
        lock (_gate)
        {
            _running = false;
            timer = _timer;
            _timer = null;
            if (_lastFullscreen)
            {
                _lastFullscreen = false;
                _bus.Publish(new FullscreenStateChanged(false, null));
            }
        }
        timer?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
