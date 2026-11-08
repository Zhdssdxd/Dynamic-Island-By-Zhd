using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Audio;

/// <summary>
/// Real-time spectrum of the system sound: WASAPI loopback of the default render device → mono → FFT
/// (<see cref="FftProcessor"/>) → 32 log-spaced bands published through <see cref="ISpectrumSource"/>.
/// <para>
/// Capture runs only while at least one consumer holds a token (<see cref="AcquireConsumer"/>) AND the visualizer
/// is enabled in settings; it stops 2 s after the last consumer leaves. Default-device changes restart capture,
/// device invalidation is retried (3 quick retries, then the module reports a failure). Audio is never stored.
/// </para>
/// <para>
/// Threads: NAudio's capture thread only converts and appends samples to a ring buffer; a dedicated analysis
/// thread (alive only while capturing) computes at most ~60 frames/s, woken by new data or by the UI polling
/// <see cref="TryGetBands"/>. Capture lifecycle (COM calls) runs on thread-pool threads, never on the UI thread.
/// </para>
/// </summary>
public sealed class AudioAnalyzer : IIslandModule, ISpectrumSource
{
    /// <summary>Number of bands produced (<see cref="BandCount"/>).</summary>
    public const int DefaultBandCount = 32;

    private const int StopDelayMs = 2000;
    private const int DeviceChangeDebounceMs = 300;
    private const int RetryBaseDelayMs = 300;
    private const int MaxQuickRetries = 3;
    private const double StableRunSeconds = 10;
    private const double StaleAfterSeconds = 0.1;
    private const double StaleDecaySeconds = 0.12;
    private const float StaleCutoff = 0.004f;
    private const double GapResetSeconds = 0.3;
    private const double MinFrameIntervalSeconds = 0.0145;
    private const int AnalysisIdleWaitMs = 500;
    private const int ENotFound = unchecked((int)0x80070490);

    private const string DisabledMessage = "Выключен в настройках";
    private const string NoDeviceMessage = "Не найдено устройство воспроизведения звука";
    private const string StoppedBySystemMessage = "Захват звука остановлен системой";

    private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly SubscriptionBag _subscriptions = new();
    private readonly Timer _reconcileTimer;

    // Capture lifecycle (start/stop/restart, COM objects). Never taken on NAudio's capture thread.
    private readonly object _controlGate = new();
    // Retry/fault bookkeeping; may be taken on the capture thread (never while waiting for that thread).
    private readonly object _failureGate = new();
    private readonly object _scheduleGate = new();
    private readonly object _statusGate = new();

    // Published frame ("front buffer"), guarded by _frameGate; the analysis thread owns the back buffer (FftProcessor).
    private readonly object _frameGate = new();
    private readonly float[] _frontBands = new float[DefaultBandCount];
    private float _frontLevel;
    private long _frontTimestamp;
    private bool _hasFrame;
    private long _frameNumber;

    private volatile CaptureSession? _session;
    private volatile bool _isCapturing;
    private volatile bool _running;
    private volatile bool _enabled;
    private volatile bool _tornDown;
    private volatile float _sensitivity = 1f;
    private int _disposed;
    private int _startCalled;
    private int _consumers;
    private long _lastReleaseTimestamp;
    private int _restartFlag;
    private volatile string _deviceId = "";
    /// <summary>Auto mode: endpoint picked because sound was actually playing there (null = default device).</summary>
    private volatile string? _autoDeviceId;
    /// <summary>Stopwatch timestamp of the last non-silent capture packet.</summary>
    private long _lastSignalTimestamp;
    private System.Threading.Timer? _autoWatch;
    private const double AutoSwitchSilenceSeconds = 1.5;
    private const float AudiblePeak = 0.002f;
    private long _scheduledDue = long.MaxValue;

    private bool _faulted;     // _failureGate
    private int _retryCount;   // _failureGate

    private bool _hasStatus;          // _statusGate
    private bool _lastStatusOk;       // _statusGate
    private string? _lastStatusMessage; // _statusGate

    private MMDeviceEnumerator? _enumerator; // _controlGate
    private DefaultDeviceWatcher? _watcher;  // _controlGate

