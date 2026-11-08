namespace WindowsDynamicIsland.Animation;

/// <summary>
/// Physical parameters of a damped harmonic oscillator (spring): <c>m·x'' + c·x' + k·x = 0</c>,
/// where <c>x</c> is the displacement from the target.
/// </summary>
/// <param name="Stiffness">Spring constant <c>k</c> (N/m-like units). Must be &gt; 0 for motion; 0 snaps instantly.</param>
/// <param name="Damping">Damping coefficient <c>c</c>. 0 = never settles (avoid in UI).</param>
/// <param name="Mass">Mass <c>m</c>. Non-positive values are treated as 1.</param>
public readonly record struct SpringParameters(double Stiffness, double Damping, double Mass = 1.0)
{
    /// <summary>
    /// Creates parameters the SwiftUI way: <paramref name="responseSeconds"/> is the period of the
    /// undamped oscillation (how "fast" the spring feels), <paramref name="dampingRatio"/> is ζ
    /// (1 = critically damped, no overshoot; &lt; 1 = bouncy).
    /// stiffness = (2π / response)² · m, damping = 4π · ζ · m / response, m = 1.
    /// </summary>
    /// <param name="responseSeconds">Oscillation period in seconds; clamped to at least 0.01.</param>
    /// <param name="dampingRatio">Damping ratio ζ; negative values are treated as 0.</param>
    public static SpringParameters FromResponse(double responseSeconds, double dampingRatio)
    {
        const double mass = 1.0;
        var response = double.IsFinite(responseSeconds) ? Math.Max(responseSeconds, 0.01) : 0.42;
        var ratio = double.IsFinite(dampingRatio) ? Math.Max(dampingRatio, 0.0) : 1.0;
        var omega = 2.0 * Math.PI / response;
        return new SpringParameters(omega * omega * mass, 4.0 * Math.PI * ratio * mass / response, mass);
    }

    /// <summary>Damping ratio ζ = c / (2·√(k·m)). Infinite/NaN for degenerate parameters.</summary>
    public double DampingRatio => Damping / (2.0 * Math.Sqrt(Stiffness * Mass));

    /// <summary>Undamped oscillation period in seconds (2π·√(m/k)). Infinite for zero stiffness.</summary>
    public double Response => 2.0 * Math.PI * Math.Sqrt(Mass / Stiffness);
}

/// <summary>Standard spring presets used across the island.</summary>
public static class SpringPresets
{
    /// <summary>Default: quick, almost no overshoot (response 0.42 s, ζ 0.86).</summary>
    public static SpringParameters Smooth { get; } = SpringParameters.FromResponse(0.42, 0.86);

    /// <summary>Playful, visible overshoot — capsule expanding (response 0.50 s, ζ 0.68).</summary>
    public static SpringParameters Bouncy { get; } = SpringParameters.FromResponse(0.50, 0.68);

    /// <summary>Fast and tight — small UI feedback (response 0.30 s, ζ 0.92).</summary>
    public static SpringParameters Snappy { get; } = SpringParameters.FromResponse(0.30, 0.92);

    /// <summary>Slow, critically damped — no overshoot at all (response 0.60 s, ζ 1.0).</summary>
    public static SpringParameters Gentle { get; } = SpringParameters.FromResponse(0.60, 1.0);
}

/// <summary>
/// A single-dimension spring simulation. Pure math (no WPF dependencies) so it can be unit tested.
/// Integration uses the exact analytic solution of the damped oscillator, so <see cref="Step"/> is
/// stable and exact for any time step (under-, critically- and over-damped cases).
/// Not thread-safe; use from one thread.
/// </summary>
public sealed class Spring
{
    private const double RelativePrecision = 0.001;   // settle when within 0.1 % of the travelled range…
    private const double VelocityFactor = 10.0;       // …and moving slower than 10 × that per second
    private const double CriticalBand = 1e-6;

    private double _value;
    private double _velocity;
    private double _target;
    private double _range;
    private SpringParameters _parameters;

    /// <summary>Creates a spring at rest at <paramref name="initialValue"/> (Target = initialValue).</summary>
    /// <exception cref="ArgumentOutOfRangeException">initialValue is NaN or infinite.</exception>
    public Spring(double initialValue, SpringParameters parameters)
    {
        if (!double.IsFinite(initialValue))
            throw new ArgumentOutOfRangeException(nameof(initialValue), initialValue, "Spring value must be finite.");
        _value = initialValue;
        _target = initialValue;
        _range = 0;
        _parameters = Sanitize(parameters);
    }

    /// <summary>Current value.</summary>
    public double Value => _value;

    /// <summary>Current velocity (units per second).</summary>
    public double Velocity => _velocity;

