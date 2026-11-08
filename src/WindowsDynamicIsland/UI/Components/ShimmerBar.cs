using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>
/// Indeterminate progress: a soft segment gliding along a slim track. The animation runs only while
/// the element is visible (IsVisible covers loaded + not collapsed), so it costs nothing otherwise.
/// </summary>
public sealed class ShimmerBar : FrameworkElement
{
    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(ShimmerBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ShimmerBar),
        new FrameworkPropertyMetadata(LevelBar.DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarThicknessProperty = DependencyProperty.Register(
        nameof(BarThickness), typeof(double), typeof(ShimmerBar),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(ShimmerBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ShimmerBar()
    {
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double BarThickness { get => (double)GetValue(BarThicknessProperty); set => SetValue(BarThicknessProperty, value); }

    private void UpdateAnimation()
    {
        if (IsVisible)
        {
            var animation = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromSeconds(1.4)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            BeginAnimation(PhaseProperty, animation);
        }
        else
        {
            BeginAnimation(PhaseProperty, null);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Math.Max(0, BarThickness));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        var th = Math.Max(1, Math.Min(BarThickness, Math.Max(1, ActualHeight)));
        var y = (ActualHeight - th) / 2;
        var r = th / 2;
        var track = new Rect(0, y, w, th);
        dc.DrawRoundedRectangle(TrackBrush, null, track, r, r);

        var segment = Math.Max(th * 4, w * 0.32);
        var phase = (double)GetValue(PhaseProperty);
        var x = -segment + phase * (w + segment);
        dc.PushClip(new RectangleGeometry(track, r, r));
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(x, y, segment, th), r, r);
        dc.Pop();
    }
}
