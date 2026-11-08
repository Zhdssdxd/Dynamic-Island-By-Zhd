using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Animation;

/// <summary>
/// Drives physics springs on the UI thread. Hooks <see cref="CompositionTarget.Rendering"/> only while at
/// least one spring is active and unhooks as soon as all of them settle, so an idle island costs ~0 % CPU.
/// <para>
/// Every animation is identified by a key. Calling <see cref="Animate"/> again with the same key retargets
/// the running spring and keeps its current value and velocity — that is what makes interrupted
/// transitions (expand while collapsing, etc.) feel natural.
/// </para>
/// <para>
/// All work happens on the UI dispatcher. Calls made from other threads are marshalled asynchronously
/// (the returned <see cref="SpringHandle"/> is valid immediately).
/// </para>
/// </summary>
public sealed class AnimationEngine
{
    private const double MaxFrameSeconds = 0.1;
    private const double FirstFrameSeconds = 1.0 / 60.0;

    private readonly Dictionary<object, ActiveSpring> _springs = new();
    private readonly List<ActiveSpring> _frameBuffer = new();
    private readonly List<Action> _completedBuffer = new();
    private readonly Stopwatch _fallbackClock = Stopwatch.StartNew();

    private Dispatcher? _dispatcher;
    private bool _subscribed;
    private bool _inFrame;
    private bool _hasLastFrame;
    private TimeSpan _lastFrameTime;
    private double _speedMultiplier = 1.0;
    private SpringParameters _defaultParameters = SpringPresets.Smooth;

    private AnimationEngine()
    {
    }

    /// <summary>The process-wide engine.</summary>
    public static AnimationEngine Instance { get; } = new();

    /// <summary>
    /// Global animation speed: 0.5 (slow) … 2 (fast), 1 = normal. Scales the simulated time of all springs
    /// and the durations used by <see cref="Transitions"/> and <see cref="MorphContentControl"/>.
    /// Values are clamped; NaN resets to 1. Safe to set from any thread.
    /// </summary>
    public double SpeedMultiplier
    {
        get => Volatile.Read(ref _speedMultiplier);
        set => Volatile.Write(ref _speedMultiplier, double.IsFinite(value) ? Math.Clamp(value, 0.5, 2.0) : 1.0);
    }

    /// <summary>Parameters used when a call passes <c>parameters: null</c>. Defaults to <see cref="SpringPresets.Smooth"/>.</summary>
    public SpringParameters DefaultParameters
    {
        get => _defaultParameters;
        set => _defaultParameters = value;
    }

    /// <summary>True while the engine is hooked to the render loop (at least one spring is active).</summary>
    public bool IsRunning => _subscribed;

    /// <summary>Number of active springs. Meaningful on the UI thread only.</summary>
    public int ActiveCount => _springs.Count;

