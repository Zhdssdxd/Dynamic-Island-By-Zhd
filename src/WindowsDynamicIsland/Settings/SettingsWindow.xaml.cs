using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Settings;

/// <summary>
/// Dark Windows-11-style settings window. All values apply live through <see cref="SettingsViewModel"/>.
/// On Windows 11 22H2+ the window uses the Mica backdrop; elsewhere a solid dark background
/// with a dark title bar (Windows 10 20H1+).
/// </summary>
public partial class SettingsWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    private readonly IEventBus _bus;
    private readonly SettingsViewModel _vm;
    private readonly FrameworkElement[]? _pages;
    private HotkeyManager? _suspendedHotkeys;
    private bool _closed;

    public SettingsWindow(SettingsManager settings, IEventBus bus, ModuleStatusRegistry statuses)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(statuses);
        _bus = bus;

        InitializeComponent();

        _vm = new SettingsViewModel(settings, bus, statuses, Dispatcher);
        DataContext = _vm;

        _pages = new FrameworkElement[]
        {
            PageAppearance, PageFeatures, PageIndicators, PageTimer, PageSystem, PageDiagnostics, PageAbout,
        };
        NavList.SelectedIndex = 0;

        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) => _vm.Flush();
        Closed += OnClosed;
    }

    /// <summary>Shows a specific page: 0 Внешний вид … 6 О программе.</summary>
    public void ShowPage(int index)
    {
        if (_pages is null || index < 0 || index >= _pages.Length) return;
        NavList.SelectedIndex = index;
    }

    // ───────────────────────────── Window chrome ─────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref on, sizeof(int));

            // DWMWA_SYSTEMBACKDROP_TYPE is supported from Windows 11 22H2 (build 22621).
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                var backdrop = DWMSBT_MAINWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                {
                    var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    if (DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0)
                    {
                        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                            target.BackgroundColor = Colors.Transparent;
                        Background = Brushes.Transparent;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings window: DWM styling failed", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        ResumeHotkeys();
        _vm.Dispose();
        DataContext = null;
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages is null) return;
        var index = NavList.SelectedIndex;
        if (index < 0) return;
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (_pages[index] is ScrollViewer sv) sv.ScrollToTop();
    }

    // ───────────────────────────── Features ─────────────────────────────

    private void BrowseDownloads_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Папка, в которой отслеживать загрузки",
                Multiselect = false,
            };
            var current = _vm.DownloadsFolder;
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
                _vm.DownloadsFolder = dialog.FolderName;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder dialog failed", ex);
        }
    }

    private void ResetDownloadsFolder_Click(object sender, RoutedEventArgs e) => _vm.DownloadsFolder = "";

    // ───────────────────────────── Timer ─────────────────────────────

    private void PresetsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    // ───────────────────────────── System ─────────────────────────────

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => _vm.RefreshMonitors();

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Вернуть все настройки к значениям по умолчанию?",
            "Dynamic Island",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _vm.ResetToDefaults();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_vm.LogsFolder);

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(Path.GetDirectoryName(_vm.SettingsFilePath) ?? AppPaths.RoamingDataDir);

    private void Exit_Click(object sender, RoutedEventArgs e) => _bus.Publish(new ExitRequested());

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open folder " + path, ex);
        }
    }

    // ───────────────────────────── Hotkey capture ─────────────────────────────

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: HotkeyEntry entry }) return;
        entry.Error = null;
        entry.Partial = null;
        entry.IsCapturing = true;

        // While recording, unregister global hotkeys so pressing an existing combination reaches this box.
        if (_suspendedHotkeys is null)
        {
            _suspendedHotkeys = ServiceRegistry.Get<HotkeyManager>();
            _suspendedHotkeys?.SetSuspended(true);
        }
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: HotkeyEntry entry })
        {
            entry.IsCapturing = false;
            entry.Partial = null;
        }
        ResumeHotkeys();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: HotkeyEntry entry }) return;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var modifierKeys = Keyboard.Modifiers;

        // Plain Tab / Shift+Tab keep normal keyboard navigation.
        if (key == Key.Tab && (modifierKeys == ModifierKeys.None || modifierKeys == ModifierKeys.Shift)) return;

        e.Handled = true;

        if (modifierKeys == ModifierKeys.None && key is Key.Escape or Key.Back or Key.Delete)
        {
            _vm.SetHotkey(entry, "");
            entry.IsCapturing = false;
            entry.Partial = null;
            return;
        }

        var modifiers = HotkeyParser.FromModifierKeys(modifierKeys);
        if (HotkeyParser.IsModifierKey(key) || key == Key.None)
        {
            entry.IsCapturing = true;
            entry.Partial = modifiers == HotkeyModifiers.None ? null : HotkeyParser.Format(modifiers, Key.None);
            return;
        }

        if (!HotkeyParser.TryNormalize(HotkeyParser.Format(modifiers, key), out var normalized))
        {
            entry.Error = "Нужен модификатор (Ctrl, Alt, Shift или Win) — без него допустимы только F1–F24";
            entry.Partial = null;
            entry.IsCapturing = true;
            return;
        }

        var duplicate = _vm.Hotkeys.FirstOrDefault(h =>
            !ReferenceEquals(h, entry) && string.Equals(h.Value, normalized, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            entry.Error = $"Сочетание {normalized} уже назначено: «{duplicate.Label}»";
            entry.Partial = null;
            entry.IsCapturing = true;
            return;
        }

        _vm.SetHotkey(entry, normalized);
        entry.Partial = null;
        entry.IsCapturing = false;
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: HotkeyEntry entry } || !entry.IsCapturing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!HotkeyParser.IsModifierKey(key)) return;

        var modifiers = HotkeyParser.FromModifierKeys(Keyboard.Modifiers);
        entry.Partial = modifiers == HotkeyModifiers.None ? null : HotkeyParser.Format(modifiers, Key.None);
        e.Handled = true;
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotkeyEntry entry)
            _vm.SetHotkey(entry, "");
    }

    private void ResumeHotkeys()
    {
        var manager = _suspendedHotkeys;
        _suspendedHotkeys = null;
        manager?.SetSuspended(false);
    }

    // ───────────────────────────── Interop ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}
