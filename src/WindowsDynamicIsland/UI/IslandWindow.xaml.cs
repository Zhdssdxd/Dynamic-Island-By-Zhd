using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowsDynamicIsland.Animation;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.UI.Native;
using WindowsDynamicIsland.UI.Views;

namespace WindowsDynamicIsland.UI;

/// <summary>
/// The island: a fixed-size, transparent, top-most, non-activating host window with a morphing capsule inside.
/// Maps <see cref="IslandViewModel.ViewKind"/> → view + geometry, animates geometry with springs and content with
/// <see cref="MorphContentControl"/>, handles mouse interaction, multi-monitor placement and DPI changes.
/// </summary>
public partial class IslandWindow : Window
{
    private const string WidthKey = "island.width";
    private const string HeightKey = "island.height";
    private const string RadiusKey = "island.radius";
    private const string OpacityKey = "island.opacity";
    private const string HoverKey = "island.hover";

    private static readonly TimeSpan CollapseAfterLeave = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan CollapseWithoutMouse = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(350);

    private readonly IslandController _controller;
    private readonly SettingsManager _settings;
    private readonly IslandViewModel _vm;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly RectangleGeometry _clip = new();

    private NativeMethods.WinEventDelegate? _winEventProc;
    private IntPtr _winEventHook;
    private HwndSource? _source;

    private IslandViewKind _currentKind = (IslandViewKind)(-1);
    private string _currentKey = "";
    private FrameworkElement? _currentView;
    private IslandGeometry _target;
    private double _width;
    private double _height;
    private double _radius;
    private bool _geometryInitialized;
    private bool _pressedOnCapsule;
    private bool _closed;
    private string _placementSignature = "";

