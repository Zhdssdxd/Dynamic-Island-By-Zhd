using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WindowsDynamicIsland.Animation;

/// <summary>
/// Short, GPU-friendly transitions built on WPF <see cref="DoubleAnimation"/>s with easing. Only
/// <see cref="UIElement.Opacity"/>, <see cref="ScaleTransform"/> and <see cref="TranslateTransform"/> are animated
/// (render-thread composited, no layout passes).
/// <para>
/// Durations are divided by <see cref="AnimationEngine.SpeedMultiplier"/>. A new transition on the same element
/// and property replaces the running one smoothly (<see cref="HandoffBehavior.SnapshotAndReplace"/>).
/// When an animation finishes, its final value is committed as the property's local value and the animation is
/// removed, so later plain assignments (<c>element.Opacity = 1</c>) work as expected.
/// </para>
/// <para>Calls from non-UI threads are marshalled to the element's dispatcher.</para>
/// </summary>
public static class Transitions
{
    /// <summary>Default duration of <see cref="FadeInAsync"/>.</summary>
    public static readonly TimeSpan DefaultInDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>Default duration of <see cref="FadeOutAsync"/>.</summary>
    public static readonly TimeSpan DefaultOutDuration = TimeSpan.FromMilliseconds(160);

    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction PulseUpEase = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction PulseDownEase = Frozen(new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction ShakeEase = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static readonly double[] ShakeOffsets = { -9, 8, -6, 4.5, -3, 1.5, 0 };

    private static readonly ConditionalWeakTable<UIElement, ElementState> States = new();

    private enum Channel
    {
        Opacity = 0,
        ScaleX = 1,
        ScaleY = 2,
        TranslateX = 3,
        TranslateY = 4,
    }

    /// <summary>
    /// Fades the element in (opacity → 1) while scaling from <paramref name="fromScale"/> to 1 and moving from
    /// <paramref name="fromOffsetY"/> to 0 (cubic ease-out). If a fade on this element is in flight (e.g. a fade-out),
    /// it continues from the current values instead of jumping. The element's <c>Visibility</c> is not touched.
    /// </summary>
    /// <param name="element">Element to show.</param>
    /// <param name="duration">Duration before speed scaling; default 220 ms.</param>
    /// <param name="fromScale">Start scale (uniform), default 0.94.</param>
    /// <param name="fromOffsetY">Start vertical offset in DIPs (positive = below), default 0.</param>
    /// <param name="delay">Optional start delay (speed-scaled). During the delay the element stays transparent.</param>
    /// <returns>Completes when the animation finishes or is superseded by another transition.</returns>
    public static Task FadeInAsync(UIElement element, TimeSpan? duration = null, double fromScale = 0.94,
                                   double fromOffsetY = 0, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!element.Dispatcher.CheckAccess())
        {
            return element.Dispatcher
                .InvokeAsync(() => FadeInAsync(element, duration, fromScale, fromOffsetY, delay))
                .Task.Unwrap();
        }

        var d = ScaleDuration(duration ?? DefaultInDuration);
        var begin = delay.HasValue ? ScaleDuration(delay.Value) : TimeSpan.Zero;
        var (scale, translate) = EnsureTransforms(element);
        var state = States.GetValue(element, static _ => new ElementState());

        if (state.Pending[(int)Channel.Opacity] is null)
        {
            // Fresh appearance: establish the start pose as base values (also visible during the delay).
            Commit(element, UIElement.OpacityProperty, 0.0);
            Commit(scale, ScaleTransform.ScaleXProperty, fromScale);
            Commit(scale, ScaleTransform.ScaleYProperty, fromScale);
            Commit(translate, TranslateTransform.YProperty, fromOffsetY);
        }

        return Task.WhenAll(
            Run(element, Channel.Opacity, element, UIElement.OpacityProperty, Create(1.0, d, begin, EaseOut), 1.0),
            Run(element, Channel.ScaleX, scale, ScaleTransform.ScaleXProperty, Create(1.0, d, begin, EaseOut), 1.0),
            Run(element, Channel.ScaleY, scale, ScaleTransform.ScaleYProperty, Create(1.0, d, begin, EaseOut), 1.0),
            Run(element, Channel.TranslateY, translate, TranslateTransform.YProperty, Create(0.0, d, begin, EaseOut), 0.0));
    }

