using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Settings;
using Forms = System.Windows.Forms;

namespace WindowsDynamicIsland.UI.Native;

/// <summary>Physical-pixel placement of the island host window on its monitor.</summary>
internal readonly record struct PlacementResult(int X, int Y, int Width, int Height, double Scale, string DeviceName);

/// <summary>
/// Computes where the fixed-size host window goes (PerMonitorV2: everything in physical pixels).
/// The capsule itself is laid out inside the host by <see cref="IslandWindow"/>.
/// </summary>
internal static class WindowPlacement
{
    /// <param name="hostWidthDip">Host window width in DIPs.</param>
    /// <param name="hostHeightDip">Host window height in DIPs.</param>
    /// <param name="windowTopOffsetDip">Gap between the monitor top edge and the window top (DIPs, ≥ 0).</param>
    /// <param name="overTopBar">Top-bar mode: start at the monitor's top edge even when a bar is docked there.</param>
    public static PlacementResult Compute(AppSettings settings, double hostWidthDip, double hostHeightDip, double windowTopOffsetDip,
        bool overTopBar = false)
    {
        var screen = FindScreen(settings.System.MonitorDeviceName);
        int left, top, right, width;
        string device;
        if (screen is null)
        {
            // No WinForms screen info (should not happen) — fall back to the primary work area in DIPs ≈ pixels.
            left = 0;
            top = 0;
            width = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
            right = width;
            device = "";
        }
        else
        {
            var bounds = screen.Bounds;
            var work = screen.WorkingArea;
            left = bounds.Left;
            right = bounds.Right;
            width = bounds.Width;
            // Taskbar docked on top → start below it (unless the island is meant to sit inside that bar).
            top = !overTopBar && work.Top > bounds.Top ? work.Top : bounds.Top;
            device = screen.DeviceName;
        }

        var scale = GetScale(left + width / 2, top + 1);
        var w = (int)Math.Round(hostWidthDip * scale);
        var h = (int)Math.Round(hostHeightDip * scale);
        var a = settings.Appearance;
        var offsetX = (int)Math.Round(a.HorizontalOffset * scale);

        int x = a.HorizontalPosition switch
        {
            IslandHorizontalPosition.Left => left + offsetX,
            IslandHorizontalPosition.Right => right - w + offsetX,
            _ => left + (width - w) / 2 + offsetX,
        };
        var y = top + (int)Math.Round(Math.Max(0, windowTopOffsetDip) * scale);
        return new PlacementResult(x, y, w, h, scale, device);
    }

    /// <summary>
    /// Height (DIPs) of whatever is docked at the top of the island's monitor — the gap between the monitor's top edge
    /// and its work area (taskbar on top, AppBar-style top bars). 0 when nothing reserves space there.
    /// </summary>
    public static double GetTopBarHeightDip(AppSettings settings)
    {
        var screen = FindScreen(settings.System.MonitorDeviceName);
        if (screen is null) return 0;
        var bounds = screen.Bounds;
        var gap = screen.WorkingArea.Top - bounds.Top;
        if (gap <= 0) return 0;
        var scale = GetScale(bounds.Left + bounds.Width / 2, bounds.Top + 1);
        return gap / (scale > 0 ? scale : 1.0);
    }

    private static Forms.Screen? FindScreen(string deviceName)
    {
        try
        {
            var all = Forms.Screen.AllScreens;
            if (!string.IsNullOrEmpty(deviceName))
            {
                var match = all.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            return Forms.Screen.PrimaryScreen ?? (all.Length > 0 ? all[0] : null);
        }
        catch (Exception ex)
        {
            Log.Warn("Screen enumeration failed", ex);
            return null;
        }
    }

    /// <summary>Effective DPI scale (1.0 = 96 DPI) of the monitor containing the given physical point.</summary>
    public static double GetScale(int x, int y)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(x, y), NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero &&
                NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("GetDpiForMonitor failed", ex);
        }
        return 1.0;
    }
}
