namespace WindowsDynamicIsland.Core.Models;

/// <summary>
/// Icon glyphs shared by Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10).
/// Use with FontFamily "Segoe Fluent Icons, Segoe MDL2 Assets" (resource key <c>IconFont</c>).
/// Only code points present in both fonts are listed here.
/// </summary>
public static class Glyphs
{
    public const string Play = "";
    public const string Pause = "";
    public const string Next = "";
    public const string Previous = "";
    public const string Stop = "";

    public const string Volume = "";
    public const string Volume0 = "";
    public const string Volume1 = "";
    public const string Volume2 = "";
    public const string Volume3 = "";
    public const string Mute = "";

    public const string Brightness = "";
    public const string Bluetooth = "";
    public const string Usb = "";
    public const string Microphone = "";
    public const string MicOff = "";
    public const string Lightning = "";
    public const string Battery0 = "";
    public const string Battery10 = "";

    public const string Music = "";
    public const string Video = "";
    public const string Download = "";
    public const string Stopwatch = "";
    public const string Clock = "";
    public const string Notification = "";
    public const string Message = "";
    public const string Settings = "";
    public const string Close = "";
    public const string Check = "";
    public const string Add = "";
    public const string Warning = "";
    public const string Error = "";

    /// <summary>Battery glyph for a 0..1 level (Battery0..Battery9 = E850..E859, Battery10 = E83F).</summary>
    public static string BatteryForLevel(double level)
    {
        var step = (int)Math.Round(Math.Clamp(level, 0, 1) * 10);
        return step >= 10 ? Battery10 : char.ConvertFromUtf32(0xE850 + step);
    }

    /// <summary>Speaker glyph for a 0..1 level.</summary>
    public static string VolumeForLevel(double level, bool muted)
    {
        if (muted || level <= 0.001) return Mute;
        if (level < 0.34) return Volume1;
        if (level < 0.67) return Volume2;
        return Volume3;
    }
}