    /// <summary>
    /// Animates an arbitrary value with a spring.
    /// If a spring with the same <paramref name="key"/> is running, it is retargeted: <paramref name="from"/> is
    /// ignored, value and velocity are preserved, <paramref name="apply"/>/<paramref name="completed"/> are
    /// replaced and the previous handle becomes completed (its <c>completed</c> callback is NOT invoked).
    /// Otherwise a new spring starts at <paramref name="from"/> and <paramref name="apply"/> is invoked with it right away.
    /// </summary>
    /// <param name="key">Identity of the animation (any object with value equality, e.g. a tuple).</param>
    /// <param name="from">Start value for a new spring. Non-finite values start at <paramref name="to"/>.</param>
    /// <param name="to">Target value. Non-finite targets are rejected (logged, handle returned as cancelled).</param>
    /// <param name="apply">Called on the UI thread each frame with the new value. Exceptions are logged and stop the spring.</param>
    /// <param name="parameters">Spring parameters; null = <see cref="DefaultParameters"/>.</param>
    /// <param name="completed">Called once on the UI thread when the spring settles (not on cancel/retarget).</param>
    public SpringHandle Animate(object key, double from, double to, Action<double> apply,
                                SpringParameters? parameters = null, Action? completed = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(apply);

        var handle = new SpringHandle(this, key, to);
        if (!double.IsFinite(to))
        {
            Log.Warn($"AnimationEngine: non-finite target {to} for key '{key}' ignored.");
            handle.TryFinish(cancelled: true);
            return handle;
        }

        var dispatcher = ResolveDispatcher(null);
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() =>
            {
                if (!handle.IsCompleted) StartOrRetarget(key, from, to, apply, parameters, completed, handle);
            });
            return handle;
        }

        StartOrRetarget(key, from, to, apply, parameters, completed, handle);
        return handle;
    }

    /// <summary>
    /// Animates a <see cref="double"/> dependency property with a spring (applied with <c>SetValue</c>, i.e. as a
    /// local value — it replaces bindings on that property). The start value is the active spring's value for the
    /// same (target, property), otherwise the property's current value; NaN <c>Width</c>/<c>Height</c> of a
    /// <see cref="FrameworkElement"/> start from <c>ActualWidth</c>/<c>ActualHeight</c>, other non-finite values from 0.
    /// A WPF timeline animation running on the property is removed (hand-off from its current value).
    /// Values rejected by the property's validation (e.g. negative Width during overshoot) are clamped to 0 or skipped.
    /// </summary>
    /// <exception cref="ArgumentException">The property is not of type <see cref="double"/>.</exception>
    public SpringHandle AnimateProperty(DependencyObject target, DependencyProperty property, double to,
                                        SpringParameters? parameters = null, Action? completed = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (property.PropertyType != typeof(double))
            throw new ArgumentException($"Property '{property.Name}' is not of type double.", nameof(property));

        var key = new PropertyKey(target, property);
        var handle = new SpringHandle(this, key, to);
        if (!double.IsFinite(to))
        {
            Log.Warn($"AnimationEngine: non-finite target {to} for property '{property.Name}' ignored.");
            handle.TryFinish(cancelled: true);
            return handle;
        }

        _dispatcher ??= target.Dispatcher;
        if (!target.CheckAccess())
        {
            target.Dispatcher.InvokeAsync(() =>
            {
                if (!handle.IsCompleted) StartProperty(key, to, parameters, completed, handle);
            });
            return handle;
        }

        StartProperty(key, to, parameters, completed, handle);
        return handle;
    }

    /// <summary>Stops the spring with this key where it is (no snap, no completed callback).</summary>
    public void Stop(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RunOnUi(null, () =>
        {
            if (_springs.TryGetValue(key, out var active)) Remove(active, cancelled: true);
        });
    }

    /// <summary>Stops a spring started with <see cref="AnimateProperty"/> where it is.</summary>
    public void Stop(DependencyObject target, DependencyProperty property) => Stop(new PropertyKey(target, property));

    /// <summary>
    /// Gets the current value of the active spring with this key. Returns false when no spring is active
    /// or when called off the UI thread.
    /// </summary>
    public bool TryGetCurrent(object key, out double value)
    {
        value = 0;
        if (key is null) return false;
        var dispatcher = ResolveDispatcher(null);
        if (dispatcher is not null && !dispatcher.CheckAccess()) return false;
        if (!_springs.TryGetValue(key, out var active) || active.Handle.IsCompleted) return false;
        value = active.Spring.Value;
        return true;
    }

    /// <summary>Gets the current value of a spring started with <see cref="AnimateProperty"/>.</summary>
    public bool TryGetCurrent(DependencyObject target, DependencyProperty property, out double value)
        => TryGetCurrent(new PropertyKey(target, property), out value);

    internal void CancelHandle(SpringHandle handle)
    {
        if (!handle.TryFinish(cancelled: true)) return;
        RunOnUi(null, () =>
        {
            if (_springs.TryGetValue(handle.Key, out var active) && ReferenceEquals(active.Handle, handle))
                Remove(active, cancelled: true);
        });
    }

    // ───────────────────────── internals ─────────────────────────

    private Dispatcher? ResolveDispatcher(DependencyObject? target)
    {
        var d = _dispatcher;
        if (d is not null) return d;
        d = target?.Dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
        _dispatcher = d;
        return d;
    }

    private void RunOnUi(DependencyObject? target, Action action)
    {
        var dispatcher = ResolveDispatcher(target);
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }

    private void StartProperty(PropertyKey key, double to, SpringParameters? parameters, Action? completed, SpringHandle handle)
    {
        var target = key.Target;
        var property = key.Property;

        var from = _springs.TryGetValue(key, out var active) ? active.Spring.Value : ReadCurrent(target, property);

        // Hand off from a WPF timeline animation: its current value was read above, now remove it so
        // our SetValue (local value) becomes visible.
        if (target is IAnimatable animatable && animatable.HasAnimatedProperties)
            animatable.BeginAnimation(property, null!);

        StartOrRetarget(key, from, to, v => SetDouble(target, property, v), parameters, completed, handle);
    }

    private void StartOrRetarget(object key, double from, double to, Action<double> apply,
                                 SpringParameters? parameters, Action? completed, SpringHandle handle)
    {
        var p = parameters ?? _defaultParameters;

        if (_springs.TryGetValue(key, out var active))
        {
            var previous = active.Handle;
            active.Handle = handle;
            active.Apply = apply;
            active.Completed = completed;
            active.Spring.Parameters = p;
            active.Spring.Target = to;
            if (!ReferenceEquals(previous, handle)) previous.TryFinish(cancelled: true);
        }
        else
        {
            var spring = new Spring(double.IsFinite(from) ? from : to, p) { Target = to };
            active = new ActiveSpring(key, spring, apply, completed, handle);
            _springs.Add(key, active);
            if (!SafeApply(active)) return;
        }

        EnsureSubscribed();
    }

    private bool SafeApply(ActiveSpring active)
    {
        try
        {
            active.Apply(active.Spring.Value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"AnimationEngine: apply callback failed for key '{active.Key}'; spring removed.", ex);
            Remove(active, cancelled: true);
            return false;
        }
    }

    private void Remove(ActiveSpring active, bool cancelled)
    {
        if (_springs.TryGetValue(active.Key, out var current) && ReferenceEquals(current, active))
            _springs.Remove(active.Key);
        active.IsRemoved = true;
        if (cancelled) active.Handle.TryFinish(cancelled: true);
        if (!_inFrame && _springs.Count == 0) Unsubscribe();
    }

    private void EnsureSubscribed()
    {
        if (_subscribed || _springs.Count == 0) return;
        _hasLastFrame = false;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
        _hasLastFrame = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_inFrame) return; // re-entrancy (nested message pump inside a callback)

        var now = e is RenderingEventArgs args ? args.RenderingTime : _fallbackClock.Elapsed;
        double dt;
        if (!_hasLastFrame)
        {
            dt = FirstFrameSeconds;
        }
        else
        {
            if (now == _lastFrameTime) return; // WPF raises Rendering more than once per frame
            dt = (now - _lastFrameTime).TotalSeconds;
            if (dt <= 0) dt = FirstFrameSeconds;
        }
        _lastFrameTime = now;
        _hasLastFrame = true;
        dt = Math.Min(dt, MaxFrameSeconds) * SpeedMultiplier;

        Action[]? callbacks = null;
        _inFrame = true;
        try
        {
            _frameBuffer.Clear();
            _frameBuffer.AddRange(_springs.Values);

            for (var i = 0; i < _frameBuffer.Count; i++)
            {
                var s = _frameBuffer[i];
                if (s.IsRemoved) continue;
                if (s.Handle.IsCompleted)
                {
                    // Cancelled from another thread; removal not processed yet.
                    Remove(s, cancelled: true);
                    continue;
                }

                s.Spring.Step(dt);
                if (!SafeApply(s)) continue;
                if (s.IsRemoved) continue; // the apply callback stopped it

                if (s.Spring.IsSettled)
                {
                    var handle = s.Handle;
                    var completed = s.Completed;
                    Remove(s, cancelled: false);
                    if (handle.TryFinish(cancelled: false) && completed is not null)
                        _completedBuffer.Add(completed);
                }
            }

            if (_completedBuffer.Count > 0)
            {
                callbacks = _completedBuffer.ToArray();
                _completedBuffer.Clear();
            }
        }
        finally
        {
            _frameBuffer.Clear();
            _inFrame = false;
        }

        if (callbacks is not null)
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    Log.Warn("AnimationEngine: completed callback threw.", ex);
                }
            }
        }

        if (_springs.Count == 0) Unsubscribe();
    }

    private static double ReadCurrent(DependencyObject target, DependencyProperty property)
    {
        var value = target.GetValue(property) is double d ? d : double.NaN;
        if (double.IsFinite(value)) return value;

        if (target is FrameworkElement fe)
        {
            if (property == FrameworkElement.WidthProperty) return fe.ActualWidth;
            if (property == FrameworkElement.HeightProperty) return fe.ActualHeight;
        }
        return 0;
    }

    private static void SetDouble(DependencyObject target, DependencyProperty property, double value)
    {
        object boxed = value;
        if (!property.IsValidValue(boxed))
        {
            // e.g. Width/Height reject negative values while a bouncy spring overshoots below zero.
            if (value < 0 && property.IsValidValue(0.0)) boxed = 0.0;
            else return;
        }
        target.SetValue(property, boxed);
    }

    private readonly record struct PropertyKey(DependencyObject Target, DependencyProperty Property);

    private sealed class ActiveSpring
    {
        public ActiveSpring(object key, Spring spring, Action<double> apply, Action? completed, SpringHandle handle)
        {
            Key = key;
            Spring = spring;
            Apply = apply;
            Completed = completed;
            Handle = handle;
        }

        public object Key { get; }
        public Spring Spring { get; }
        public Action<double> Apply { get; set; }
        public Action? Completed { get; set; }
        public SpringHandle Handle { get; set; }
        public bool IsRemoved { get; set; }
    }
}

