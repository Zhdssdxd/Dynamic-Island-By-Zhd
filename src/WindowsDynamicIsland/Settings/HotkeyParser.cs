using System.Globalization;
using System.Windows.Input;

namespace WindowsDynamicIsland.Settings;

/// <summary>Win32 hotkey modifier flags (values match RegisterHotKey's MOD_* constants).</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>A parsed global hotkey.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, Key Key)
{
    /// <summary>Win32 virtual-key code for <see cref="Key"/>.</summary>
    public uint VirtualKey => HotkeyParser.ToVirtualKey(Key);

    public override string ToString() => HotkeyParser.Format(Modifiers, Key);
}

/// <summary>
/// Parses and formats hotkey strings of the form "Ctrl+Alt+Shift+Win+Key" (case-insensitive, spaces allowed).
/// Supported keys: A–Z, 0–9, F1–F24, NumPad0–9, arrows, Space, Enter, Tab, Esc, Backspace, Insert, Delete,
/// Home, End, PageUp, PageDown, PrintScreen, Pause, punctuation (Plus, Minus, Comma, Period, …), media keys,
/// plus any other <see cref="Key"/> enum name.
/// A hotkey without modifiers is accepted only for F1–F24 (a bare letter would hijack normal typing).
/// </summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Control,
        ["Control"] = HotkeyModifiers.Control,
        ["Ctl"] = HotkeyModifiers.Control,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Win"] = HotkeyModifiers.Win,
        ["Windows"] = HotkeyModifiers.Win,
        ["Meta"] = HotkeyModifiers.Win,
        ["Super"] = HotkeyModifiers.Win,
    };

    private static readonly Dictionary<string, Key> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = Key.Space,
        ["Spacebar"] = Key.Space,
        ["Enter"] = Key.Enter,
        ["Return"] = Key.Enter,
        ["Tab"] = Key.Tab,
        ["Esc"] = Key.Escape,
        ["Escape"] = Key.Escape,
        ["Backspace"] = Key.Back,
        ["Back"] = Key.Back,
        ["Ins"] = Key.Insert,
        ["Insert"] = Key.Insert,
        ["Del"] = Key.Delete,
        ["Delete"] = Key.Delete,
        ["Home"] = Key.Home,
        ["End"] = Key.End,
        ["PageUp"] = Key.PageUp,
        ["PgUp"] = Key.PageUp,
        ["PageDown"] = Key.PageDown,
        ["PgDn"] = Key.PageDown,
        ["Up"] = Key.Up,
        ["Down"] = Key.Down,
        ["Left"] = Key.Left,
        ["Right"] = Key.Right,
        ["ArrowUp"] = Key.Up,
        ["ArrowDown"] = Key.Down,
        ["ArrowLeft"] = Key.Left,
        ["ArrowRight"] = Key.Right,
        ["PrintScreen"] = Key.PrintScreen,
        ["PrtSc"] = Key.PrintScreen,
        ["Pause"] = Key.Pause,
        ["Break"] = Key.Pause,
        ["Plus"] = Key.OemPlus,
        ["="] = Key.OemPlus,
        ["Minus"] = Key.OemMinus,
        ["-"] = Key.OemMinus,
        ["Comma"] = Key.OemComma,
        [","] = Key.OemComma,
        ["Period"] = Key.OemPeriod,
        ["."] = Key.OemPeriod,
        ["Semicolon"] = Key.OemSemicolon,
        [";"] = Key.OemSemicolon,
        ["Slash"] = Key.OemQuestion,
        ["/"] = Key.OemQuestion,
        ["Backslash"] = Key.OemPipe,
        ["\\"] = Key.OemPipe,
        ["Tilde"] = Key.OemTilde,
        ["`"] = Key.OemTilde,
        ["Quote"] = Key.OemQuotes,
        ["'"] = Key.OemQuotes,
        ["OpenBracket"] = Key.OemOpenBrackets,
        ["["] = Key.OemOpenBrackets,
        ["CloseBracket"] = Key.OemCloseBrackets,
        ["]"] = Key.OemCloseBrackets,
        ["NumPlus"] = Key.Add,
        ["NumMinus"] = Key.Subtract,
        ["NumMultiply"] = Key.Multiply,
        ["NumDivide"] = Key.Divide,
        ["NumDecimal"] = Key.Decimal,
        ["PlayPause"] = Key.MediaPlayPause,
        ["MediaPlayPause"] = Key.MediaPlayPause,
        ["MediaNext"] = Key.MediaNextTrack,
        ["MediaPrevious"] = Key.MediaPreviousTrack,
        ["MediaStop"] = Key.MediaStop,
        ["VolumeUp"] = Key.VolumeUp,
        ["VolumeDown"] = Key.VolumeDown,
        ["VolumeMute"] = Key.VolumeMute,
    };

    /// <summary>Parses "Ctrl+Alt+I". Returns false for empty/invalid strings.</summary>
    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out Key key)
    {
        modifiers = HotkeyModifiers.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = SplitTokens(text);
        if (tokens.Count == 0) return false;

        var parsedKey = Key.None;
        var mods = HotkeyModifiers.None;
        foreach (var token in tokens)
        {
            if (ModifierTokens.TryGetValue(token, out var mod))
            {
                if ((mods & mod) != 0) return false;          // "Ctrl+Ctrl+X"
                mods |= mod;
                continue;
            }

            if (parsedKey != Key.None) return false;          // two non-modifier keys
            if (!TryParseKey(token, out parsedKey)) return false;
        }

        if (parsedKey == Key.None) return false;              // modifiers only
        if (mods == HotkeyModifiers.None && !IsFunctionKey(parsedKey)) return false;

        modifiers = mods;
        key = parsedKey;
        return true;
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        if (TryParse(text, out var mods, out var key))
        {
            hotkey = new Hotkey(mods, key);
            return true;
        }
        hotkey = default;
        return false;
    }

    /// <summary>Canonical text: modifiers in the order Ctrl, Alt, Shift, Win, then the key. Re-parses to the same value.</summary>
    public static string Format(HotkeyModifiers modifiers, Key key)
    {
        var parts = new List<string>(5);
        if ((modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
        if (key != Key.None) parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>Normalizes user text ("ctrl + alt + i" → "Ctrl+Alt+I"). Returns false when invalid.</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        if (TryParse(text, out var mods, out var key))
        {
            normalized = Format(mods, key);
            return true;
        }
        normalized = "";
        return false;
    }

    /// <summary>Maps WPF keyboard modifiers to hotkey modifiers.</summary>
    public static HotkeyModifiers FromModifierKeys(ModifierKeys modifierKeys)
    {
        var m = HotkeyModifiers.None;
        if ((modifierKeys & ModifierKeys.Control) != 0) m |= HotkeyModifiers.Control;
        if ((modifierKeys & ModifierKeys.Alt) != 0) m |= HotkeyModifiers.Alt;
        if ((modifierKeys & ModifierKeys.Shift) != 0) m |= HotkeyModifiers.Shift;
        if ((modifierKeys & ModifierKeys.Windows) != 0) m |= HotkeyModifiers.Win;
        return m;
    }

    /// <summary>True for keys that are modifiers themselves (cannot be the main key of a hotkey).</summary>
    public static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    public static bool IsFunctionKey(Key key) => key >= Key.F1 && key <= Key.F24;

    /// <summary>Win32 virtual-key code for a WPF key.</summary>
    public static uint ToVirtualKey(Key key) => (uint)KeyInterop.VirtualKeyFromKey(key);

    // ───────────────────────────── helpers ─────────────────────────────

    private static List<string> SplitTokens(string text)
    {
        // '+' is the separator; a trailing "++" means the '+' key itself ("Ctrl++").
        var result = new List<string>();
        var trimmed = text.Trim();
        var plusKey = false;
        if (trimmed.EndsWith("++", StringComparison.Ordinal))
        {
            plusKey = true;
            trimmed = trimmed[..^2];
        }
        else if (trimmed == "+")
        {
            return result;
        }

        foreach (var raw in trimmed.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                if (plusKey && result.Count == 0 && trimmed.Length == 0) continue;
                result.Clear();
                return result;          // empty token like "Ctrl++Alt" or "+X"
            }
            result.Add(token);
        }
        if (plusKey) result.Add("Plus");
        return result;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;

        if (KeyAliases.TryGetValue(token, out var alias))
        {
            key = alias;
            return true;
        }

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = Key.A + (c - 'A');
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                key = Key.D0 + (c - '0');
                return true;
            }
            return false;
        }

        // F1..F24
        if ((token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var fn) &&
            fn is >= 1 and <= 24)
        {
            key = Key.F1 + (fn - 1);
            return true;
        }

        // Num0..Num9 / NumPad0..NumPad9
        foreach (var prefix in new[] { "NumPad", "Numpad", "Num" })
        {
            if (token.Length == prefix.Length + 1 &&
                token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                token[^1] is >= '0' and <= '9')
            {
                key = Key.NumPad0 + (token[^1] - '0');
                return true;
            }
        }

        // Any other Key enum name (not numeric strings, which Enum.TryParse would accept as raw values).
        if (!char.IsLetter(token[0])) return false;
        if (Enum.TryParse<Key>(token, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed) &&
            parsed != Key.None &&
            !IsModifierKey(parsed) &&
            parsed is not (Key.System or Key.ImeProcessed or Key.DeadCharProcessed))
        {
            key = parsed;
            return true;
        }
        return false;
    }

    private static string KeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (key - Key.NumPad0);
        // Several Key members share a value (Return/Enter, Prior/PageUp, …): Enum.ToString would pick
        // an arbitrary alias, so the canonical names are spelled out here (comparisons, not switch cases,
        // because duplicate constant values are not allowed as case labels).
        if (key == Key.Enter) return "Enter";
        if (key == Key.Escape) return "Esc";
        if (key == Key.Back) return "Backspace";
        if (key == Key.PageUp) return "PageUp";
        if (key == Key.PageDown) return "PageDown";
        if (key == Key.PrintScreen) return "PrintScreen";
        if (key == Key.CapsLock) return "CapsLock";
        if (key == Key.OemPlus) return "Plus";
        if (key == Key.OemMinus) return "Minus";
        if (key == Key.OemComma) return "Comma";
        if (key == Key.OemPeriod) return "Period";
        if (key == Key.OemSemicolon) return "Semicolon";
        if (key == Key.OemQuestion) return "Slash";
        if (key == Key.OemPipe) return "Backslash";
        if (key == Key.OemTilde) return "Tilde";
        if (key == Key.OemQuotes) return "Quote";
        if (key == Key.OemOpenBrackets) return "OpenBracket";
        if (key == Key.OemCloseBrackets) return "CloseBracket";
        if (key == Key.Add) return "NumPlus";
        if (key == Key.Subtract) return "NumMinus";
        if (key == Key.Multiply) return "NumMultiply";
        if (key == Key.Divide) return "NumDivide";
        if (key == Key.Decimal) return "NumDecimal";
        if (key == Key.MediaNextTrack) return "MediaNext";
        if (key == Key.MediaPreviousTrack) return "MediaPrevious";
        return key.ToString();
    }
}
