using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace WindowsDynamicIsland.Animation;

/// <summary>How <see cref="MorphContentControl"/> swaps old and new content.</summary>
public enum ContentTransition
{
    /// <summary>Plain cross-fade.</summary>
    Fade,

    /// <summary>Cross-fade with the new content growing from 0.92 and the old one shrinking to 0.92 (default).</summary>
    ScaleFade,

    /// <summary>New content rises from 10 DIPs below, old content leaves upwards; with fade.</summary>
    SlideUp,

    /// <summary>New content drops from 10 DIPs above, old content leaves downwards; with fade.</summary>
    SlideDown,

    /// <summary>Cross-fade with a Gaussian blur (radius 8 → 0); the effect is removed when done.</summary>
    Blur,
}

/// <summary>
/// A <see cref="ContentControl"/> that cross-morphs between contents like iOS: when <see cref="ContentControl.Content"/>
/// or <see cref="TransitionKey"/> changes, the old visual animates out while the new one animates in, overlapping.
/// <para>
/// <see cref="ContentControl.ContentTemplate"/>, <see cref="ContentControl.ContentTemplateSelector"/>,
/// <see cref="ContentControl.ContentStringFormat"/> and the content alignments apply to both internal presenters.
/// Changing <see cref="TransitionKey"/> while Content stays the same re-instantiates the template (re-runs the
/// selector) and morphs between the two instances — useful when a view model's state changes.
/// </para>
/// <para>
/// Content shown before the control is loaded (or while it is invisible) appears without animation. Rapid changes
/// never stack: at most two presenters exist; an interrupted transition reverses smoothly if the old content comes back.
/// Layout size follows the incoming content immediately; the outgoing content keeps its own size while it fades.
/// Do not override <see cref="Control.Template"/> (a custom template must contain a <see cref="Decorator"/> named
/// <c>PART_Root</c>). Prefer data content with DataTemplates; a UIElement content must not be reused elsewhere while
/// it is still animating out.
/// </para>
/// </summary>
public class MorphContentControl : ContentControl
{
    private const string RootPartName = "PART_Root";
    private const double ScaleDelta = 0.92;
    private const double SlideDistance = 10.0;
    private const double BlurRadius = 8.0;
    private const double OutDurationFraction = 0.6;

    /// <summary>Identifies <see cref="TransitionKey"/>. Default null.</summary>
    public static readonly DependencyProperty TransitionKeyProperty = DependencyProperty.Register(
        nameof(TransitionKey), typeof(object), typeof(MorphContentControl),
        new PropertyMetadata(null, OnTransitionKeyChanged));

    /// <summary>Identifies <see cref="Transition"/>. Default <see cref="ContentTransition.ScaleFade"/>.</summary>
    public static readonly DependencyProperty TransitionProperty = DependencyProperty.Register(
        nameof(Transition), typeof(ContentTransition), typeof(MorphContentControl),
        new PropertyMetadata(ContentTransition.ScaleFade));

