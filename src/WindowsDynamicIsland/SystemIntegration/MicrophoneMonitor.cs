using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Microphone indicators (<c>Indicators.Microphone</c>):
///  (a) mute state of the default communications capture endpoint (Core Audio volume notifications,
///      follows default-device changes) → "Микрофон выключен" / "Микрофон включён";
///  (b) "microphone in use" from the Windows privacy consent store
///      <c>HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone</c>:
///      an app is using the microphone while its <c>LastUsedTimeStop</c> is 0 and <c>LastUsedTimeStart</c> is not.
///      Packaged apps are direct subkeys; desktop apps live under <c>NonPackaged</c> as "C:#Path#app.exe".
///      Polled every 2 s while enabled (a handful of registry reads). Our own process is excluded.
/// Apps already using the microphone at start are recorded silently.
/// </summary>
public sealed class MicrophoneMonitor : IIslandModule
{
    private const string ConsentStorePath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string NonPackagedKey = "NonPackaged";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _ownConsentKey;

    private EndpointVolumeWatcher? _watcher;
    private System.Threading.Timer? _pollTimer;
    private HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private bool? _lastMuted;
    private bool _running;
    private int _polling;

    public MicrophoneMonitor(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Indicators.Microphone,
            _ => "",
            ActivateAsync,
            Deactivate);