    /// <summary>
    /// Fades the element out (opacity → 0) while scaling to <paramref name="toScale"/> and moving to
    /// <paramref name="toOffsetY"/> (cubic ease-in), starting from the current values. Afterwards Opacity stays 0
    /// (as a plain local value) and scale/offset are reset to identity, so a later <see cref="FadeInAsync"/> or a
    /// manual <c>Opacity = 1</c> works. The element's <c>Visibility</c> is not touched.
    /// </summary>
    /// <param name="element">Element to hide.</param>
    /// <param name="duration">Duration before speed scaling; default 160 ms.</param>
    /// <param name="toScale">End scale (uniform), default 0.94.</param>
    /// <param name="toOffsetY">End vertical offset in DIPs, default 0.</param>
    /// <returns>Completes when the animation finishes or is superseded by another transition.</returns>
    public static Task FadeOutAsync(UIElement element, TimeSpan? duration = null, double toScale = 0.94,
                                    double toOffsetY = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!element.Dispatcher.CheckAccess())
        {
            return element.Dispatcher
                .InvokeAsync(() => FadeOutAsync(element, duration, toScale, toOffsetY))
                .Task.Unwrap();
        }

        var d = ScaleDuration(duration ?? DefaultOutDuration);
        var (scale, translate) = EnsureTransforms(element);

