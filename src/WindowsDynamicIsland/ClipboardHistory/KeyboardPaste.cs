using System.Runtime.InteropServices;

namespace WindowsDynamicIsland.ClipboardHistory;

/// <summary>Sends Ctrl+V to whatever window has the keyboard focus (the island itself never takes focus).</summary>
public static class KeyboardPaste
{
    private const byte VK_CONTROL = 0x11;
    private const byte VK_SHIFT = 0x10;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void SendCtrlV()
    {
        // A Shift still held from a hotkey would turn this into Ctrl+Shift+V (paste as plain text / other action).
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
