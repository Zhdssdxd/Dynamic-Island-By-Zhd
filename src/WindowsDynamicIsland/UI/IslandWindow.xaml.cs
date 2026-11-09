using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowsDynamicIsland.Animation;
using WindowsDynamicIsland.Audio;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.UI.Components;
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
    private const string FillKey = "island.fill";

    /// <summary>Bar height used in top-bar mode when nothing is docked on top and no height is set.</summary>
    private const double DefaultTopBarHeight = 32;

    private static readonly TimeSpan CollapseAfterLeave = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan CollapseWithoutMouse = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(350);
    /// <summary>The clipboard is usually opened from the keyboard — give the mouse time to get there.</summary>
    private static readonly TimeSpan ClipboardCollapseWithoutMouse = TimeSpan.FromSeconds(12);

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

    // Top-bar mode: compact height = bar height; optional outline-only (transparent) compact capsule.
    private double _topBarHeight;
    private SolidColorBrush? _fillBrush;
    private Color _fillColor = Colors.Black;
    private double _fillOpacity = 1;
    private double _fillAmount = 1; // 1 = solid capsule, 0 = outline only
    private Brush? _defaultStroke;
    private static readonly Brush OutlineStroke = CreateFrozenBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));

    // Beat pulse (bottom edge bounces to the music while expanded).
    private const double BeatMaxOffset = 18;
    private const double BeatBreathing = 4;
    private double _waveMaxBulge;
    private double _swellLevel;
    private const int WavePoints = 48;
    /// <summary>Frequency heard at the centre of the bottom wave (bass) …</summary>
    private const double WaveCenterHz = 55;
    /// <summary>… and at its ends near the corners; in between the axis is logarithmic, like the iPhone.</summary>
    private const double WaveEdgeHz = 6000;
    private readonly float[] _waveLevels = new float[WavePoints + 1];
    private readonly CrestWave _crest = new();
    private double _wavePhase;
    private TimeSpan _lastWaveFrame;
    private readonly float[] _beatBands = new float[32];
    private IDisposable? _beatConsumer;
    private bool _beatHooked;
    private bool _beatWanted;
    private double _beatOffset;
    private double _bassAverage;

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
        _defaultStroke = Capsule.BorderBrush;

        WavePath.Data = _crest.Outline;
        WaveGroup.Clip = _crest.Outline; // the blurred shade never spills outside the crest
        WaveShadePath.Data = _crest.Shade;

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
            var topBar = a.TopBarMode;
            var topOffset = topBar ? 0 : a.TopOffset;
            var windowOffset = Math.Max(0, topOffset - IslandLayout.ShadowRoom);
            var capsuleTop = topOffset - windowOffset;

            var barHeight = topBar ? ResolveTopBarHeight(s) : 0;
            if (Math.Abs(barHeight - _topBarHeight) > 0.1)
            {
                _topBarHeight = barHeight;
                UpdateLayoutForState(force: false);
            }

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

            var p = WindowPlacement.Compute(s, IslandLayout.HostWidth, IslandLayout.HostHeight, windowOffset, topBar);
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

    /// <summary>Bar height in DIPs: the user's value, else what is docked on top, else <see cref="DefaultTopBarHeight"/>.</summary>
    private static double ResolveTopBarHeight(AppSettings s)
    {
        var h = s.Appearance.TopBarHeight;
        if (h <= 0)
        {
            try
            {
                h = WindowPlacement.GetTopBarHeightDip(s);
            }
            catch (Exception ex)
            {
                Log.Warn("Top bar detection failed", ex);
                h = 0;
            }
        }
        if (h <= 0) h = DefaultTopBarHeight;
        return Math.Round(Math.Clamp(h, 16, 60));
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
            case nameof(IslandState.IsVisualizerActive):
                UpdateBeatPulse();
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
        var target = IslandLayout.Measure(kind, state, MeasureNotification, _vm.HasLyrics, _topBarHeight);

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
        UpdateCapsuleFill(kind, animate: true);
        UpdateBeatPulse();
        if (kind is IslandViewKind.Hidden or IslandViewKind.Notification or IslandViewKind.Indicator) ResetHover();

        // Expanded via hotkey/tray (mouse elsewhere) → collapse on its own after a while.
        if (IsExpandedKind(kind) && !Capsule.IsMouseOver && !_collapseTimer.IsEnabled)
            StartCollapseTimer(kind == IslandViewKind.Clipboard ? ClipboardCollapseWithoutMouse : CollapseWithoutMouse);
    }

    private static bool IsExpandedKind(IslandViewKind kind) => kind is IslandViewKind.ExpandedMusic or IslandViewKind.ExpandedVideo
        or IslandViewKind.ExpandedTimer or IslandViewKind.ExpandedDownload or IslandViewKind.Dashboard or IslandViewKind.Clipboard;

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
            IslandViewKind.Clipboard => new ClipboardView(),
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
        var height = _height;
        CapsuleHost.Width = _width;
        CapsuleHost.Height = height;
        var r = Math.Min(_radius, Math.Min(_width, _height) / 2);
        Capsule.CornerRadius = new CornerRadius(r);
        var inner = Math.Max(0, r - 1);
        _clip.Rect = new Rect(0, 0, Math.Max(0, _width - 2), Math.Max(0, height - 2));
        _clip.RadiusX = inner;
        _clip.RadiusY = inner;
    }

    private void ApplyAppearance()
    {
        var a = _controller.State.Settings.Appearance;
        _fillColor = a.ColorScheme == IslandColorScheme.Graphite
            ? (TryFindResource("GraphiteColor") is Color g ? g : Color.FromRgb(0x0E, 0x0E, 0x10))
            : Colors.Black;
        _fillOpacity = Math.Clamp(a.Opacity, 0.5, 1.0);
        if (_fillBrush is null)
        {
            // One live (unfrozen) brush for the capsule and the beat wave, so top-bar mode can fade it in and out.
            _fillBrush = new SolidColorBrush(Colors.Black);
            Capsule.Background = _fillBrush;
            WavePath.Fill = _fillBrush;
            WaveShadePath.Fill = _fillBrush;
        }
        UpdateCapsuleFill(_vm.ViewKind, animate: false);
    }

    /// <summary>
    /// Solid capsule normally; in top-bar mode with <see cref="TopBarFill.Outline"/> the compact capsule turns transparent
    /// (only the outline stays, blending with the bar) and fades back to solid when it expands. A 1/255 alpha floor keeps
    /// the capsule clickable (fully transparent pixels of a layered window are click-through).
    /// </summary>
    private void UpdateCapsuleFill(IslandViewKind kind, bool animate)
    {
        if (_fillBrush is null) return;
        var a = _controller.State.Settings.Appearance;
        var outline = a.TopBarMode && a.TopBarFill == TopBarFill.Outline
                      && (IslandLayout.IsCompactLike(kind) || kind == IslandViewKind.Hidden);
        Capsule.BorderBrush = outline ? OutlineStroke : _defaultStroke;
        var target = outline ? 0.0 : 1.0;
        if (animate && _geometryInitialized)
            AnimationEngine.Instance.Animate(FillKey, _fillAmount, target, SetFillAmount, SpringPresets.Gentle);
        else
        {
            AnimationEngine.Instance.Stop(FillKey);
            SetFillAmount(target);
        }
    }

    private void SetFillAmount(double v)
    {
        _fillAmount = Math.Clamp(v, 0, 1);
        if (_fillBrush is null) return;
        var alpha = Math.Max(1.0 / 255, _fillOpacity * _fillAmount);
        _fillBrush.Color = Color.FromArgb((byte)Math.Round(alpha * 255), _fillColor.R, _fillColor.G, _fillColor.B);
        CapsuleShadow.Opacity = 0.45 * _fillOpacity * _fillAmount;
    }

    private static Brush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ───────────────────────────── Beat pulse ─────────────────────────────

    /// <summary>
    /// Starts/stops the beat pulse: only while the island is expanded on music or video, media is playing,
    /// the visualizer (audio analysis) is enabled and the user did not switch the effect off.
    /// The render hook stays alive just long enough for the offset to settle back to zero.
    /// </summary>
    private void UpdateBeatPulse()
    {
        var state = _controller.State;
        var kind = _vm.ViewKind;
        _beatWanted = !_closed
                      && state.Settings.Appearance.PulseToBeat
                      && state.IsVisualizerActive
                      && kind is IslandViewKind.ExpandedMusic or IslandViewKind.ExpandedVideo;

        if (_beatWanted)
        {
            if (_beatConsumer is null && ServiceRegistry.Get<ISpectrumSource>() is { } source)
                _beatConsumer = source.AcquireConsumer();
            HookBeat();
        }
        else
        {
            _beatConsumer?.Dispose();
            _beatConsumer = null;
            if (_beatOffset > 0.05) HookBeat();
            else UnhookBeat();
        }
    }

    private void HookBeat()
    {
        UpdateAmbientSampling(true); // also re-checks the setting while already running
        if (_beatHooked) return;
        _beatHooked = true;
        CompositionTarget.Rendering += OnBeatFrame;
    }

    private void UnhookBeat()
    {
        if (!_beatHooked) return;
        _beatHooked = false;
        CompositionTarget.Rendering -= OnBeatFrame;
        UpdateAmbientSampling(false);
        _lastWaveFrame = TimeSpan.Zero;
        Array.Clear(_waveLevels);
        WaveGroup.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Per frame: bass energy (peak of 40–150 Hz) vs. its running average detects beats; the spectrum shapes the
    /// wave on a logarithmic frequency axis (bass in the middle, highs toward the corners) and the wave travels along the bottom edge, faster on beats.
    /// </summary>
    private void OnBeatFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.Zero;
        if (now == _lastWaveFrame && now != TimeSpan.Zero) return; // Rendering can fire twice per frame
        var dt = _lastWaveFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((now - _lastWaveFrame).TotalSeconds, 0, 0.1);
        _lastWaveFrame = now;

        var speed = AnimationEngine.Instance.SpeedMultiplier;
        double target = 0;
        double kick = 0;
        var haveBands = _beatWanted && ServiceRegistry.Get<ISpectrumSource>() is { } source && source.TryGetBands(_beatBands, out _);
        if (haveBands)
        {
            double bass = SpectrumShaper.PeakBetween(_beatBands, SpectrumShaper.MinHz, SpectrumShaper.BassHighHz);
            _bassAverage += (bass - _bassAverage) * 0.05;
            kick = Math.Clamp((bass - _bassAverage * 1.04) / 0.16, 0, 1);
            target = BeatMaxOffset * kick + BeatBreathing * bass;
            var mids = SpectrumShaper.PeakBetween(_beatBands, 150, 1500);
            _swellLevel += (mids - _swellLevel) * 0.25;
        }

        // Fast attack, smooth release.
        var k = target > _beatOffset ? 0.6 : 0.09; // quick swell, soft settle
        if (!haveBands) _swellLevel *= 0.9;
        _beatOffset += (target - _beatOffset) * Math.Min(1, k * speed);

        // Spectrum → wave levels, mirrored around the centre (bass in the middle).
        var maxLevel = 0f;
        for (var i = 0; i <= WavePoints; i++)
        {
            float v = 0;
            if (haveBands)
            {
                // 0 at the centre → 1 at the corners, mapped onto a logarithmic frequency axis.
                var d = Math.Abs(2.0 * i / WavePoints - 1);
                v = SpectrumShaper.SampleAt(_beatBands, WaveCenterHz * Math.Pow(WaveEdgeHz / WaveCenterHz, d));
            }
            var current = _waveLevels[i];
            current += (v - current) * (v > current ? 0.5f : 0.15f);
            _waveLevels[i] = current;
            if (current > maxLevel) maxLevel = current;
        }

        _wavePhase += dt * speed * (1.8 + 3 * kick); // calm drift; a kick only nudges it
        StepAmbientColor(dt);
        if (_wavePhase > Math.PI * 2000) _wavePhase -= Math.PI * 2000;

        if (!_beatWanted && _beatOffset < 0.05 && maxLevel < 0.02f)
        {
            _beatOffset = 0;
            _bassAverage = 0;
            _swellLevel = 0;
            UnhookBeat();
            return;
        }
        UpdateWavePath();
    }

    /// <summary>
    /// Rebuilds the wave in place: a strip spanning the straight part of the bottom edge (between the rounded corners),
    /// its top hidden inside the capsule and its bottom following the wave. A sine envelope makes it melt into the corners.
    /// </summary>
    private void UpdateWavePath()
    {
        if (!_crest.Update(_width, _height, _radius, _beatOffset, _swellLevel, _wavePhase))
        {
            WaveGroup.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(WaveGroup, _crest.Inset);
        Canvas.SetTop(WaveGroup, 0);
        WaveGroup.Visibility = Visibility.Visible;
        _waveMaxBulge += (_crest.MaxBulge - _waveMaxBulge) * 0.2;

        if (_ambientWaveBrush is not null && ReferenceEquals(WavePath.Fill, _ambientWaveBrush))
        {
            // Colour runs left → right like the screen under the crest.
            _ambientWaveBrush.StartPoint = new Point(0, 0);
            _ambientWaveBrush.EndPoint = new Point(_crest.StripWidth, 0);
        }
    }

    // ───────────────────────────── Ambient colour (what is behind the island) ─────────────────────────────

    private const int AmbientColumns = 7;
    private LinearGradientBrush? _ambientWaveBrush;
    private readonly Color[] _ambientColors = new Color[AmbientColumns];
    private readonly Color[] _ambientTargets = new Color[AmbientColumns];
    private bool _ambientValid;
    private DispatcherTimer? _ambientTimer;

    /// <summary>Wave colour follows the screen under the island while the beat wave runs (setting "Волна цвета фона").</summary>
    private void UpdateAmbientSampling(bool active)
    {
        var enabled = active && _controller.State.Settings.Appearance.WaveAmbientColor;
        if (enabled)
        {
            if (_ambientWaveBrush is null)
            {
                _ambientWaveBrush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
                for (var i = 0; i < AmbientColumns; i++)
                    _ambientWaveBrush.GradientStops.Add(new GradientStop(Colors.Black, (double)i / (AmbientColumns - 1)));
            }
            WavePath.Fill = _ambientWaveBrush;
            WaveShadePath.Visibility = Visibility.Visible;
            if (_ambientTimer is null)
            {
                _ambientTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                _ambientTimer.Tick += (_, _) => SampleAmbient();
            }
            if (!_ambientTimer.IsEnabled)
            {
                _ambientValid = false;
                SampleAmbient();
                Array.Copy(_ambientTargets, _ambientColors, AmbientColumns);
                _ambientTimer.Start();
            }
        }
        else
        {
            _ambientTimer?.Stop();
            WaveShadePath.Visibility = Visibility.Collapsed;
            if (_fillBrush is not null) WavePath.Fill = _fillBrush;
        }
    }

    /// <summary>
    /// Reads a thin strip of the screen just below the island (past its shadow), i.e. exactly what the crest hangs
    /// over, and averages it into <see cref="AmbientColumns"/> colours from left to right. Physical pixels.
    /// </summary>
    private void SampleAmbient()
    {
        if (_closed || !IsLoaded || PresentationSource.FromVisual(CapsuleHost) is null) return;
        try
        {
            var topLeft = CapsuleHost.PointToScreen(new Point(0, 0));
            var bottomRight = CapsuleHost.PointToScreen(new Point(_width, _height));
            var scale = Math.Max(0.5, (bottomRight.X - topLeft.X) / Math.Max(1, _width));
            var r = Math.Min(_radius, Math.Min(_width, _height) / 2) * 0.3 * scale;
            var left = (int)(topLeft.X + r);
            var right = (int)(bottomRight.X - r);
            var y = (int)(bottomRight.Y + (BeatMaxOffset + 22) * scale); // below the crest and the drop shadow
            var strip = new System.Drawing.Rectangle(left, y, Math.Max(2, right - left), Math.Max(2, (int)(10 * scale)));
            var virt = System.Windows.Forms.SystemInformation.VirtualScreen;
            var clipped = System.Drawing.Rectangle.Intersect(strip, virt);
            if (clipped.Width < AmbientColumns * 2 || clipped.Height < 2) return;

            using var bmp = new System.Drawing.Bitmap(clipped.Width, clipped.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(clipped.Location, System.Drawing.Point.Empty, clipped.Size);

            var colWidth = bmp.Width / (double)AmbientColumns;
            for (var c = 0; c < AmbientColumns; c++)
            {
                double sr = 0, sg = 0, sb = 0;
                var n = 0;
                var x0 = (int)(c * colWidth);
                var x1 = Math.Min(bmp.Width, (int)((c + 1) * colWidth));
                for (var py = 0; py < bmp.Height; py += 3)
                for (var px = x0; px < x1; px += 3)
                {
                    var p = bmp.GetPixel(px, py);
                    sr += p.R;
                    sg += p.G;
                    sb += p.B;
                    n++;
                }
                if (n > 0) _ambientTargets[c] = Vivid(Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n)));
            }
            _ambientValid = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Ambient colour sampling failed", ex);
            _ambientTimer?.Stop();
        }
    }

    /// <summary>
    /// The crest should read as a lit edge, not as a hole showing the background: keep the hue of what is behind,
    /// push saturation up and keep brightness in a range that stands out against both the black body and the screen.
    /// </summary>
    internal static Color Vivid(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double h = 0;
        if (delta > 1e-6)
        {
            if (max == r) h = ((g - b) / delta % 6 + 6) % 6;
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
        }
        var s = max <= 0 ? 0 : delta / max;
        s = Math.Min(1, s * 1.6);
        var v = Math.Clamp(max, 0.55, 0.92);

        var chroma = v * s;
        var xx = chroma * (1 - Math.Abs(h % 2 - 1));
        double r1, g1, b1;
        switch ((int)h)
        {
            case 0: (r1, g1, b1) = (chroma, xx, 0); break;
            case 1: (r1, g1, b1) = (xx, chroma, 0); break;
            case 2: (r1, g1, b1) = (0, chroma, xx); break;
            case 3: (r1, g1, b1) = (0, xx, chroma); break;
            case 4: (r1, g1, b1) = (xx, 0, chroma); break;
            default: (r1, g1, b1) = (chroma, 0, xx); break;
        }
        var m = v - chroma;
        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    /// <summary>Per frame: glide every column toward its sampled colour.</summary>
    private void StepAmbientColor(double dt)
    {
        if (_ambientWaveBrush is null || !ReferenceEquals(WavePath.Fill, _ambientWaveBrush) || !_ambientValid) return;
        var k = 1 - Math.Exp(-dt * 5);
        var alpha = _fillBrush?.Color.A ?? (byte)255;
        for (var i = 0; i < AmbientColumns; i++)
        {
            var from = _ambientColors[i];
            var to = _ambientTargets[i];
            var c = Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * k),
                (byte)Math.Round(from.G + (to.G - from.G) * k),
                (byte)Math.Round(from.B + (to.B - from.B) * k));
            _ambientColors[i] = c;
            _ambientWaveBrush.GradientStops[i].Color = Color.FromArgb(alpha, c.R, c.G, c.B);
        }
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
            if (!Capsule.IsMouseOver)
                StartCollapseTimer(_controller.State.IsClipboardOpen ? ClipboardCollapseWithoutMouse : CollapseWithoutMouse);
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
        menu.Items.Add(Item("Буфер обмена", Glyphs.Copy, _vm.ToggleClipboardCommand));
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
        _beatWanted = false;
        UnhookBeat();
        _beatConsumer?.Dispose();
        _beatConsumer = null;
        foreach (var key in new[] { WidthKey, HeightKey, RadiusKey, OpacityKey, HoverKey, FillKey })
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
