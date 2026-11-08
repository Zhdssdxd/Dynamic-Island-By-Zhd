using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowsDynamicIsland.Animation;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>
/// Rounded artwork (album art, video thumbnail, app icon) drawn directly with a DrawingContext:
/// uniform-to-fill crop, rounded clip, glyph fallback when there is no image, cross-fade on source change,
/// and an optional "paused" shrink (0.92, spring) like iOS.
/// </summary>
public sealed class ArtworkImage : FrameworkElement
{
    private static readonly Brush DefaultFallbackBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    private static readonly Brush DefaultGlyphBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9F)));
    private static readonly FontFamily FallbackIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Duration CrossfadeDuration = new(TimeSpan.FromMilliseconds(260));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Corner radius used for the glyph fallback; NaN = same as <see cref="CornerRadius"/>.</summary>
    public static readonly DependencyProperty FallbackCornerRadiusProperty = DependencyProperty.Register(
        nameof(FallbackCornerRadius), typeof(double), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph), typeof(string), typeof(ArtworkImage),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FallbackBackgroundProperty = DependencyProperty.Register(
        nameof(FallbackBackground), typeof(Brush), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(DefaultFallbackBackground, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(DefaultGlyphBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Glyph em size; NaN = 45 % of the smaller side.</summary>
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(Stretch.UniformToFill, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When true the artwork shrinks to 0.92 (Smooth spring); false → back to 1.0 (Bouncy).</summary>
    public static readonly DependencyProperty IsPausedProperty = DependencyProperty.Register(
        nameof(IsPaused), typeof(bool), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(false, OnIsPausedChanged));

    private static readonly DependencyProperty CrossfadeProperty = DependencyProperty.Register(
        "Crossfade", typeof(double), typeof(ArtworkImage),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double PausedScale = 0.92;

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly object _scaleKey = new();
    private ImageSource? _previous;
    private bool _hasPrevious;

    public ArtworkImage()
    {
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SnapsToDevicePixels = false;
        Loaded += (_, _) => SnapScale();
        Unloaded += (_, _) => AnimationEngine.Instance.Stop(_scaleKey);
    }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double FallbackCornerRadius { get => (double)GetValue(FallbackCornerRadiusProperty); set => SetValue(FallbackCornerRadiusProperty, value); }
    public string FallbackGlyph { get => (string)GetValue(FallbackGlyphProperty); set => SetValue(FallbackGlyphProperty, value); }
    public Brush FallbackBackground { get => (Brush)GetValue(FallbackBackgroundProperty); set => SetValue(FallbackBackgroundProperty, value); }
    public Brush GlyphBrush { get => (Brush)GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }
    public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool IsPaused { get => (bool)GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ArtworkImage)d;
        if (!self.IsLoaded || ReferenceEquals(e.OldValue, e.NewValue)) return;

        // Cross-fade from the old artwork (or the fallback) to the new one.
        self._previous = e.OldValue as ImageSource;
        self._hasPrevious = true;
        var animation = new DoubleAnimation(0.0, 1.0, CrossfadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            self._previous = null;
            self._hasPrevious = false;
            self.InvalidateVisual();
        };
        self.BeginAnimation(CrossfadeProperty, animation);
    }

    private static void OnIsPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ArtworkImage)d;
        if (!self.IsLoaded)
        {
            self.SnapScale();
            return;
        }

        var paused = (bool)e.NewValue;
        var from = self._scale.ScaleX;
        AnimationEngine.Instance.Animate(self._scaleKey, from, paused ? PausedScale : 1.0,
            v =>
            {
                self._scale.ScaleX = v;
                self._scale.ScaleY = v;
            },
            paused ? SpringPresets.Smooth : SpringPresets.Bouncy);
    }

    private void SnapScale()
    {
        AnimationEngine.Instance.Stop(_scaleKey);
        var s = IsPaused ? PausedScale : 1.0;
        _scale.ScaleX = s;
        _scale.ScaleY = s;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var bounds = new Rect(0, 0, w, h);

        var t = Math.Clamp((double)GetValue(CrossfadeProperty), 0, 1);
        if (_hasPrevious && t < 1)
        {
            dc.PushOpacity(1 - t);
            DrawLayer(dc, _previous, bounds);
            dc.Pop();
            dc.PushOpacity(t);
            DrawLayer(dc, Source, bounds);
            dc.Pop();
        }
        else
        {
            DrawLayer(dc, Source, bounds);
        }
    }

    private void DrawLayer(DrawingContext dc, ImageSource? source, Rect bounds)
    {
        var maxRadius = Math.Min(bounds.Width, bounds.Height) / 2;
        if (source is not null && source.Width > 0 && source.Height > 0)
        {
            var r = Math.Clamp(CornerRadius, 0, maxRadius);
            dc.PushClip(new RectangleGeometry(bounds, r, r));
            dc.DrawImage(source, ComputeImageRect(source, bounds));
            dc.Pop();
            return;
        }

        var fr = double.IsNaN(FallbackCornerRadius) ? CornerRadius : FallbackCornerRadius;
        fr = Math.Clamp(fr, 0, maxRadius);
        dc.DrawRoundedRectangle(FallbackBackground, null, bounds, fr, fr);

        var glyph = FallbackGlyph;
        if (string.IsNullOrEmpty(glyph)) return;
        var size = double.IsNaN(GlyphSize) ? Math.Max(6, Math.Min(bounds.Width, bounds.Height) * 0.45) : GlyphSize;
        var font = TryFindResource("IconFont") as FontFamily ?? FallbackIconFont;
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, GlyphBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(bounds.X + (bounds.Width - ft.Width) / 2, bounds.Y + (bounds.Height - ft.Height) / 2));
    }

    private Rect ComputeImageRect(ImageSource source, Rect bounds)
    {
        var iw = source.Width;
        var ih = source.Height;
        double scale = Stretch switch
        {
            Stretch.Uniform => Math.Min(bounds.Width / iw, bounds.Height / ih),
            Stretch.Fill => double.NaN,
            Stretch.None => 1.0,
            _ => Math.Max(bounds.Width / iw, bounds.Height / ih),
        };
        if (double.IsNaN(scale)) return bounds;
        var w = iw * scale;
        var h = ih * scale;
        return new Rect(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
