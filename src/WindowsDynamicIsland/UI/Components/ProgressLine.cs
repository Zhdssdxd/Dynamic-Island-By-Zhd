using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsDynamicIsland.Animation;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>
/// Media timeline: thin rounded track + fill. When <see cref="IsSeekable"/> the line thickens on hover and
/// supports click/drag-to-seek; on release <see cref="SeekCommand"/> is executed with the fraction (double 0..1).
/// The hit area is the whole element height so the 4 px line is easy to grab.
/// </summary>
public sealed class ProgressLine : FrameworkElement
{
    private static readonly TimeSpan SeekHold = TimeSpan.FromMilliseconds(1200);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(ProgressLine),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressLine),
        new FrameworkPropertyMetadata(LevelBar.DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
        nameof(LineThickness), typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsSeekableProperty = DependencyProperty.Register(
        nameof(IsSeekable), typeof(bool), typeof(ProgressLine),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsSeekableChanged));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(ProgressLine), new FrameworkPropertyMetadata(null));

    internal static readonly DependencyProperty HoverProgressProperty = DependencyProperty.Register(
        "HoverProgress", typeof(double), typeof(ProgressLine),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer _holdTimer;
    private bool _dragging;
    private double _dragValue;
    private double _holdValue;
    private DateTime _holdUntil = DateTime.MinValue;

    public ProgressLine()
    {
        _holdTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SeekHold + TimeSpan.FromMilliseconds(50) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            InvalidateVisual();
        };
        Unloaded += (_, _) =>
        {
            _holdTimer.Stop();
            AnimationEngine.Instance.Stop(this);
        };
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double LineThickness { get => (double)GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }
    public bool IsSeekable { get => (bool)GetValue(IsSeekableProperty); set => SetValue(IsSeekableProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    private static void OnIsSeekableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ProgressLine)d;
        self.Cursor = (bool)e.NewValue ? Cursors.Hand : null;
        if (!(bool)e.NewValue) self.AnimateHover(false);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, LineThickness + 2);

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (IsSeekable) AnimateHover(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging) AnimateHover(false);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsSeekable || ActualWidth <= 0) return;
        if (CaptureMouse())
        {
            _dragging = true;
            _dragValue = FractionAt(e.GetPosition(this));
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _dragValue = FractionAt(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            if (IsSeekable) e.Handled = true;
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        var fraction = _dragValue;
        _holdValue = fraction;
        _holdUntil = DateTime.UtcNow + SeekHold;
        _holdTimer.Stop();
        _holdTimer.Start();

        var command = SeekCommand;
        if (command is not null && command.CanExecute(fraction)) command.Execute(fraction);
        if (!IsMouseOver) AnimateHover(false);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false;
        if (!IsMouseOver) AnimateHover(false);
        InvalidateVisual();
    }

    private double FractionAt(Point p) => ActualWidth > 0 ? Math.Clamp(p.X / ActualWidth, 0, 1) : 0;

    private void AnimateHover(bool on)
    {
        if (!IsLoaded)
        {
            SetValue(HoverProgressProperty, on ? 1.0 : 0.0);
            return;
        }
        AnimationEngine.Instance.Animate(this, (double)GetValue(HoverProgressProperty), on ? 1.0 : 0.0,
            v => SetValue(HoverProgressProperty, v), SpringPresets.Snappy);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Transparent full-size rect makes the whole element hit-testable.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var hover = Math.Clamp((double)GetValue(HoverProgressProperty), 0, 1.5);
        var th = Math.Max(1, LineThickness + (IsSeekable ? 2 * hover : 0));
        th = Math.Min(th, h);
        var y = (h - th) / 2;
        var r = th / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, y, w, th), r, r);

        double v;
        if (_dragging) v = _dragValue;
        else if (DateTime.UtcNow < _holdUntil) v = _holdValue;
        else v = Value;
        v = double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
        if (v <= 0.0005) return;
        var fw = Math.Min(w, Math.Max(th, w * v));
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, y, fw, th), r, r);
    }
}