    public IslandWindow(IslandController controller, SettingsManager settings)
    {
        _controller = controller;
        _settings = settings;
        InitializeComponent();

        _vm = new IslandViewModel(controller);
        DataContext = _vm;

        _collapseTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Capsule.IsMouseOver && _controller.State.IsExpanded && !IsContextMenuOpen)
                _controller.Handle(IslandUserAction.Collapse);
        };
        _hoverExpandTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = HoverExpandDelay };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (Capsule.IsMouseOver && !_controller.State.IsExpanded) _controller.Handle(IslandUserAction.Expand);
        };

        ClipHost.Clip = _clip;

        Capsule.MouseEnter += OnCapsuleMouseEnter;
        Capsule.MouseLeave += OnCapsuleMouseLeave;
        Capsule.MouseLeftButtonDown += (_, _) => _pressedOnCapsule = true;
        Capsule.MouseLeftButtonUp += OnCapsuleLeftButtonUp;
        Capsule.MouseRightButtonUp += OnCapsuleRightButtonUp;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _controller.State.PropertyChanged += OnStatePropertyChanged;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        ApplyAppearance();
        UpdateLayoutForState(force: true);
    }

    private bool IsContextMenuOpen => Capsule.ContextMenu is { IsOpen: true };

    // ───────────────────────────── Window plumbing ─────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        // Tool window (no Alt+Tab / taskbar), never activates (clicks don't steal focus from the user's app).
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

        // Re-assert top-most whenever the foreground window changes (some apps push themselves above).
        _winEventProc = OnForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        Reposition(force: true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            case NativeMethods.WM_DPICHANGED:
                // WPF rescales the content itself; we only re-anchor the window to the top of its monitor.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reposition(force: true)));
                break;
            case NativeMethods.WM_DISPLAYCHANGE:
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reposition(force: true)));
                break;
            case NativeMethods.WM_SETTINGCHANGE when wParam.ToInt64() == NativeMethods.SPI_SETWORKAREA:
                // Taskbar moved / resized.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reposition(force: true)));
                break;
        }
        return IntPtr.Zero;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => EnsureTopmost();

    private void EnsureTopmost()
    {
        if (_closed || _source is null) return;
        NativeMethods.SetWindowPos(_source.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Reposition(force: true)));
    }

    /// <summary>Places the host window on the configured monitor (physical pixels, per-monitor DPI).</summary>
    private void Reposition(bool force)
    {
        if (_closed || _source is null) return;
        try
        {
            var s = _controller.State.Settings;
            var a = s.Appearance;
            var windowOffset = Math.Max(0, a.TopOffset - IslandLayout.ShadowRoom);
            var capsuleTop = a.TopOffset - windowOffset;

            CapsuleHost.Margin = a.HorizontalPosition switch
            {
                IslandHorizontalPosition.Left => new Thickness(IslandLayout.SideMargin, capsuleTop, 0, 0),
                IslandHorizontalPosition.Right => new Thickness(0, capsuleTop, IslandLayout.SideMargin, 0),
                _ => new Thickness(0, capsuleTop, 0, 0),
            };
            CapsuleHost.HorizontalAlignment = a.HorizontalPosition switch
            {
                IslandHorizontalPosition.Left => HorizontalAlignment.Left,
                IslandHorizontalPosition.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center,
            };
            CapsuleHost.RenderTransformOrigin = a.HorizontalPosition switch
            {
                IslandHorizontalPosition.Left => new Point(0, 0),
                IslandHorizontalPosition.Right => new Point(1, 0),
                _ => new Point(0.5, 0),
            };

            var p = WindowPlacement.Compute(s, IslandLayout.HostWidth, IslandLayout.HostHeight, windowOffset);
            var signature = $"{p.X},{p.Y},{p.Width},{p.Height}";
            if (!force && signature == _placementSignature) return;
            _placementSignature = signature;

            NativeMethods.SetWindowPos(_source.Handle, NativeMethods.HWND_TOPMOST, p.X, p.Y, p.Width, p.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
        catch (Exception ex)
        {
            Log.Warn("Island reposition failed", ex);
        }
    }

    // ───────────────────────────── State → layout ─────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandViewModel.ViewKind) or nameof(IslandViewModel.HasLyrics))
            UpdateLayoutForState(force: false);
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandState.Settings):
                ApplyAppearance();
                Reposition(force: false);
                UpdateLayoutForState(force: false);
                break;
            case nameof(IslandState.IsExpanded):
                OnExpandedChanged();
                UpdateLayoutForState(force: false);
                break;
            case nameof(IslandState.Media):
            case nameof(IslandState.MediaSessions):
            case nameof(IslandState.Timer):
            case nameof(IslandState.SecondaryActivity):
            case nameof(IslandState.Notification):
            case nameof(IslandState.TransientSequence):
                // Geometry may change inside the same view kind (timeline appears, badge, banner height…).
                UpdateLayoutForState(force: false);
                break;
        }
    }

    /// <summary>
    /// Resolves the target view + geometry. Swaps content (with a morph transition) when the view key changes,
    /// then drives the capsule geometry springs toward the target.
    /// </summary>
    private void UpdateLayoutForState(bool force)
    {
        if (_closed) return;
        var state = _controller.State;
        var kind = _vm.ViewKind;
        var key = IslandLayout.ViewKey(kind, state);
        var target = IslandLayout.Measure(kind, state, MeasureNotification, _vm.HasLyrics);

        var previousKind = _currentKind;
        var contentChanged = force || key != _currentKey;

        if (contentChanged)
        {
            ContentHost.Transition = ChooseTransition(previousKind, kind);
            var view = CreateView(kind);
            SizeView(view, target);
            _currentView = view;
            _currentKind = kind;
            _currentKey = key;
            ContentHost.Content = view;
            ContentHost.TransitionKey = key;
        }
        else if (_currentView is not null)
        {
            SizeView(_currentView, target);
        }

        AnimateGeometry(previousKind, kind, target, snap: !_geometryInitialized);
        _geometryInitialized = true;

        Capsule.IsHitTestVisible = kind != IslandViewKind.Hidden;
        if (kind is IslandViewKind.Hidden or IslandViewKind.Notification or IslandViewKind.Indicator) ResetHover();

        // Expanded via hotkey/tray (mouse elsewhere) → collapse on its own after a while.
        if (IsExpandedKind(kind) && !Capsule.IsMouseOver && !_collapseTimer.IsEnabled)
            StartCollapseTimer(CollapseWithoutMouse);
    }

    private static bool IsExpandedKind(IslandViewKind kind) => kind is IslandViewKind.ExpandedMusic or IslandViewKind.ExpandedVideo
        or IslandViewKind.ExpandedTimer or IslandViewKind.ExpandedDownload or IslandViewKind.Dashboard;

    private static ContentTransition ChooseTransition(IslandViewKind from, IslandViewKind to)
    {
        if (from == to && to is IslandViewKind.Notification or IslandViewKind.Indicator) return ContentTransition.SlideUp;
        if (to == IslandViewKind.Hidden || from == IslandViewKind.Hidden) return ContentTransition.Fade;
        if (IslandLayout.IsCompactLike(to) && IsExpandedKind(from)) return ContentTransition.Fade;
        return ContentTransition.ScaleFade;
    }

    private FrameworkElement CreateView(IslandViewKind kind)
    {
        FrameworkElement view = kind switch
        {
            IslandViewKind.CompactMusic => new CompactMusicView(),
            IslandViewKind.CompactVideo => new CompactVideoView(),
            IslandViewKind.CompactTimer => new CompactTimerView(),
            IslandViewKind.CompactDownload => new CompactDownloadView(),
            IslandViewKind.ExpandedMusic => new ExpandedMusicView(),
            IslandViewKind.ExpandedVideo => new ExpandedVideoView(),
            IslandViewKind.ExpandedTimer => new ExpandedTimerView(),
            IslandViewKind.ExpandedDownload => new ExpandedDownloadView(),
            IslandViewKind.Dashboard => new DashboardView(),
            IslandViewKind.Notification => new NotificationView(),
            IslandViewKind.Indicator => new IndicatorView(),
            _ => new IdleView(),
        };
        view.DataContext = _vm;
        view.Focusable = false;
        return view;
    }

    /// <summary>Views are laid out at their FINAL size and revealed by the growing clip (no reflow during morphs).</summary>
    private static void SizeView(FrameworkElement view, IslandGeometry g)
    {
        view.Width = Math.Max(0, g.Width - 2);
        view.Height = Math.Max(0, g.Height - 2);
    }

    private double MeasureNotification(IslandNotification n)
    {
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var font = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        return IslandLayout.MeasureNotificationHeight(n, ppd, font);
    }

    private void AnimateGeometry(IslandViewKind from, IslandViewKind to, IslandGeometry target, bool snap)
    {
        var engine = AnimationEngine.Instance;
        var grows = target.Area > _width * _height + 1;
        var spring = snap ? SpringPresets.Snappy
            : from == to && to == IslandViewKind.Indicator ? SpringPresets.Snappy
            : to == IslandViewKind.Hidden ? SpringPresets.Smooth
            : grows ? SpringPresets.Bouncy
            : SpringPresets.Smooth;

        if (snap)
        {
            _width = target.Width;
            _height = target.Height;
            _radius = target.CornerRadius;
            ApplyGeometry();
            CapsuleHost.Opacity = target.Opacity;
            _target = target;
            return;
        }

        if (target == _target && engine.ActiveCount == 0) return;
        _target = target;

        engine.Animate(WidthKey, _width, target.Width, v => { _width = Math.Max(1, v); ApplyGeometry(); }, spring);
        engine.Animate(HeightKey, _height, target.Height, v => { _height = Math.Max(1, v); ApplyGeometry(); }, spring);
        engine.Animate(RadiusKey, _radius, target.CornerRadius, v => { _radius = Math.Max(0, v); ApplyGeometry(); }, spring);
        engine.Animate(OpacityKey, CapsuleHost.Opacity, target.Opacity,
            v => CapsuleHost.Opacity = Math.Clamp(v, 0, 1), SpringPresets.Gentle);
    }

    private void ApplyGeometry()
    {
        CapsuleHost.Width = _width;
        CapsuleHost.Height = _height;
        var r = Math.Min(_radius, Math.Min(_width, _height) / 2);
        Capsule.CornerRadius = new CornerRadius(r);
        var inner = Math.Max(0, r - 1);
        _clip.Rect = new Rect(0, 0, Math.Max(0, _width - 2), Math.Max(0, _height - 2));
        _clip.RadiusX = inner;
        _clip.RadiusY = inner;
    }

    private void ApplyAppearance()
    {
        var a = _controller.State.Settings.Appearance;
        var baseColor = a.ColorScheme == IslandColorScheme.Graphite
            ? (TryFindResource("GraphiteColor") is Color g ? g : Color.FromRgb(0x0E, 0x0E, 0x10))
            : Colors.Black;
        var alpha = (byte)Math.Round(Math.Clamp(a.Opacity, 0.5, 1.0) * 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        Capsule.Background = brush;
        CapsuleShadow.Opacity = 0.45 * a.Opacity;
    }

    // ───────────────────────────── Mouse interaction ─────────────────────────────

    private void OnCapsuleMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        var kind = _vm.ViewKind;
        if (kind is IslandViewKind.Idle || IslandLayout.IsCompactLike(kind))
        {
            AnimateHover(1.04);
            if (_controller.State.Settings.Appearance.ExpandOnHover) _hoverExpandTimer.Start();
        }
    }

    private void OnCapsuleMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverExpandTimer.Stop();
        _pressedOnCapsule = false;
        ResetHover();
        if (_controller.State.IsExpanded) StartCollapseTimer(CollapseAfterLeave);
    }

    private void OnCapsuleLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || !_pressedOnCapsule) return; // buttons / seek bar handled it
        _pressedOnCapsule = false;
        _hoverExpandTimer.Stop();
        ResetHover();
        _controller.Handle(IslandUserAction.ToggleExpanded);
        e.Handled = true;
    }

    private void OnCapsuleRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = BuildContextMenu();
        Capsule.ContextMenu = menu;
        menu.PlacementTarget = Capsule;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            if (_controller.State.IsExpanded && !Capsule.IsMouseOver) StartCollapseTimer(CollapseAfterLeave);
        };
        menu.IsOpen = true;
    }

    private void OnExpandedChanged()
    {
        if (_controller.State.IsExpanded)
        {
            ResetHover();
            if (!Capsule.IsMouseOver) StartCollapseTimer(CollapseWithoutMouse);
        }
        else
        {
            _collapseTimer.Stop();
        }
    }

    private void StartCollapseTimer(TimeSpan delay)
    {
        _collapseTimer.Stop();
        _collapseTimer.Interval = delay;
        _collapseTimer.Start();
    }

    private void AnimateHover(double scale)
    {
        AnimationEngine.Instance.Animate(HoverKey, HoverScale.ScaleX, scale, v =>
        {
            HoverScale.ScaleX = v;
            HoverScale.ScaleY = v;
        }, SpringPresets.Snappy);
    }

    private void ResetHover()
    {
        if (Math.Abs(HoverScale.ScaleX - 1) > 0.0001 || AnimationEngine.Instance.TryGetCurrent(HoverKey, out _))
            AnimateHover(1.0);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        if (TryFindResource("IslandContextMenuStyle") is Style style) menu.Style = style;

        var timer = _controller.State.Timer;
        if (timer is { IsActive: true })
        {
            menu.Items.Add(Item(timer.State == TimerRunState.Running ? "Пауза таймера" : "Продолжить таймер",
                timer.State == TimerRunState.Running ? Glyphs.Pause : Glyphs.Play, _vm.TimerToggleCommand));
            menu.Items.Add(Item("Остановить таймер", Glyphs.Stop, _vm.TimerStopCommand));
        }
        else
        {
            menu.Items.Add(Item("Секундомер", Glyphs.Stopwatch, _vm.StartStopwatchCommand));
            var timers = Item("Таймер", Glyphs.Clock, null);
            foreach (var preset in _vm.TimerPresets)
                timers.Items.Add(Item(IslandViewModel.FormatPreset(preset.Minutes), null, _vm.StartTimerCommand, preset.Minutes));
            menu.Items.Add(timers);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Настройки…", Glyphs.Settings, _vm.OpenSettingsCommand));
        menu.Items.Add(Item("Скрыть остров", Glyphs.Close, _vm.HideIslandCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Выход", null, _vm.ExitCommand));
        return menu;
    }

    private static MenuItem Item(string header, string? glyph, ICommand? command, object? parameter = null)
    {
        var item = new MenuItem { Header = header, Command = command, CommandParameter = parameter };
        if (glyph is not null) item.Icon = new TextBlock { Text = glyph };
        return item;
    }

    // ───────────────────────────── Shutdown ─────────────────────────────

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _controller.State.PropertyChanged -= OnStatePropertyChanged;
        _collapseTimer.Stop();
        _hoverExpandTimer.Stop();
        foreach (var key in new[] { WidthKey, HeightKey, RadiusKey, OpacityKey, HoverKey })
            AnimationEngine.Instance.Stop(key);
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
        ContentHost.Content = null;
        _vm.Dispose();
    }
}