/// <summary>
/// Handle to one <see cref="AnimationEngine.Animate"/> / <see cref="AnimationEngine.AnimateProperty"/> call.
/// Becomes completed when the spring settles, is cancelled/stopped, or is superseded by a retarget with the same key.
/// </summary>
public sealed class SpringHandle
{
    private const int Running = 0;
    private const int Settled = 1;
    private const int Cancelled = 2;

    private readonly AnimationEngine _engine;
    private int _state;

    internal SpringHandle(AnimationEngine engine, object key, double target)
    {
        _engine = engine;
        Key = key;
        Target = target;
    }

    /// <summary>The animation key this handle belongs to.</summary>
    public object Key { get; }

    /// <summary>The target value requested by this call.</summary>
    public double Target { get; }

    /// <summary>True once this animation is no longer running (settled, cancelled or superseded).</summary>
    public bool IsCompleted => Volatile.Read(ref _state) != Running;

    /// <summary>True when the animation ended without settling (cancelled, stopped or superseded).</summary>
    public bool IsCancelled => Volatile.Read(ref _state) == Cancelled;

    /// <summary>
    /// Stops this animation where it is, without invoking its completed callback. No effect if it has already
    /// completed or was superseded (a newer animation on the same key is never affected). Thread-safe.
    /// </summary>
    public void Cancel() => _engine.CancelHandle(this);

    internal bool TryFinish(bool cancelled)
        => Interlocked.CompareExchange(ref _state, cancelled ? Cancelled : Settled, Running) == Running;
}