    /// <summary>Creates the module. Cheap: touches no OS audio API (that happens in <see cref="StartAsync"/>).</summary>
    public AudioAnalyzer(IEventBus bus, SettingsManager settings)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var features = settings.Current.Features;
        _enabled = features.VisualizerEnabled;
        _deviceId = features.VisualizerDeviceId ?? "";
        _sensitivity = SanitizeSensitivity(features.VisualizerSensitivity);
        _reconcileTimer = new Timer(OnReconcileTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public string Name => "Аудиовизуализатор";

    /// <inheritdoc />
    public int BandCount => DefaultBandCount;

    /// <inheritdoc />
    public bool IsCapturing => _isCapturing;

    /// <inheritdoc />
    public long FrameNumber => Interlocked.Read(ref _frameNumber);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed || cancellationToken.IsCancellationRequested || Interlocked.Exchange(ref _startCalled, 1) == 1)
            return Task.CompletedTask;

        var features = _settings.Current.Features;
        _enabled = features.VisualizerEnabled;
        _sensitivity = SanitizeSensitivity(features.VisualizerSensitivity);
        _subscriptions.Add(_bus.Subscribe<SettingsChanged>(OnSettingsChanged));

        // MMDeviceEnumerator creation is a COM activation: keep it off the UI thread (thread pool = MTA).
        return Task.Run(InitializeCoreAudio, CancellationToken.None);
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _running = false;
        _subscriptions.Dispose();
        return Task.Run(() => Teardown(publish: true));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _running = false;
        _subscriptions.Dispose();
        try { _reconcileTimer.Dispose(); } catch (Exception ex) { Log.Warn("Audio reconcile timer dispose failed", ex); }