        return Task.WhenAll(
            Run(element, Channel.Opacity, element, UIElement.OpacityProperty, Create(0.0, d, TimeSpan.Zero, EaseIn), 0.0),
            Run(element, Channel.ScaleX, scale, ScaleTransform.ScaleXProperty, Create(toScale, d, TimeSpan.Zero, EaseIn), 1.0),
            Run(element, Channel.ScaleY, scale, ScaleTransform.ScaleYProperty, Create(toScale, d, TimeSpan.Zero, EaseIn), 1.0),
            Run(element, Channel.TranslateY, translate, TranslateTransform.YProperty, Create(toOffsetY, d, TimeSpan.Zero, EaseIn), 0.0));
    }

    /// <summary>
    /// Quick "tap" feedback: scales up to <paramref name="scale"/> and springs back to 1 with a slight undershoot
    /// (~340 ms at normal speed).
    /// </summary>
    public static void Pulse(UIElement element, double scale = 1.06)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!element.Dispatcher.CheckAccess())
        {
            element.Dispatcher.InvokeAsync(() => Pulse(element, scale));
            return;
        }

        var (s, _) = EnsureTransforms(element);
        var total = ScaleDuration(TimeSpan.FromMilliseconds(340));
        var up = ScaleDuration(TimeSpan.FromMilliseconds(110));
        _ = Run(element, Channel.ScaleX, s, ScaleTransform.ScaleXProperty, CreatePulse(scale, up, total), 1.0);
        _ = Run(element, Channel.ScaleY, s, ScaleTransform.ScaleYProperty, CreatePulse(scale, up, total), 1.0);
    }

    /// <summary>"Attention" feedback (e.g. a finished timer): damped horizontal shake, ~400 ms at normal speed.</summary>
    public static void Shake(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!element.Dispatcher.CheckAccess())
        {
            element.Dispatcher.InvokeAsync(() => Shake(element));
            return;
        }

        var (_, translate) = EnsureTransforms(element);
        var total = ScaleDuration(TimeSpan.FromMilliseconds(400));
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(total),
            FillBehavior = FillBehavior.HoldEnd,
        };
        for (var i = 0; i < ShakeOffsets.Length; i++)
        {
            var time = TimeSpan.FromTicks(total.Ticks * (i + 1) / ShakeOffsets.Length);
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(ShakeOffsets[i], KeyTime.FromTimeSpan(time), ShakeEase));
        }
        _ = Run(element, Channel.TranslateX, translate, TranslateTransform.XProperty, animation, 0.0);
    }

    /// <summary>
    /// Fades the elements in one after another, each delayed by <paramref name="step"/> (speed-scaled) relative to
    /// the previous one, with a small upward motion. Completes when all have finished.
    /// </summary>
    public static Task StaggerInAsync(IEnumerable<UIElement> elements, TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var list = elements.Where(e => e is not null).ToList();
        if (list.Count == 0) return Task.CompletedTask;
        if (step < TimeSpan.Zero) step = TimeSpan.Zero;

        var tasks = new Task[list.Count];
        for (var i = 0; i < list.Count; i++)
            tasks[i] = FadeInAsync(list[i], fromOffsetY: 6, delay: TimeSpan.FromTicks(step.Ticks * i));
        return Task.WhenAll(tasks);
    }

    /// <summary>
    /// Makes sure the element has <c>RenderTransformOrigin = (0.5, 0.5)</c> and a mutable
    /// <see cref="TransformGroup"/> containing a <see cref="ScaleTransform"/> and a <see cref="TranslateTransform"/>.
    /// Existing (unfrozen) transforms are reused; frozen ones (e.g. from styles) are cloned; any other existing
    /// non-identity transform is kept inside the group. Must be called on the element's UI thread.
    /// </summary>
    /// <returns>The scale and translate transforms to animate.</returns>
    public static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransforms(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        Transform? existing = element.RenderTransform;
        TransformGroup group;
        if (existing is TransformGroup existingGroup)
        {
            group = existingGroup.IsFrozen ? existingGroup.Clone() : existingGroup;
        }
        else
        {
            group = new TransformGroup();
            if (existing is not null
                && (existing is ScaleTransform || existing is TranslateTransform || !existing.Value.IsIdentity))
            {
                group.Children.Add(existing.IsFrozen ? existing.Clone() : existing);
            }
        }

        ScaleTransform? scale = null;
        TranslateTransform? translate = null;
        foreach (var child in group.Children)
        {
            if (scale is null && child is ScaleTransform s && !s.IsFrozen) scale = s;
            else if (translate is null && child is TranslateTransform t && !t.IsFrozen) translate = t;
        }

        if (scale is null)
        {
            scale = new ScaleTransform(1.0, 1.0);
            group.Children.Add(scale);
        }
        if (translate is null)
        {
            translate = new TranslateTransform(0.0, 0.0);
            group.Children.Add(translate);
        }

        if (!ReferenceEquals(element.RenderTransform, group)) element.RenderTransform = group;
        return (scale, translate);
    }

    /// <summary>
    /// Sets <paramref name="value"/> as the local (base) value and removes any running animation on the property,
    /// so the value is final and visible immediately.
    /// </summary>
    internal static void Commit<T>(T target, DependencyProperty property, double value)
        where T : DependencyObject, IAnimatable
    {
        target.SetValue(property, value);
        target.BeginAnimation(property, null!);
    }

    /// <summary>Divides a duration by <see cref="AnimationEngine.SpeedMultiplier"/>; negative durations become zero.</summary>
    internal static TimeSpan ScaleDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return TimeSpan.Zero;
        return TimeSpan.FromTicks((long)(duration.Ticks / AnimationEngine.Instance.SpeedMultiplier));
    }

    private static Task Run<T>(UIElement owner, Channel channel, T target, DependencyProperty property,
                               AnimationTimeline timeline, double commitValue)
        where T : DependencyObject, IAnimatable
    {
        var state = States.GetValue(owner, static _ => new ElementState());
        var index = (int)channel;
        var version = ++state.Versions[index];

        // Whatever was running on this channel is superseded: release its awaiters.
        state.Pending[index]?.TrySetResult();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Pending[index] = tcs;

        timeline.Completed += (_, _) =>
        {
            if (state.Versions[index] == version)
            {
                state.Pending[index] = null;
                Commit(target, property, commitValue);
            }
            tcs.TrySetResult();
        };

        target.BeginAnimation(property, timeline, HandoffBehavior.SnapshotAndReplace);
        return tcs.Task;
    }

    private static DoubleAnimation Create(double to, TimeSpan duration, TimeSpan beginTime, IEasingFunction ease)
        => new(to, new Duration(duration))
        {
            BeginTime = beginTime,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

    private static DoubleAnimationUsingKeyFrames CreatePulse(double peak, TimeSpan up, TimeSpan total)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(total),
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(up), PulseUpEase));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(total), PulseDownEase));
        return animation;
    }

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    private sealed class ElementState
    {
        public readonly int[] Versions = new int[5];
        public readonly TaskCompletionSource?[] Pending = new TaskCompletionSource?[5];
    }
}