    /// <summary>
    /// Rest position. Changing it keeps the current value and velocity (natural interruption).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">value is NaN or infinite.</exception>
    public double Target
    {
        get => _target;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Spring target must be finite.");
            _target = value;
            _range = Math.Abs(value - _value);
        }
    }

    /// <summary>
    /// Physical parameters. May be changed mid-flight; the motion continues from the current state.
    /// Invalid components are sanitized (mass ≤ 0 → 1, negative/NaN stiffness or damping → 0).
    /// </summary>
    public SpringParameters Parameters
    {
        get => _parameters;
        set => _parameters = Sanitize(value);
    }

    /// <summary>
    /// Position tolerance used by <see cref="IsSettled"/>: 0.1 % of the distance travelled since the
    /// last retarget, but never less than 0.001.
    /// </summary>
    public double Precision => RelativePrecision * Math.Max(1.0, _range);

    /// <summary>True when the spring is (practically) at rest at its target.</summary>
    public bool IsSettled
    {
        get
        {
            var eps = Precision;
            return Math.Abs(_value - _target) < eps && Math.Abs(_velocity) < eps * VelocityFactor;
        }
    }

    /// <summary>
    /// Advances the simulation by <paramref name="dtSeconds"/>. Exact for any step size.
    /// Non-positive or NaN steps are ignored; an infinite step snaps to the target.
    /// When the spring settles, it is snapped exactly to <see cref="Target"/> with zero velocity.
    /// </summary>
    public void Step(double dtSeconds)
    {
        if (IsSettled)
        {
            _value = _target;
            _velocity = 0;
            return;
        }

        if (double.IsNaN(dtSeconds) || dtSeconds <= 0) return;
        if (double.IsPositiveInfinity(dtSeconds))
        {
            SnapTo(_target);
            return;
        }

        var m = _parameters.Mass;
        var k = _parameters.Stiffness;
        var c = _parameters.Damping;
        if (k <= 0)
        {
            // No restoring force: nothing meaningful to simulate — jump to the target.
            SnapTo(_target);
            return;
        }

        var t = dtSeconds;
        var x0 = _value - _target;
        var v0 = _velocity;
        var w0 = Math.Sqrt(k / m);
        var zeta = c / (2.0 * Math.Sqrt(k * m));
        double x, v;

        if (zeta < 1.0 - CriticalBand)
        {
            // Under-damped: x = e^(−ζω₀t)·(x₀·cos ω_d t + ((v₀ + ζω₀x₀)/ω_d)·sin ω_d t)
            var wd = w0 * Math.Sqrt(1.0 - zeta * zeta);
            var a = zeta * w0;
            var decay = Math.Exp(-a * t);
            var cos = Math.Cos(wd * t);
            var sin = Math.Sin(wd * t);
            x = decay * (x0 * cos + (v0 + a * x0) / wd * sin);
            v = decay * (v0 * cos - (a * v0 + w0 * w0 * x0) / wd * sin);
        }
        else if (zeta > 1.0 + CriticalBand)
        {
            // Over-damped: x = C₁e^(r₁t) + C₂e^(r₂t)
            var s = w0 * Math.Sqrt(zeta * zeta - 1.0);
            var r1 = -zeta * w0 + s;
            var r2 = -zeta * w0 - s;
            var c2 = (v0 - r1 * x0) / (r2 - r1);
            var c1 = x0 - c2;
            var e1 = Math.Exp(r1 * t);
            var e2 = Math.Exp(r2 * t);
            x = c1 * e1 + c2 * e2;
            v = c1 * r1 * e1 + c2 * r2 * e2;
        }
        else
        {
            // Critically damped: x = e^(−ω₀t)·(x₀ + (v₀ + ω₀x₀)·t)
            var decay = Math.Exp(-w0 * t);
            var b = v0 + w0 * x0;
            x = decay * (x0 + b * t);
            v = decay * (v0 - w0 * b * t);
        }

        if (!double.IsFinite(x) || !double.IsFinite(v))
        {
            SnapTo(_target);
            return;
        }

        _value = _target + x;
        _velocity = v;

        if (IsSettled)
        {
            _value = _target;
            _velocity = 0;
        }
    }

    /// <summary>Jumps to <paramref name="value"/>, sets Target to it and stops all motion.</summary>
    /// <exception cref="ArgumentOutOfRangeException">value is NaN or infinite.</exception>
    public void SnapTo(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Spring value must be finite.");
        _value = value;
        _target = value;
        _velocity = 0;
        _range = 0;
    }

    private static SpringParameters Sanitize(SpringParameters p)
    {
        var mass = double.IsFinite(p.Mass) && p.Mass > 0 ? p.Mass : 1.0;
        var stiffness = double.IsFinite(p.Stiffness) && p.Stiffness > 0 ? p.Stiffness : 0.0;
        var damping = double.IsFinite(p.Damping) && p.Damping > 0 ? p.Damping : 0.0;
        return new SpringParameters(stiffness, damping, mass);
    }
}