        if (_tornDown) return;
        try
        {
            // COM teardown on an MTA thread; bounded so shutdown can never hang on a stuck driver.
            if (!Task.Run(() => Teardown(publish: false)).Wait(2000))
                Log.Warn("Audio analyzer teardown did not finish within 2 s");
        }
        catch (Exception ex)
        {
            Log.Warn("Audio analyzer teardown failed", ex);
        }
    }

    /// <inheritdoc />
    public IDisposable AcquireConsumer()
    {
        if (Interlocked.Increment(ref _consumers) == 1)
            ScheduleReconcile(0);
        return new ConsumerToken(this);
    }

    /// <inheritdoc />
    public bool TryGetBands(Span<float> destination, out float level)
    {
        level = 0f;
        if (destination.IsEmpty) return false;

        var session = _session;
        if (!_isCapturing || session is null)
        {
            destination.Clear();
            return false;
        }

        // Lets the analysis thread advance its play-out cursor in step with the display.
        session.RequestFrameIfPending();

        Span<float> bands = stackalloc float[DefaultBandCount];
        float frameLevel;
        long timestamp;
        lock (_frameGate)
        {
            if (!_hasFrame)
            {
                destination.Clear();
                return false;
            }
            _frontBands.AsSpan().CopyTo(bands);
            frameLevel = _frontLevel;
            timestamp = _frontTimestamp;
        }

        // Loopback delivers no callbacks while nothing plays: age the last frame towards silence.
        var age = (Stopwatch.GetTimestamp() - timestamp) * TicksToSeconds;
        if (age > StaleAfterSeconds)
        {
            var fade = (float)Math.Exp(-(age - StaleAfterSeconds) / StaleDecaySeconds);
            if (fade < StaleCutoff)
            {
                destination.Clear();
                return false;
            }
            for (var i = 0; i < bands.Length; i++) bands[i] *= fade;
            frameLevel *= fade;
        }

        if (destination.Length == DefaultBandCount) bands.CopyTo(destination);
        else FftProcessor.Resample(bands, destination);

        level = frameLevel;
        return true;
    }

    // ───────────────────────── lifecycle ─────────────────────────

    private void InitializeCoreAudio()
    {
        string? failure = null;
        lock (_controlGate)
        {
            if (IsDisposed || _tornDown) return;
            try
            {
                _enumerator = new MMDeviceEnumerator();
            }
            catch (Exception ex)
            {
                Log.Error("Core Audio (MMDeviceEnumerator) is unavailable", ex);
                failure = "Системная служба звука Windows недоступна: " + ex.Message;
            }

            if (_enumerator is not null)
            {
                try
                {
                    var watcher = new DefaultDeviceWatcher(OnDefaultRenderDeviceChanged, OnDeviceAvailabilityChanged);
                    _enumerator.RegisterEndpointNotificationCallback(watcher);
                    _watcher = watcher;
                }
                catch (Exception ex)
                {
                    // Capture still works; only automatic following of output-device switches is lost.
                    Log.Warn("Audio endpoint notifications are unavailable", ex);
                }
                _running = true;
            }
        }

        if (failure is not null)
        {
            _bus.Publish(new AudioCaptureStateChanged(false, failure));
            PublishStatus(false, failure);
            return;
        }

        PublishStatus(true, _enabled ? null : DisabledMessage);
        ScheduleReconcile(0);
    }

    private void Teardown(bool publish)
    {
        lock (_controlGate)
        {
            if (_tornDown) return;
            _tornDown = true;
            _running = false;

            StopCaptureCore(publish);

            if (_watcher is not null && _enumerator is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); }
                catch (Exception ex) { Log.Warn("Unregistering audio endpoint notifications failed", ex); }
            }
            _watcher = null;

            DisposeQuietly(_enumerator);
            _enumerator = null;
        }
    }

    private void OnReconcileTimer(object? state)
    {
        lock (_scheduleGate) _scheduledDue = long.MaxValue;
        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            Log.Error("Audio capture reconcile failed", ex);
        }
    }

    /// <summary>
    /// Requests a <see cref="Reconcile"/> after <paramref name="delayMs"/>. If an earlier run is already pending the
    /// request is merged into it (every run re-evaluates the full state and re-schedules itself when it must wait).
    /// </summary>
    private void ScheduleReconcile(int delayMs)
    {
        if (IsDisposed) return;
        delayMs = Math.Max(0, delayMs);
        var due = Stopwatch.GetTimestamp() + (long)(delayMs / 1000.0 * Stopwatch.Frequency);
        lock (_scheduleGate)
        {
            if (_scheduledDue <= due) return;
            _scheduledDue = due;
            try
            {
                _reconcileTimer.Change(delayMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently — nothing left to do.
            }
        }
    }

    /// <summary>Brings the capture in line with the desired state. Runs on thread-pool threads, serialized.</summary>
    private void Reconcile()
    {
        if (IsDisposed) return;
        lock (_controlGate)
        {
            if (IsDisposed || _tornDown) return;

            var restart = Interlocked.Exchange(ref _restartFlag, 0) == 1;
            var session = _session;
            if (session is not null && (restart || session.Failed))
            {
                // A restart is announced by the next start (or the failure that caused it).
                StopCaptureCore(publish: false);
                session = null;
            }

            bool faulted;
            lock (_failureGate) faulted = _faulted;
            var consumers = Volatile.Read(ref _consumers);
            var enabled = _enabled;
            var want = _running && enabled && consumers > 0 && !faulted && _enumerator is not null;

            if (want)
            {
                if (session is null) StartCaptureCore();
                return;
            }

            if (session is null) return;

            if (_running && enabled && !faulted && consumers == 0)
            {
                // Debounce: keep capturing for a moment in case a consumer comes right back (view switches).
                var sinceReleaseMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastReleaseTimestamp)) * TicksToSeconds * 1000;
                if (sinceReleaseMs < StopDelayMs)
                {
                    ScheduleReconcile((int)Math.Ceiling(StopDelayMs - sinceReleaseMs) + 10);
                    return;
                }
            }

            StopCaptureCore(publish: true);
        }
    }

    /// <summary>Starts loopback capture of the default render device. Caller holds <see cref="_controlGate"/>.</summary>
    private void StartCaptureCore()
    {
        var enumerator = _enumerator;
        if (enumerator is null) return;

        MMDevice? device = null;
        WasapiLoopbackCapture? capture = null;
        CaptureSession? session = null;
        try
        {
            device = ResolveCaptureDevice(enumerator, string.IsNullOrEmpty(_deviceId) ? _autoDeviceId ?? "" : _deviceId);
            capture = new WasapiLoopbackCapture(device);

            var converter = SampleConverter.TryCreate(capture.WaveFormat, out var formatError);
            if (converter is null)
            {
                CleanupFailedStart(null, capture, device);
                SetFaulted(formatError ?? "Неподдерживаемый формат звука.");
                return;
            }

            var deviceName = TryGetFriendlyName(device);
            session = new CaptureSession(this, device, capture, converter);
            ResetFrontBuffer();
            _session = session;
            session.Start();
            _isCapturing = true;

            Log.Info($"Audio loopback capture started: {deviceName} ({converter.Description})");
            Interlocked.Exchange(ref _lastSignalTimestamp, Stopwatch.GetTimestamp());
            if (string.IsNullOrEmpty(_deviceId))
            {
                _autoWatch?.Dispose();
                _autoWatch = new System.Threading.Timer(OnAutoWatch, null, 1000, 1000);
            }
            _bus.Publish(new AudioCaptureStateChanged(true, null));
            PublishStatus(true, null);
        }
        catch (COMException ex) when (ex.HResult == ENotFound)
        {
            CleanupFailedStart(session, capture, device);
            Log.Info("Audio loopback: no default render device");
            _bus.Publish(new AudioCaptureStateChanged(false, NoDeviceMessage));
            PublishStatus(false, NoDeviceMessage);
        }
        catch (Exception ex)
        {
            CleanupFailedStart(session, capture, device);
            Log.Warn("Audio loopback capture failed to start", ex);
            RegisterFailure(ex.Message, 0);
        }
    }

    private void CleanupFailedStart(CaptureSession? session, WasapiLoopbackCapture? capture, MMDevice? device)
    {
        _isCapturing = false;
        if (session is not null)
        {
            if (ReferenceEquals(_session, session)) _session = null;
            session.Shutdown();
        }
        else
        {
            DisposeQuietly(capture);
            DisposeQuietly(device);
        }
        ResetFrontBuffer();
    }

    /// <summary>Stops and releases the current capture session. Caller holds <see cref="_controlGate"/>.</summary>
    private void StopCaptureCore(bool publish)
    {
        var session = _session;
        if (session is null) return;

        _session = null;
        _isCapturing = false;
        _autoWatch?.Dispose();
        _autoWatch = null;
        session.Shutdown();
        ResetFrontBuffer();

        Log.Info("Audio loopback capture stopped");
        if (publish) _bus.Publish(new AudioCaptureStateChanged(false, null));
    }

    // ───────────────────────── failures / notifications ─────────────────────────

    /// <summary>NAudio capture thread: the stream ended without being asked to (device invalidated, format change…).</summary>
    private void OnSessionFailed(CaptureSession session, Exception? error)
    {
        if (!ReferenceEquals(_session, session)) return;
        _isCapturing = false;
        var reason = error?.Message ?? StoppedBySystemMessage;
        Log.Warn("Audio loopback capture stopped unexpectedly: " + reason, error);
        RegisterFailure(reason, session.StartedTimestamp);
    }

    /// <summary>Counts a failure and schedules a restart with back-off, or gives up after the quick retries.</summary>
    private void RegisterFailure(string reason, long startedTimestamp)
    {
        int attempt;
        bool giveUp;
        lock (_failureGate)
        {
            // A capture that ran fine for a while earns a fresh set of quick retries.
            if (startedTimestamp != 0 && (Stopwatch.GetTimestamp() - startedTimestamp) * TicksToSeconds > StableRunSeconds)
                _retryCount = 0;
            attempt = ++_retryCount;
            giveUp = attempt > MaxQuickRetries;
            if (giveUp) _faulted = true;
        }

        _bus.Publish(new AudioCaptureStateChanged(false, reason));
        Interlocked.Exchange(ref _restartFlag, 1);
        if (giveUp)
        {
            PublishStatus(false, "Не удалось захватить системный звук: " + reason);
            ScheduleReconcile(0);
        }
        else
        {
            ScheduleReconcile(RetryBaseDelayMs * attempt);
        }
    }

    private void SetFaulted(string reason)
    {
        lock (_failureGate) _faulted = true;
        Log.Warn("Audio loopback unavailable: " + reason);
        _bus.Publish(new AudioCaptureStateChanged(false, reason));
        PublishStatus(false, reason);
    }

    private void ResetFailures()
    {
        lock (_failureGate)
        {
            _faulted = false;
            _retryCount = 0;
        }
    }

    /// <summary>COM notification thread: must return quickly and must not call into Core Audio.</summary>
    private void OnDefaultRenderDeviceChanged()
    {
        if (IsDisposed) return;
        ResetFailures();
        Interlocked.Exchange(ref _restartFlag, 1);
        ScheduleReconcile(DeviceChangeDebounceMs);
    }

    /// <summary>COM notification thread: a device appeared/disappeared — retry if we are failed or have nothing.</summary>
    private void OnDeviceAvailabilityChanged()
    {
        if (IsDisposed) return;
        bool faulted;
        lock (_failureGate) faulted = _faulted;
        if (faulted || _session is null)
        {
            ResetFailures();
            ScheduleReconcile(DeviceChangeDebounceMs);
        }
    }

    private void OnSettingsChanged(SettingsChanged e)
    {
        var features = e.Settings.Features;
        _sensitivity = SanitizeSensitivity(features.VisualizerSensitivity);

        var deviceId = features.VisualizerDeviceId ?? "";
        if (!string.Equals(_deviceId, deviceId, StringComparison.Ordinal))
        {
            // Another endpoint was chosen: restart capture on it (if capturing).
            _deviceId = deviceId;
            _autoDeviceId = null;
            ResetFailures();
            Interlocked.Exchange(ref _restartFlag, 1);
            ScheduleReconcile(0);
        }

        var enabled = features.VisualizerEnabled;
        if (_enabled == enabled) return;
        _enabled = enabled;

        if (enabled) ResetFailures();
        if (_running) PublishStatus(true, enabled ? null : DisabledMessage);
        ScheduleReconcile(0);
    }

    /// <summary>Capture thread: remembers that real (non-silent) audio arrived.</summary>
    private void NoteSignal(long timestamp) => Interlocked.Exchange(ref _lastSignalTimestamp, timestamp);

    /// <summary>
    /// Auto mode watchdog (thread pool, 1 Hz, only while capturing with no device chosen).
    /// Virtual mixers such as SteelSeries Sonar or Voicemeeter route apps to different endpoints, so the default
    /// device can be silent while music plays elsewhere. When the captured endpoint has been silent for a moment and
    /// another endpoint's peak meter shows sound, capture moves there.
    /// </summary>
    private void OnAutoWatch(object? state)
    {
        try
        {
            if (IsDisposed || !string.IsNullOrEmpty(_deviceId)) return;
            var session = _session;
            if (session is null) return;

            var silentFor = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastSignalTimestamp)) * TicksToSeconds;
            if (silentFor < AutoSwitchSilenceSeconds) return;

            string currentId;
            try { currentId = session.Device.ID; } catch { return; }

            var loudest = FindLoudestRenderDevice(currentId);
            if (loudest is null) return;

            Log.Info($"Audio loopback: '{loudest.Value.Name}' is playing while the captured device is silent — switching");
            _autoDeviceId = loudest.Value.Id;
            Interlocked.Exchange(ref _restartFlag, 1);
            ScheduleReconcile(0);
        }
        catch (Exception ex)
        {
            Log.Debug("Audio auto-device watchdog failed: " + ex.Message);
        }
    }

    /// <summary>Active render endpoint (other than <paramref name="excludeId"/>) with the highest audible peak, if any.</summary>
    private static (string Id, string Name)? FindLoudestRenderDevice(string excludeId)
    {
        using var enumerator = new MMDeviceEnumerator();
        (string Id, string Name)? best = null;
        var bestPeak = AudiblePeak;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    if (string.Equals(device.ID, excludeId, StringComparison.OrdinalIgnoreCase)) continue;
                    var peak = device.AudioMeterInformation.MasterPeakValue;
                    if (peak > bestPeak)
                    {
                        bestPeak = peak;
                        best = (device.ID, TryGetFriendlyName(device));
                    }
                }
                catch
                {
                    // Endpoint vanished or has no meter: skip.
                }
            }
        }
        return best;
    }

    /// <summary>
    /// The endpoint to capture: the configured one when it exists and is active, otherwise the default
    /// multimedia render device.
    /// </summary>
    private static MMDevice ResolveCaptureDevice(MMDeviceEnumerator enumerator, string deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active && device.DataFlow == DataFlow.Render) return device;
                device.Dispose();
                Log.Info("Audio loopback: configured device is not active, using the default device");
            }
            catch (Exception ex)
            {
                Log.Info("Audio loopback: configured device not found, using the default device (" + ex.Message + ")");
            }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>Active audio output devices (ID, friendly name) for the settings UI. Never throws.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetRenderDevices()
    {
        var result = new List<KeyValuePair<string, string>>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    string name;
                    try { name = device.FriendlyName; } catch { name = device.ID; }
                    result.Add(new KeyValuePair<string, string>(device.ID, name));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not enumerate audio output devices", ex);
        }
        return result;
    }

    private void PublishStatus(bool isWorking, string? message)
    {
        if (IsDisposed) return;
        lock (_statusGate)
        {
            if (_hasStatus && _lastStatusOk == isWorking && _lastStatusMessage == message) return;
            _hasStatus = true;
            _lastStatusOk = isWorking;
            _lastStatusMessage = message;
        }
        _bus.Publish(new ModuleStatusChanged(Name, isWorking, message));
    }

    // ───────────────────────── frames ─────────────────────────

    private void PublishFrame(CaptureSession session, ReadOnlySpan<float> bands, float level, long timestamp)
    {
        lock (_frameGate)
        {
            if (!ReferenceEquals(_session, session)) return;
            var count = Math.Min(bands.Length, _frontBands.Length);
            bands.Slice(0, count).CopyTo(_frontBands);
            _frontLevel = level;
            _frontTimestamp = timestamp;
            _hasFrame = true;
            Interlocked.Increment(ref _frameNumber);
        }
    }

    private void ResetFrontBuffer()
    {
        lock (_frameGate)
        {
            Array.Clear(_frontBands);
            _frontLevel = 0f;
            _hasFrame = false;
        }
    }

    private void ReleaseConsumer()
    {
        // Timestamp first, so a concurrent reconcile never sees "0 consumers" with a stale release time.
        Interlocked.Exchange(ref _lastReleaseTimestamp, Stopwatch.GetTimestamp());
        if (Interlocked.Decrement(ref _consumers) == 0)
            ScheduleReconcile(StopDelayMs);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static float SanitizeSensitivity(double value)
        => double.IsFinite(value) ? (float)Math.Clamp(value, 0.1, 4.0) : 1f;

    private static string TryGetFriendlyName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return "default render device"; }
    }

    /// <summary>Disposes when the object is IDisposable (works across NAudio versions), logging failures.</summary>
    private static void DisposeQuietly(object? instance)
    {
        if (instance is not IDisposable disposable) return;
        try { disposable.Dispose(); }
        catch (Exception ex) { Log.Warn($"Disposing {instance.GetType().Name} failed", ex); }
    }

    private sealed class ConsumerToken : IDisposable
    {
        private AudioAnalyzer? _owner;

        public ConsumerToken(AudioAnalyzer owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseConsumer();
    }

    /// <summary>
    /// One running loopback stream plus its analysis thread. Created and shut down under the owner's control gate.
    /// <para>
    /// Play-out: WASAPI hands over audio in bursts (up to ~50 ms). Instead of analysing each burst once, the analysis
    /// thread moves a cursor through the newest burst in real time, so frames stay smooth at display rate
    /// (costs at most one burst of latency, which roughly matches the output device latency anyway).
    /// </para>
    /// </summary>
    private sealed class CaptureSession
    {
        private readonly AudioAnalyzer _owner;
        private readonly SampleConverter _converter;
        private readonly FftProcessor _processor;
        private readonly SampleRingBuffer _ring;
        private readonly float[] _window;
        private readonly object _ringGate = new();
        private readonly object _wakeGate = new();
        private float[] _mono;
        private Thread? _analysisThread;
        private long _chunkStart;          // _ringGate
        private long _chunkTimestamp;      // _ringGate
        private long _lastDataTimestamp;   // _ringGate
        private bool _resetPending;        // _ringGate
        private bool _wakeRequested;       // _wakeGate
        private volatile bool _pending;
        private volatile bool _stopRequested;
        private volatile bool _failed;
        private bool _dataErrorLogged;

        public CaptureSession(AudioAnalyzer owner, MMDevice device, WasapiLoopbackCapture capture, SampleConverter converter)
        {
            _owner = owner;
            Device = device;
            Capture = capture;
            _converter = converter;

            // 2048 points ≈ 23 Hz resolution at 44.1/48 kHz; keep similar resolution at high sample rates.
            var fftSize = converter.SampleRate >= 88200 ? 4096 : 2048;
            _processor = new FftProcessor(fftSize, DefaultBandCount, converter.SampleRate);
            _window = new float[fftSize];
            _ring = new SampleRingBuffer(Math.Max(fftSize * 4, converter.SampleRate / 2 + fftSize));
            _mono = new float[Math.Max(1024, converter.SampleRate / 5)];
        }

        public MMDevice Device { get; }

        public WasapiLoopbackCapture Capture { get; }

        public long StartedTimestamp { get; private set; }

        public bool Failed => _failed;

        /// <summary>Starts the analysis thread and the WASAPI stream (throws on failure; caller cleans up).</summary>
        public void Start()
        {
            StartedTimestamp = Stopwatch.GetTimestamp();
            _analysisThread = new Thread(AnalysisLoop)
            {
                IsBackground = true,
                Name = "AudioAnalyzer",
                Priority = ThreadPriority.AboveNormal,
            };
            _analysisThread.Start();

            Capture.DataAvailable += OnDataAvailable;
            Capture.RecordingStopped += OnRecordingStopped;
            Capture.StartRecording();
        }

        /// <summary>Stops the stream, joins the analysis thread and releases COM objects. Idempotent.</summary>
        public void Shutdown()
        {
            _stopRequested = true;

            try { Capture.StopRecording(); }
            catch (Exception ex) { Log.Warn("WASAPI StopRecording failed", ex); }
            DisposeQuietly(Capture);
            try
            {
                Capture.DataAvailable -= OnDataAvailable;
                Capture.RecordingStopped -= OnRecordingStopped;
            }
            catch (Exception ex)
            {
                Log.Warn("Detaching WASAPI handlers failed", ex);
            }

            Wake();
            var thread = _analysisThread;
            if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive && !thread.Join(1000))
                Log.Warn("Audio analysis thread did not exit within 1 s");

            // The device owns the IAudioClient used by the capture: release it last.
            DisposeQuietly(Device);
        }

        /// <summary>UI thread (via TryGetBands): wake the analysis thread when there is audio not yet analysed.</summary>
        public void RequestFrameIfPending()
        {
            if (_pending && !_stopRequested) Wake();
        }

        private void Wake()
        {
            lock (_wakeGate)
            {
                _wakeRequested = true;
                Monitor.Pulse(_wakeGate);
            }
        }

        /// <summary>NAudio capture thread. Converts to mono and appends to the ring — no analysis here.</summary>
        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_stopRequested) return;
            try
            {
                var buffer = e.Buffer;
                if (buffer is null) return;
                var bytes = Math.Min(e.BytesRecorded, buffer.Length);
                var frames = _converter.FrameCount(bytes);
                if (frames <= 0) return;

                if (_mono.Length < frames) _mono = new float[Math.Max(frames, _mono.Length * 2)];
                var count = _converter.ToMono(new ReadOnlySpan<byte>(buffer, 0, bytes), _mono);
                if (count <= 0) return;

                var now = Stopwatch.GetTimestamp();
                for (var i = 0; i < count; i++)
                {
                    var v = _mono[i];
                    if (v > 1e-4f || v < -1e-4f)
                    {
                        _owner.NoteSignal(now);
                        break;
                    }
                }
                lock (_ringGate)
                {
                    if (_lastDataTimestamp != 0 && (now - _lastDataTimestamp) * TicksToSeconds > GapResetSeconds)
                    {
                        // Playback resumed after a pause: start from rest instead of from stale history.
                        _ring.Clear();
                        _resetPending = true;
                    }
                    _chunkStart = _ring.TotalWritten;
                    _ring.Write(new ReadOnlySpan<float>(_mono, 0, count));
                    _chunkTimestamp = now;
                    _lastDataTimestamp = now;
                }

                _pending = true;
                Wake();
            }
            catch (Exception ex)
            {
                if (!_dataErrorLogged)
                {
                    _dataErrorLogged = true;
                    Log.Warn("Audio sample conversion failed", ex);
                }
            }
        }

        /// <summary>NAudio capture thread (no SynchronizationContext was captured: created on the thread pool).</summary>
        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_stopRequested) return;
            _failed = true;
            try
            {
                _owner.OnSessionFailed(this, e.Exception);
            }
            catch (Exception ex)
            {
                Log.Warn("Audio failure handling failed", ex);
            }
        }

        /// <summary>Dedicated analysis thread: at most ~60 FFT frames per second, idle when no audio arrives.</summary>
        private void AnalysisLoop()
        {
            var minInterval = (long)(MinFrameIntervalSeconds * Stopwatch.Frequency);
            var sampleRate = _converter.SampleRate;
            long lastCursor = 0;
            long lastFrameTimestamp = 0;
            long lastTraceTimestamp = 0;

            try
            {
                while (!_stopRequested)
                {
                    lock (_wakeGate)
                    {
                        if (!_wakeRequested) Monitor.Wait(_wakeGate, AnalysisIdleWaitMs);
                        _wakeRequested = false;
                    }
                    if (_stopRequested) break;

                    var now = Stopwatch.GetTimestamp();
                    if (lastFrameTimestamp != 0 && now - lastFrameTimestamp < minInterval)
                        continue; // too soon; the next display poll or data burst wakes us again

                    long cursor;
                    bool reset;
                    lock (_ringGate)
                    {
                        var written = _ring.TotalWritten;
                        if (written <= lastCursor)
                        {
                            _pending = false;
                            continue;
                        }

                        var elapsed = (now - _chunkTimestamp) * TicksToSeconds;
                        var target = _chunkStart + (long)(Math.Max(0, elapsed) * sampleRate);
                        cursor = Math.Clamp(target, _chunkStart, written);
                        cursor = Math.Max(cursor, lastCursor);
                        cursor = Math.Max(cursor, written - (_ring.Capacity - _window.Length));
                        if (cursor <= lastCursor) continue;

                        _ring.CopyWindow(cursor, _window);
                        reset = _resetPending;
                        _resetPending = false;
                        _pending = cursor < written;
                    }

                    if (reset) _processor.ResetSignal();
                    _processor.Sensitivity = _owner._sensitivity;
                    _processor.AnalyzeWindow(_window);
                    lastCursor = cursor;
                    lastFrameTimestamp = now;

                    _owner.PublishFrame(this, _processor.Bands, _processor.Level, now);

                    // Debug-build diagnostics: one line every ~3 s while frames are produced.
                    if ((now - lastTraceTimestamp) * TicksToSeconds >= 3)
                    {
                        lastTraceTimestamp = now;
                        var bands = _processor.Bands;
                        var peak = 0f;
                        for (var i = 0; i < bands.Length; i++) peak = Math.Max(peak, bands[i]);
                        Log.Debug($"Audio: frames={_processor.FrameCount} level={_processor.Level:0.000} peakBand={peak:0.000} gainDb={_processor.CurrentGainDb:0.0}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Audio analysis thread failed", ex);
            }
        }
    }
}
