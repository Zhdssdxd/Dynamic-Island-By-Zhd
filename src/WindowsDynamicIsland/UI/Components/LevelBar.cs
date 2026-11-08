using System.Windows;
using System.Windows.Media;
using WindowsDynamicIsland.Animation;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>
/// Slim rounded level bar (volume, brightness, battery, timer, download). Value changes are spring-animated
/// (Snappy) once the control is loaded, so dragging the system volume slider looks fluid.
/// </summary>
public sealed class LevelBar : FrameworkElement
{
    internal static readonly Brush DefaultTrack = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LevelBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelBar),
        new FrameworkPropertyMetadata(DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarThicknessProperty = DependencyProperty.Register(
        nameof(BarThickness), typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Animate value changes with a spring (default true).</summary>
    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated), typeof(bool), typeof(LevelBar), new FrameworkPropertyMetadata(true));

    internal static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public LevelBar()
    {
        Loaded += (_, _) => SetValue(DisplayValueProperty, Clamp01(Value));
        Unloaded += (_, _) => AnimationEngine.Instance.Stop(this);
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double BarThickness { get => (double)GetValue(BarThicknessProperty); set => SetValue(BarThicknessProperty, value); }
    public bool IsAnimated { get => (bool)GetValue(IsAnimatedProperty); set => SetValue(IsAnimatedProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (LevelBar)d;
        var target = Clamp01((double)e.NewValue);
        if (self.IsLoaded && self.IsAnimated)
            AnimationEngine.Instance.Animate(self, (double)self.GetValue(DisplayValueProperty), target,
                v => self.SetValue(DisplayValueProperty, v), SpringPresets.Snappy);
        else
            self.SetValue(DisplayValueProperty, target);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(0, Math.Max(0, BarThickness));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        var th = Math.Max(1, Math.Min(BarThickness, Math.Max(1, ActualHeight)));
        var y = (ActualHeight - th) / 2;
        var r = th / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, y, w, th), r, r);

        var v = Clamp01((double)GetValue(DisplayValueProperty));
        if (v <= 0.0005) return;
        var fw = Math.Max(th, w * v);
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, y, Math.Min(fw, w), th), r, r);
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
