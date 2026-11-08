using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WindowsDynamicIsland.SystemIntegration.Native;
using WinBattery = Windows.Devices.Power.Battery;
using WinBatteryReport = Windows.Devices.Power.BatteryReport;
using WinBatteryStatus = Windows.System.Power.BatteryStatus;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Charger and battery-level indicators.
/// Sources: <c>Windows.Devices.Power.Battery.AggregateBattery</c> (<c>GetReport()</c> + <c>ReportUpdated</c>)
/// for level and charge status, and <c>GetSystemPowerStatus</c> for the AC line state (and as a fallback for
/// the level). If <c>ReportUpdated</c> cannot be subscribed, a 20 s <c>GetSystemPowerStatus</c> poll is used.
///
/// Events (no indicator for the state found at start):
///  * Charger (<c>Indicators.Charger</c>): AC plugged / unplugged, settled for 800 ms.
///  * Battery (<c>Indicators.Battery</c>): crossing the low threshold, 10 % and 5 % downward while on battery
///    (only the most severe one when several are crossed at once), and reaching 100 % on AC.
///    Each fires once per crossing and re-arms 2 % above its threshold (100 % re-arms below 98 %).
/// A PC without a battery reports "Батарея не обнаружена" and publishes nothing.
/// </summary>
public sealed class BatteryMonitor : IIslandModule
{
    private const double Hysteresis = 0.02;
    private const double FullLevel = 0.995;
    private static readonly TimeSpan AcSettleDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(20);

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();

    private WinBattery? _battery;
    private bool _reportSubscribed;
    private System.Threading.Timer? _acSettleTimer;
    private System.Threading.Timer? _pollTimer;
    private bool _running;

    // State (guarded by _gate).
    private bool? _publishedAc;
    private bool _batteryPresent;
    private double? _lastLevel;
    private bool _lowArmed;
    private bool _tenArmed;
    private bool _fiveArmed;
    private bool _fullArmed;

    /// <summary>Power state; <see cref="OnAc"/> is null when unknown, <see cref="Level"/> is 0..1 or null.</summary>
    private readonly record struct PowerSnapshot(bool BatteryPresent, bool? OnAc, double? Level);

    public BatteryMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _settings = settings;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Indicators.Battery || s.Indicators.Charger,
            _ => "",
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Батарея";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        WinBattery? battery = null;
        try
        {
            battery = WinBattery.AggregateBattery;
        }
        catch (Exception ex)
        {
            Log.Warn("Battery: AggregateBattery unavailable, using GetSystemPowerStatus", ex);
        }

        lock (_gate)
        {
            _running = true;
            _battery = battery;
            _acSettleTimer = new System.Threading.Timer(_ => OnAcSettled(), null, Timeout.Infinite, Timeout.Infinite);

            // Baseline: arm only the thresholds that are still ahead of us.
            var snapshot = ReadSnapshot(battery);
            _batteryPresent = snapshot.BatteryPresent;
            _publishedAc = snapshot.OnAc;
            _lastLevel = snapshot.Level;
            ArmFromLevel(snapshot.Level, settings.Indicators.LowBatteryThreshold);
        }

        var subscribed = false;
        if (battery is not null)
        {
            try
            {
                battery.ReportUpdated += OnReportUpdated;
                subscribed = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Battery: ReportUpdated subscription failed, polling instead", ex);
            }
        }

        lock (_gate)
        {
            _reportSubscribed = subscribed;
            if (!subscribed)
                _pollTimer = new System.Threading.Timer(_ => Evaluate(), null, FallbackPollInterval, FallbackPollInterval);
        }

