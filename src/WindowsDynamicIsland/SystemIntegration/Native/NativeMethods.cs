using System.Runtime.InteropServices;
using System.Text;

namespace WindowsDynamicIsland.SystemIntegration.Native;

/// <summary>
/// Win32 P/Invoke declarations used by the system-integration and download modules.
/// Classic <see cref="DllImportAttribute"/> with explicit Unicode entry points; structures use sequential
/// layout whose sizes are taken with <see cref="Marshal.SizeOf{T}()"/> at runtime.
/// </summary>
internal static class NativeMethods
{
    // ───────────────────────────── Device notifications (user32) ─────────────────────────────

    /// <summary>WM_DEVICECHANGE window message.</summary>
    public const int WM_DEVICECHANGE = 0x0219;
    /// <summary>A device or piece of media has been inserted and is now available.</summary>
    public const int DBT_DEVICEARRIVAL = 0x8000;
    /// <summary>A device or piece of media has been removed.</summary>
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    /// <summary>Class of devices (device interface) broadcast type.</summary>
    public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    /// <summary>The recipient handle passed to RegisterDeviceNotification is a window handle.</summary>
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    /// <summary>Parent handle that turns a window into a message-only window.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>
    /// DEV_BROADCAST_DEVICEINTERFACE_W. The real structure ends with a variable-length, NUL-terminated
    /// <c>dbcc_name</c>; here it is declared as one char so the structure can be used as a registration filter
    /// and so <see cref="Marshal.OffsetOf{T}(string)"/> yields the offset of the name in received messages.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    /// <summary>Byte offset of <c>dbcc_name</c> inside DEV_BROADCAST_DEVICEINTERFACE_W.</summary>
    public static readonly int DeviceInterfaceNameOffset =
        Marshal.OffsetOf<DEV_BROADCAST_DEVICEINTERFACE>(nameof(DEV_BROADCAST_DEVICEINTERFACE.dbcc_name)).ToInt32();

    /// <summary>Byte offset of <c>dbch_devicetype</c> inside DEV_BROADCAST_HDR.</summary>
    public const int BroadcastHeaderDeviceTypeOffset = 4;

    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterDeviceNotification(IntPtr handle);

    // ───────────────────────────── Windows / monitors (user32) ─────────────────────────────

    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
    public const int GWL_STYLE = -16;
    public const int WS_CAPTION = 0x00C00000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>MONITORINFOEXW (cbSize must be set to <c>Marshal.SizeOf&lt;MONITORINFOEX&gt;()</c>).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    // ───────────────────────────── Shell (shell32) ─────────────────────────────

    /// <summary>QUERY_USER_NOTIFICATION_STATE values.</summary>
    public const int QUNS_NOT_PRESENT = 1;
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    /// <summary>FOLDERID_Downloads.</summary>
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    /// <summary>Resolves a known folder path; returns null on failure.</summary>
    public static string? TryGetKnownFolderPath(Guid folderId)
    {
        var ptr = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(ptr);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) CoTaskMemFree(ptr);
        }
    }

    // ───────────────────────────── Power (kernel32) ─────────────────────────────

    /// <summary>SYSTEM_POWER_STATUS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        /// <summary>0 = offline, 1 = online, 255 = unknown.</summary>
        public byte ACLineStatus;
        /// <summary>Bit flags; 128 = no system battery, 255 = unknown.</summary>
        public byte BatteryFlag;
        /// <summary>0..100, 255 = unknown.</summary>
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public const byte BATTERY_FLAG_NO_BATTERY = 128;
    public const byte BATTERY_FLAG_UNKNOWN = 255;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
