using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.ClipboardHistory;

/// <summary>
/// Separate dark window with the clipboard history: search, click to paste into the window the user came from
/// (like Win+V), copy only, pin (kept across restarts), delete, clear. Esc closes, Enter pastes the first match.
/// </summary>
public partial class ClipboardHistoryWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;

    private readonly ClipboardHistoryManager _manager;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _metaTimer;
    private readonly DispatcherTimer _statusTimer;
    private IntPtr _previousWindow;

    public ClipboardHistoryWindow(ClipboardHistoryManager manager)
    {
        InitializeComponent();
        _manager = manager;

        _view = new ListCollectionView(manager.Entries)
        {
            Filter = o => o is ClipboardEntry e && e.Matches(SearchBox.Text),
        };
        ItemsList.ItemsSource = _view;
        manager.Entries.CollectionChanged += OnEntriesChanged;

        _metaTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var e in _manager.Entries) e.RefreshMeta();
        }, Dispatcher);
        _statusTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1.6) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            UpdateFooter();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        UpdateFooter();
    }

    /// <summary>Shows the window and remembers where the user was typing (paste target).</summary>
    public void ShowFor(IntPtr previousWindow)
    {
        _previousWindow = previousWindow;
        SearchBox.Text = "";
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Topmost = false;
        SearchBox.Focus();
        foreach (var e in _manager.Entries) e.RefreshMeta();
    }

    // ───────────────────────────── Actions ─────────────────────────────

    private static ClipboardEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipboardEntry;

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || EntryOf(sender) is not { } entry) return;
        e.Handled = true;
        Paste(entry);
    }

    private void Paste(ClipboardEntry entry)
    {
        if (!_manager.CopyToClipboard(entry))
        {
            ShowStatus("Буфер обмена занят другой программой, попробуй ещё раз");
            return;
        }

        var target = _previousWindow;
        if (target == IntPtr.Zero || !IsWindow(target))
        {
            ShowStatus("Скопировано");
            return;
        }

        // Like Win+V: go back to the previous window and paste there.
        Close();
        SetForegroundWindow(target);
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (GetForegroundWindow() == target) SendCtrlV();
        };
        timer.Start();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (EntryOf(sender) is not { } entry) return;
        ShowStatus(_manager.CopyToClipboard(entry) ? "Скопировано" : "Буфер обмена занят другой программой");
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (EntryOf(sender) is { } entry) _manager.TogglePin(entry);
        UpdateFooter();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (EntryOf(sender) is { } entry) _manager.Remove(entry);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _manager.ClearUnpinned();

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateFooter();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && _view.Cast<ClipboardEntry>().FirstOrDefault() is { } first)
        {
            e.Handled = true;
            Paste(first);
        }
    }

    // ───────────────────────────── Footer ─────────────────────────────

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateFooter();

    private void UpdateFooter()
    {
        if (_statusTimer.IsEnabled) return;
        var total = _manager.Entries.Count;
        var visible = _view.Count;
        var pinned = _manager.Entries.Count(x => x.IsPinned);
        EmptyText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = total == 0 ? "Пока пусто — скопируй что-нибудь, и оно появится здесь" : "Ничего не найдено";
        FooterText.Text = total == 0 ? "" : pinned > 0 ? $"Записей: {total} · закреплено: {pinned}" : $"Записей: {total}";
    }

    private void ShowStatus(string text)
    {
        FooterText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ───────────────────────────── Window chrome / lifetime ─────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref on, sizeof(int));
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
            Log.Warn("Clipboard window: DWM styling failed", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _manager.Entries.CollectionChanged -= OnEntriesChanged;
        _metaTimer.Stop();
        _statusTimer.Stop();
        ItemsList.ItemsSource = null;
    }

    // ───────────────────────────── Native ─────────────────────────────

    private static void SendCtrlV()
    {
        const byte VK_CONTROL = 0x11;
        const byte VK_V = 0x56;
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
}