        bool present;
        lock (_gate) present = _batteryPresent;
        return Task.FromResult(present
            ? FeatureStatus.Ok()
            : FeatureStatus.Ok("Батарея не обнаружена — индикаторы заряда и зарядки не используются"));
    }

    private void ArmFromLevel(double? level, double lowThreshold)
    {
        var l = level ?? 1.0;
        _lowArmed = l > lowThreshold;
        _tenArmed = l > 0.10;
        _fiveArmed = l > 0.05;
        _fullArmed = l < FullLevel;
    }

    private void OnReportUpdated(WinBattery sender, object args) => Evaluate();

    /// <summary>Reads current power state. Never throws.</summary>
    private static PowerSnapshot ReadSnapshot(WinBattery? battery)
    {
        bool? onAc = null;
        double? spsLevel = null;
        bool? spsPresent = null;
        try
        {
            if (NativeMethods.GetSystemPowerStatus(out var sps))
            {
                onAc = sps.ACLineStatus switch { 0 => false, 1 => true, _ => null };
                if (sps.BatteryFlag != NativeMethods.BATTERY_FLAG_UNKNOWN)
                    spsPresent = (sps.BatteryFlag & NativeMethods.BATTERY_FLAG_NO_BATTERY) == 0;
                if (sps.BatteryLifePercent <= 100) spsLevel = sps.BatteryLifePercent / 100.0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Battery: GetSystemPowerStatus failed", ex);
        }

        double? reportLevel = null;
        bool? reportPresent = null;
        if (battery is not null)
        {
            try
            {
                WinBatteryReport report = battery.GetReport();
                reportPresent = report.Status != WinBatteryStatus.NotPresent;
                if (report.RemainingCapacityInMilliwattHours is int remaining &&
                    report.FullChargeCapacityInMilliwattHours is int full && full > 0)
                {
                    reportLevel = Math.Clamp((double)remaining / full, 0, 1);
                }
                onAc ??= report.Status switch
                {
                    WinBatteryStatus.Charging or WinBatteryStatus.Idle => true,
                    WinBatteryStatus.Discharging => false,
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Battery: GetReport failed", ex);
            }
        }

        var present = reportPresent ?? spsPresent ?? false;
        var level = present ? reportLevel ?? spsLevel : null;
        return new PowerSnapshot(present, onAc, level);
    }

    private void Evaluate()
    {
        try
        {
            EvaluateCore();
        }
        catch (Exception ex)
        {
            Log.Warn("Battery: evaluation failed", ex);
        }
    }

    private void EvaluateCore()
    {
        SystemIndicatorEvent? levelEvent = null;
        var indicators = _settings.Current.Indicators;
        lock (_gate)
        {
            if (!_running) return;
            var snapshot = ReadSnapshot(_battery);
            _batteryPresent = snapshot.BatteryPresent;
            if (!snapshot.BatteryPresent) return;

            // AC change → settle before announcing (loose connectors, docking).
            if (snapshot.OnAc is not null && snapshot.OnAc != _publishedAc)
            {
                try { _acSettleTimer?.Change(AcSettleDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }

            if (snapshot.Level is not { } level) return;
            _lastLevel = level;
            var low = indicators.LowBatteryThreshold;

            // Re-arm with hysteresis.
            if (level >= low + Hysteresis) _lowArmed = true;
            if (level >= 0.10 + Hysteresis) _tenArmed = true;
            if (level >= 0.05 + Hysteresis) _fiveArmed = true;
            if (level < 0.98) _fullArmed = true;

            if (snapshot.OnAc == false)
            {
                // Fire only the most severe threshold crossed; disarm every crossed one.
                var fiveHit = _fiveArmed && level <= 0.05;
                var tenHit = _tenArmed && level <= 0.10;
                var lowHit = _lowArmed && level <= low;
                if (fiveHit || tenHit || lowHit)
                {
                    if (level <= 0.05) _fiveArmed = false;
                    if (level <= 0.10) _tenArmed = false;
                    if (level <= low) _lowArmed = false;

                    var percent = ToPercent(level);
                    var critical = fiveHit || tenHit;
                    levelEvent = new SystemIndicatorEvent
                    {
                        Kind = IndicatorKind.Battery,
                        Glyph = Glyphs.BatteryForLevel(level),
                        Title = fiveHit ? "Критический заряд" : tenHit ? "Очень низкий заряд" : "Низкий заряд",
                        Subtitle = critical ? $"{percent}% · подключите зарядку" : $"{percent}%",
                        Level = level,
                        Flag = false,
                        Tone = critical ? IndicatorTone.Critical : IndicatorTone.Warning,
                    };
                }
            }
            else if (snapshot.OnAc == true && _fullArmed && level >= FullLevel)
            {
                _fullArmed = false;
                levelEvent = new SystemIndicatorEvent
                {
                    Kind = IndicatorKind.Battery,
                    Glyph = Glyphs.BatteryForLevel(1.0),
                    Title = "Заряжено",
                    Subtitle = "100%",
                    Level = 1.0,
                    Flag = false,
                    Tone = IndicatorTone.Positive,
                };
            }
        }

        if (levelEvent is not null && indicators.Battery) _bus.Publish(levelEvent);
    }

    private void OnAcSettled()
    {
        try
        {
            SystemIndicatorEvent? evt = null;
            lock (_gate)
            {
                if (!_running) return;
                var snapshot = ReadSnapshot(_battery);
                if (!snapshot.BatteryPresent || snapshot.OnAc is not { } onAc || onAc == _publishedAc) return;
                var hadBaseline = _publishedAc is not null;
                _publishedAc = onAc;
                // The AC state was unknown at start: this is the baseline, not a change.
                if (!hadBaseline) return;
                var level = snapshot.Level ?? _lastLevel;
                var subtitle = level is { } l ? $"{ToPercent(l)}%" : null;
                evt = onAc
                    ? new SystemIndicatorEvent
                    {
                        Kind = IndicatorKind.Charger,
                        Glyph = Glyphs.Lightning,
                        Title = "Зарядка подключена",
                        Subtitle = subtitle,
                        Level = level,
                        Flag = true,
                        Tone = IndicatorTone.Positive,
                    }
                    : new SystemIndicatorEvent
                    {
                        Kind = IndicatorKind.Charger,
                        Glyph = Glyphs.BatteryForLevel(level ?? 0),
                        Title = "Работа от батареи",
                        Subtitle = subtitle,
                        Level = level,
                        Flag = false,
                        Tone = IndicatorTone.Neutral,
                    };
            }

            if (_settings.Current.Indicators.Charger) _bus.Publish(evt);
        }
        catch (Exception ex)
        {
            Log.Warn("Battery: AC settle failed", ex);
        }
    }

    private static int ToPercent(double level) => (int)Math.Round(Math.Clamp(level, 0, 1) * 100);

    private void Deactivate()
    {
        WinBattery? battery;
        bool subscribed;
        System.Threading.Timer? settle, poll;
        lock (_gate)
        {
            _running = false;
            battery = _battery;
            subscribed = _reportSubscribed;
            settle = _acSettleTimer;
            poll = _pollTimer;
            _battery = null;
            _reportSubscribed = false;
            _acSettleTimer = null;
            _pollTimer = null;
        }

        if (battery is not null && subscribed)
        {
            try { battery.ReportUpdated -= OnReportUpdated; }
            catch (Exception ex) { Log.Warn("Battery: unsubscribe failed", ex); }
        }
        settle?.Dispose();
        poll?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