        var processPath = Environment.ProcessPath;
        _ownConsentKey = string.IsNullOrEmpty(processPath) ? null : processPath.Replace('\\', '#');
    }

    /// <inheritdoc />
    public string Name => "Микрофон";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        var problems = new List<string>();

        // (a) Mute state.
        var watcher = new EndpointVolumeWatcher(DataFlow.Capture, Role.Communications, "Microphone", OnCaptureVolume);
        lock (_gate)
        {
            _running = true;
            _lastMuted = null;
            _watcher = watcher;
        }
        var error = watcher.Start();
        if (error is not null) problems.Add("состояние «без звука» недоступно: " + error);
        else if (!watcher.HasDevice) problems.Add("микрофон не найден — ожидание подключения");

        // (b) Usage via the consent store.
        HashSet<string>? initial = null;
        try
        {
            initial = ReadInUse();
        }
        catch (Exception ex)
        {
            Log.Warn("Microphone: consent store read failed", ex);
        }

        if (initial is null)
        {
            problems.Add("определение «микрофон используется» недоступно: в этой версии Windows нет журнала доступа к микрофону (CapabilityAccessManager)");
        }
        else
        {
            lock (_gate)
            {
                _inUse = initial;
                _pollTimer = new System.Threading.Timer(_ => Poll(), null, PollInterval, PollInterval);
            }
        }

        if (error is not null && initial is null)
            return Task.FromResult(FeatureStatus.Unavailable("Микрофон: " + string.Join("; ", problems)));
        return Task.FromResult(problems.Count == 0
            ? FeatureStatus.Ok()
            : FeatureStatus.Ok("Микрофон: " + string.Join("; ", problems)));
    }

    // ───────────────────────────── (a) Mute ─────────────────────────────

    private void OnCaptureVolume(float level, bool muted, bool isBaseline)
    {
        lock (_gate)
        {
            if (!_running) return;
            var previous = _lastMuted;
            _lastMuted = muted;
            // Baseline (start / device switch) or a level-only change: nothing to show.
            if (isBaseline || previous is null || previous == muted) return;
        }

        _bus.Publish(new SystemIndicatorEvent
        {
            Kind = IndicatorKind.Microphone,
            Glyph = muted ? Glyphs.MicOff : Glyphs.Microphone,
            Title = muted ? "Микрофон выключен" : "Микрофон включён",
            Flag = muted,
            Tone = muted ? IndicatorTone.Warning : IndicatorTone.Neutral,
        });
    }

    // ───────────────────────────── (b) Usage ─────────────────────────────

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            var current = ReadInUse();
            if (current is null) return;

            List<string> started, stopped;
            bool? muted;
            lock (_gate)
            {
                if (!_running) return;
                started = current.Where(k => !_inUse.Contains(k)).ToList();
                stopped = _inUse.Where(k => !current.Contains(k)).ToList();
                _inUse = current;
                muted = _lastMuted;
            }

            if (started.Count > 0)
            {
                _bus.Publish(new SystemIndicatorEvent
                {
                    Kind = IndicatorKind.Microphone,
                    Glyph = Glyphs.Microphone,
                    Title = "Микрофон используется",
                    Subtitle = JoinNames(started),
                    Flag = muted,
                    Tone = IndicatorTone.Warning,
                });
            }
            else if (stopped.Count > 0)
            {
                _bus.Publish(new SystemIndicatorEvent
                {
                    Kind = IndicatorKind.Microphone,
                    Glyph = Glyphs.Microphone,
                    Title = "Микрофон освобождён",
                    Subtitle = JoinNames(stopped),
                    Flag = muted,
                    Tone = IndicatorTone.Neutral,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Microphone: poll failed", ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private string JoinNames(List<string> keys)
    {
        var names = keys.Select(GetDisplayName).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} и ещё {names.Count - 2}";
    }

    /// <summary>
    /// Returns the consent-store keys of apps currently using the microphone, or null when the store does not exist.
    /// Keys: packaged family name, or "NonPackaged\C:#Path#app.exe".
    /// </summary>
    private HashSet<string>? ReadInUse()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStorePath, writable: false);
        if (root is null) return null;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in root.GetSubKeyNames())
        {
            if (string.Equals(name, NonPackagedKey, StringComparison.OrdinalIgnoreCase))
            {
                using var nonPackaged = root.OpenSubKey(name, writable: false);
                if (nonPackaged is null) continue;
                foreach (var exeKey in nonPackaged.GetSubKeyNames())
                {
                    if (_ownConsentKey is not null && string.Equals(exeKey, _ownConsentKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsInUse(nonPackaged, exeKey)) result.Add(NonPackagedKey + "\\" + exeKey);
                }
            }
            else if (IsInUse(root, name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    private static bool IsInUse(RegistryKey parent, string subKeyName)
    {
        try
        {
            using var key = parent.OpenSubKey(subKeyName, writable: false);
            if (key is null) return false;
            return key.GetValue("LastUsedTimeStart") is long start && start != 0
                && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
        }
        catch (Exception)
        {
            // Key removed between enumeration and open, or access denied: treat as not in use.
            return false;
        }
    }

    private string GetDisplayName(string consentKey) => _displayNames.GetOrAdd(consentKey, BuildDisplayName);

    /// <summary>"NonPackaged\C:#Program Files#Zoom#Zoom.exe" → file description or "Zoom"; "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".</summary>
    private static string BuildDisplayName(string consentKey)
    {
        try
        {
            if (consentKey.StartsWith(NonPackagedKey + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var path = consentKey.Substring(NonPackagedKey.Length + 1).Replace('#', '\\');
                if (File.Exists(path))
                {
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(description)) return description;
                }
                var fileName = Path.GetFileNameWithoutExtension(path);
                return string.IsNullOrEmpty(fileName) ? "Приложение" : fileName;
            }

            var family = consentKey;
            var underscore = family.LastIndexOf('_');
            if (underscore > 0) family = family.Substring(0, underscore);
            var dot = family.LastIndexOf('.');
            if (dot >= 0 && dot < family.Length - 1) family = family.Substring(dot + 1);
            return string.IsNullOrEmpty(family) ? "Приложение" : family;
        }
        catch (Exception)
        {
            return "Приложение";
        }
    }

    private void Deactivate()
    {
        EndpointVolumeWatcher? watcher;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            _running = false;
            watcher = _watcher;
            timer = _pollTimer;
            _watcher = null;
            _pollTimer = null;
            _inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastMuted = null;
        }
        timer?.Dispose();
        watcher?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