    /// <summary>Identifies <see cref="Duration"/>. Default 260 ms (divided by <see cref="AnimationEngine.SpeedMultiplier"/>).</summary>
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(TimeSpan), typeof(MorphContentControl),
        new PropertyMetadata(TimeSpan.FromMilliseconds(260)));

    private static readonly ControlTemplate DefaultTemplate = CreateDefaultTemplate();
    private static readonly IEasingFunction EnterEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction ExitEase = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    private readonly MorphHost _host;
    private Slot _current;
    private Slot _previous;
    private Decorator? _root;
    private bool _pendingContentChange;
    private bool _pendingKeyChange;
    private bool _operationQueued;

    static MorphContentControl()
    {
        // A content host, not an interactive control: keep it out of keyboard focus / tab order.
        FocusableProperty.OverrideMetadata(typeof(MorphContentControl), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(MorphContentControl), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Creates the control with its built-in two-presenter template.</summary>
    public MorphContentControl()
    {
        _host = new MorphHost();
        _current = CreateSlot();
        _previous = CreateSlot();
        _host.Children.Add(_current.Presenter);
        _host.Children.Add(_previous.Presenter);
        MakeCurrent(_current, _previous);

        Template = DefaultTemplate;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Any value; when it changes (by <see cref="object.Equals(object, object)"/>) the content is re-instantiated and
    /// morphed even if <see cref="ContentControl.Content"/> is the same object.
    /// </summary>
    public object? TransitionKey
    {
        get => GetValue(TransitionKeyProperty);
        set => SetValue(TransitionKeyProperty, value);
    }

    /// <summary>Transition style. Default <see cref="ContentTransition.ScaleFade"/>.</summary>
    public ContentTransition Transition
    {
        get => (ContentTransition)GetValue(TransitionProperty);
        set => SetValue(TransitionProperty, value);
    }

    /// <summary>
    /// Duration of the incoming animation (the outgoing one takes 60 % of it), before division by
    /// <see cref="AnimationEngine.SpeedMultiplier"/>. Zero disables animation. Default 260 ms.
    /// </summary>
    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_root is not null && ReferenceEquals(_root.Child, _host)) _root.Child = null;
        _root = GetTemplateChild(RootPartName) as Decorator;
        if (_root is not null) _root.Child = _host;
    }

    /// <inheritdoc />
    protected override void OnContentChanged(object? oldContent, object? newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        RequestTransition(contentChanged: true, keyChanged: false);
    }

    private static void OnTransitionKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (Equals(e.OldValue, e.NewValue)) return;
        ((MorphContentControl)d).RequestTransition(contentChanged: false, keyChanged: true);
    }

    // ───────────────────────── transition scheduling ─────────────────────────

    private void RequestTransition(bool contentChanged, bool keyChanged)
    {
        _pendingContentChange |= contentChanged;
        _pendingKeyChange |= keyChanged;

        if (!IsLoaded)
        {
            // Not on screen: apply immediately, no animation (first content appears instantly).
            ProcessPending();
            return;
        }

        // Coalesce Content + TransitionKey changes made in the same pass (e.g. two bindings updating together)
        // into one transition. DataBind priority runs before layout/render, so no stale frame is shown.
        if (_operationQueued) return;
        _operationQueued = true;
        Dispatcher.InvokeAsync(ProcessPending, DispatcherPriority.DataBind);
    }

    private void ProcessPending()
    {
        _operationQueued = false;
        var contentChanged = _pendingContentChange;
        var keyChanged = _pendingKeyChange;
        _pendingContentChange = false;
        _pendingKeyChange = false;
        if (!contentChanged && !keyChanged) return;

        var animate = TryGetDuration(out var duration);
        RunTransition(Content, rebuild: keyChanged, animate, duration);
    }

    private bool TryGetDuration(out TimeSpan duration)
    {
        duration = Transitions.ScaleDuration(Duration);
        return IsLoaded && IsVisible && duration.TotalMilliseconds >= 1.0;
    }

    private void RunTransition(object? content, bool rebuild, bool animate, TimeSpan duration)
    {
        var outgoing = _current;
        var other = _previous;
        var inCurrent = Equals(outgoing.Presenter.Content, content);
        var inOther = Equals(other.Presenter.Content, content);

        if (inCurrent && !rebuild)
        {
            if (!animate)
            {
                FinishSlot(other, clearContent: true);
                FinishSlot(outgoing, clearContent: false);
            }
            return;
        }

        if (!animate)
        {
            // Keep a visual where it already lives (a Visual can't have two parents).
            var keep = inOther && !inCurrent ? other : outgoing;
            var drop = ReferenceEquals(keep, other) ? outgoing : other;
            FinishSlot(drop, clearContent: true);
            FinishSlot(keep, clearContent: false);
            if (rebuild || !Equals(keep.Presenter.Content, content))
            {
                keep.Presenter.Content = null;
                keep.Presenter.Content = content;
            }
            MakeCurrent(keep, drop);
            return;
        }

        var kind = Transition;

        if (inCurrent && content is Visual)
        {
            // Rebuild requested for a UIElement that is already displayed: it cannot be shown twice,
            // so replay the entrance on the current presenter.
            FinishSlot(other, clearContent: true);
            AnimateIn(outgoing, fresh: true, kind, duration);
            return;
        }

        // Resume (reverse) when the requested content is the one currently animating out.
        var resume = inOther && !rebuild && other.IsLeaving;
        if (!resume)
        {
            FinishSlot(other, clearContent: true);
            other.Presenter.Content = content;
        }

        MakeCurrent(other, outgoing);
        AnimateOut(outgoing, kind, TimeSpan.FromTicks((long)(duration.Ticks * OutDurationFraction)));
        AnimateIn(other, fresh: !resume, kind, duration);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        FinishSlot(_previous, clearContent: true);
        FinishSlot(_current, clearContent: false);
    }

    // ───────────────────────── animations ─────────────────────────

    private void AnimateIn(Slot slot, bool fresh, ContentTransition kind, TimeSpan duration)
    {
        var version = ++slot.Version;
        slot.IsLeaving = false;
        var presenter = slot.Presenter;
        presenter.IsHitTestVisible = true;

        GetPose(kind, entering: true, out var fromScale, out var fromOffsetY, out var fromBlur);
        var blur = PrepareBlur(presenter, kind == ContentTransition.Blur, fresh ? fromBlur : (double?)null);

        if (fresh)
        {
            Transitions.Commit(presenter, OpacityProperty, 0.0);
            Transitions.Commit(slot.Scale, ScaleTransform.ScaleXProperty, fromScale);
            Transitions.Commit(slot.Scale, ScaleTransform.ScaleYProperty, fromScale);
            Transitions.Commit(slot.Translate, TranslateTransform.XProperty, 0.0);
            Transitions.Commit(slot.Translate, TranslateTransform.YProperty, fromOffsetY);
        }

        var opacity = CreateAnimation(1.0, duration, EnterEase);
        opacity.Completed += (_, _) =>
        {
            if (slot.Version == version) FinishSlot(slot, clearContent: false);
        };

        presenter.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
        slot.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(1.0, duration, EnterEase), HandoffBehavior.SnapshotAndReplace);
        slot.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(1.0, duration, EnterEase), HandoffBehavior.SnapshotAndReplace);
        slot.Translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(0.0, duration, EnterEase), HandoffBehavior.SnapshotAndReplace);
        blur?.BeginAnimation(BlurEffect.RadiusProperty, CreateAnimation(0.0, duration, EnterEase), HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateOut(Slot slot, ContentTransition kind, TimeSpan duration)
    {
        var version = ++slot.Version;
        slot.IsLeaving = true;
        var presenter = slot.Presenter;
        presenter.IsHitTestVisible = false;

        GetPose(kind, entering: false, out var toScale, out var toOffsetY, out var toBlur);
        var blur = PrepareBlur(presenter, kind == ContentTransition.Blur, null);

        var opacity = CreateAnimation(0.0, duration, ExitEase);
        opacity.Completed += (_, _) =>
        {
            if (slot.Version == version) FinishSlot(slot, clearContent: true);
        };

        presenter.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
        slot.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateAnimation(toScale, duration, ExitEase), HandoffBehavior.SnapshotAndReplace);
        slot.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateAnimation(toScale, duration, ExitEase), HandoffBehavior.SnapshotAndReplace);
        slot.Translate.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(toOffsetY, duration, ExitEase), HandoffBehavior.SnapshotAndReplace);
        blur?.BeginAnimation(BlurEffect.RadiusProperty, CreateAnimation(toBlur, duration, ExitEase), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops all animations on the slot, commits the identity pose and optionally drops its content.</summary>
    private static void FinishSlot(Slot slot, bool clearContent)
    {
        slot.Version++;
        slot.IsLeaving = false;
        var presenter = slot.Presenter;

        Transitions.Commit(presenter, OpacityProperty, 1.0);
        Transitions.Commit(slot.Scale, ScaleTransform.ScaleXProperty, 1.0);
        Transitions.Commit(slot.Scale, ScaleTransform.ScaleYProperty, 1.0);
        Transitions.Commit(slot.Translate, TranslateTransform.XProperty, 0.0);
        Transitions.Commit(slot.Translate, TranslateTransform.YProperty, 0.0);
        RemoveBlur(presenter);
        presenter.IsHitTestVisible = true;

        if (clearContent && presenter.Content is not null) presenter.Content = null;
    }

    private static BlurEffect? PrepareBlur(ContentPresenter presenter, bool useBlur, double? startRadius)
    {
        if (!useBlur)
        {
            RemoveBlur(presenter);
            return null;
        }

        if (presenter.Effect is BlurEffect existing && !existing.IsFrozen)
        {
            if (startRadius.HasValue) Transitions.Commit(existing, BlurEffect.RadiusProperty, startRadius.Value);
            return existing;
        }

        var blur = new BlurEffect
        {
            Radius = startRadius ?? 0.0,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance,
        };
        presenter.Effect = blur;
        return blur;
    }

    private static void RemoveBlur(ContentPresenter presenter)
    {
        if (presenter.Effect is null) return;
        if (presenter.Effect is BlurEffect blur && !blur.IsFrozen) blur.BeginAnimation(BlurEffect.RadiusProperty, null!);
        presenter.Effect = null;
    }

    private static void GetPose(ContentTransition kind, bool entering, out double scale, out double offsetY, out double blur)
    {
        scale = 1.0;
        offsetY = 0.0;
        blur = 0.0;
        switch (kind)
        {
            case ContentTransition.ScaleFade:
                scale = ScaleDelta;
                break;
            case ContentTransition.SlideUp:
                offsetY = entering ? SlideDistance : -SlideDistance;
                break;
            case ContentTransition.SlideDown:
                offsetY = entering ? -SlideDistance : SlideDistance;
                break;
            case ContentTransition.Blur:
                blur = BlurRadius;
                break;
        }
    }

    private static DoubleAnimation CreateAnimation(double to, TimeSpan duration, IEasingFunction ease)
        => new(to, new Duration(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };

    // ───────────────────────── structure ─────────────────────────

    private Slot CreateSlot()
    {
        var presenter = new ContentPresenter();
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty,
            new Binding(nameof(ContentTemplate)) { Source = this });
        presenter.SetBinding(ContentPresenter.ContentTemplateSelectorProperty,
            new Binding(nameof(ContentTemplateSelector)) { Source = this });
        presenter.SetBinding(ContentPresenter.ContentStringFormatProperty,
            new Binding(nameof(ContentStringFormat)) { Source = this });
        presenter.SetBinding(FrameworkElement.HorizontalAlignmentProperty,
            new Binding(nameof(HorizontalContentAlignment)) { Source = this });
        presenter.SetBinding(FrameworkElement.VerticalAlignmentProperty,
            new Binding(nameof(VerticalContentAlignment)) { Source = this });

        var (scale, translate) = Transitions.EnsureTransforms(presenter);
        return new Slot(presenter, scale, translate);
    }

    private void MakeCurrent(Slot current, Slot previous)
    {
        _current = current;
        _previous = previous;
        Panel.SetZIndex(current.Presenter, 1);
        Panel.SetZIndex(previous.Presenter, 0);
        _host.Primary = current.Presenter;
    }

    private static ControlTemplate CreateDefaultTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border), RootPartName);
        root.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        root.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        root.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        root.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        root.SetValue(UIElement.SnapsToDevicePixelsProperty, new TemplateBindingExtension(SnapsToDevicePixelsProperty));

        var template = new ControlTemplate(typeof(MorphContentControl)) { VisualTree = root };
        template.Seal();
        return template;
    }

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    private sealed class Slot
    {
        public Slot(ContentPresenter presenter, ScaleTransform scale, TranslateTransform translate)
        {
            Presenter = presenter;
            Scale = scale;
            Translate = translate;
        }

        public ContentPresenter Presenter { get; }
        public ScaleTransform Scale { get; }
        public TranslateTransform Translate { get; }
        public int Version { get; set; }
        public bool IsLeaving { get; set; }
    }

    /// <summary>
    /// Overlapping panel: its desired size is the primary (incoming) child's, so layout follows the new content
    /// at once; other (outgoing) children are arranged at no less than their own desired size, aligned per their
    /// alignment, so they do not re-layout or get clipped while fading away.
    /// </summary>
    private sealed class MorphHost : Panel
    {
        private UIElement? _primary;

        public UIElement? Primary
        {
            get => _primary;
            set
            {
                if (ReferenceEquals(_primary, value)) return;
                _primary = value;
                InvalidateMeasure();
                InvalidateArrange();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var max = new Size();
            Size? primary = null;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(availableSize);
                var desired = child.DesiredSize;
                if (ReferenceEquals(child, _primary)) primary = desired;
                max = new Size(Math.Max(max.Width, desired.Width), Math.Max(max.Height, desired.Height));
            }
            return primary ?? max;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in InternalChildren)
            {
                if (ReferenceEquals(child, _primary))
                {
                    child.Arrange(new Rect(finalSize));
                    continue;
                }

                var desired = child.DesiredSize;
                var width = Math.Max(finalSize.Width, desired.Width);
                var height = Math.Max(finalSize.Height, desired.Height);
                var x = child is FrameworkElement fe ? AlignOffset(fe.HorizontalAlignment, finalSize.Width, width) : (finalSize.Width - width) / 2;
                var y = child is FrameworkElement fy ? AlignOffset(fy.VerticalAlignment, finalSize.Height, height) : (finalSize.Height - height) / 2;
                child.Arrange(new Rect(x, y, width, height));
            }
            return finalSize;
        }

        private static double AlignOffset(HorizontalAlignment alignment, double available, double size) => alignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => available - size,
            _ => (available - size) / 2,
        };

        private static double AlignOffset(VerticalAlignment alignment, double available, double size) => alignment switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Bottom => available - size,
            _ => (available - size) / 2,
        };
    }
}
