using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using WinAppInfo = Windows.ApplicationModel.AppInfo;
using WinNotifications = Windows.UI.Notifications;
using WinNotificationsManagement = Windows.UI.Notifications.Management;
using WinSize = Windows.Foundation.Size;
using WpfApplication = System.Windows.Application;

namespace WindowsDynamicIsland.SystemIntegration;

/// <summary>
/// Mirrors Windows toast notifications into the island (<c>Features.NotificationsEnabled</c>).
///
/// Uses <c>UserNotificationListener.Current</c>: <c>RequestAccessAsync()</c>, then — because the
/// <c>NotificationChanged</c> event is not available to unpackaged desktop apps — polls
/// <c>GetNotificationsAsync(NotificationKinds.Toast)</c> every 1.5 s while enabled and diffs by notification Id.
/// Notifications already present when listening starts are recorded silently. For each new toast:
/// app display name and 32 px logo (cached per app, frozen <see cref="BitmapImage"/>), the ToastGeneric binding's
/// first text element as the title and the rest as the body. Calls/alarms get <see cref="NotificationPriority.High"/>.
/// Identical toasts (app + title + body) within 3 s are dropped.
///
/// Limitations: Windows grants notification access only when "Allow apps to access your notifications" is on;
/// on some builds an unpackaged app without package identity is refused (or the API throws) — the module then
/// reports why in Diagnostics and stays idle.
/// </summary>
public sealed class NotificationManager : IIslandModule
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(3);
    /// <summary>Toasts older than this when first seen (e.g. synced into Action Center after resume) are not shown.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private const string DeniedMessage =
        "Нет доступа к уведомлениям: откройте «Параметры → Конфиденциальность и защита → Уведомления» и включите " +
        "«Разрешить приложениям доступ к уведомлениям», затем перезапустите Dynamic Island.";
    private const string NoIdentityMessage =
        "Windows не дала доступ к уведомлениям этой (неупакованной) версии приложения. " +
        "Проверьте «Параметры → Конфиденциальность → Уведомления»; если доступ уже разрешён — нужна упакованная (MSIX) версия.";

    private static readonly string[] HighPriorityMarkers =
    {
        "звонок", "входящий вызов", "вызов", "будильник", "call", "alarm", "ringing",
    };

    private readonly IEventBus _bus;
    private readonly FeatureLifecycle _lifecycle;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ImageSource?> _logoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pollCts;
    private HashSet<uint> _known = new();

    public NotificationManager(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _lifecycle = new FeatureLifecycle(Name, bus, settings,
            s => s.Features.NotificationsEnabled,
            _ => "",
            ActivateAsync,
            Deactivate);
    }

    /// <inheritdoc />
    public string Name => "Уведомления";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _lifecycle.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync() => _lifecycle.StopAsync();

    private async Task<FeatureStatus> ActivateAsync(AppSettings settings, CancellationToken ct)
    {
        WinNotificationsManagement.UserNotificationListener listener;
        try
        {
            listener = WinNotificationsManagement.UserNotificationListener.Current;
            var access = await listener.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
            if (access != WinNotificationsManagement.UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Info($"Notifications: access status {access}");
                return FeatureStatus.Unavailable(DeniedMessage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: UserNotificationListener unavailable", ex);
            return FeatureStatus.Unavailable(NoIdentityMessage + " (" + ex.Message + ")");
        }

        IReadOnlyList<WinNotifications.UserNotification> initial;
        try
        {
            initial = await listener.GetNotificationsAsync(WinNotifications.NotificationKinds.Toast).AsTask(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: GetNotificationsAsync failed", ex);
            return FeatureStatus.Unavailable("Не удалось прочитать уведомления Windows: " + ex.Message);
        }

        var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _known = new HashSet<uint>(initial.Select(n => n.Id));
            _recent.Clear();
            _pollCts = pollCts;
        }

        var token = pollCts.Token;
        _ = Task.Run(() => PollLoopAsync(listener, token), CancellationToken.None);
        return FeatureStatus.Ok();
    }

    private async Task PollLoopAsync(WinNotificationsManagement.UserNotificationListener listener, CancellationToken token)
    {
        var consecutiveFailures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                var list = await listener.GetNotificationsAsync(WinNotifications.NotificationKinds.Toast).AsTask(token).ConfigureAwait(false);
                consecutiveFailures = 0;
                await ProcessAsync(list, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1) Log.Warn("Notifications: poll failed", ex);

                if (IsAccessRevoked(listener))
                {
                    _lifecycle.PublishStatus(false, DeniedMessage);
                    return; // re-enabling the feature retries
                }

                // Back off on persistent errors (max ~30 s) without spinning.
                if (consecutiveFailures > 3)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, consecutiveFailures * 3)), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
    }

    private static bool IsAccessRevoked(WinNotificationsManagement.UserNotificationListener listener)
    {
        try
        {
            return listener.GetAccessStatus() != WinNotificationsManagement.UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ProcessAsync(IReadOnlyList<WinNotifications.UserNotification> list, CancellationToken token)
    {
        List<WinNotifications.UserNotification> fresh;
        lock (_gate)
        {
            var current = new HashSet<uint>(list.Count);
            fresh = new List<WinNotifications.UserNotification>();
            foreach (var n in list)
            {
                current.Add(n.Id);
                if (!_known.Contains(n.Id)) fresh.Add(n);
            }
            // Keep only ids still present so the set cannot grow without bound.
            _known = current;
        }

        if (fresh.Count == 0) return;

        var now = DateTimeOffset.Now;
        foreach (var userNotification in fresh.OrderBy(SafeCreationTime))
        {
            if (token.IsCancellationRequested) return;
            try
            {
                if (now - SafeCreationTime(userNotification) > MaxAge) continue;
                var notification = await BuildAsync(userNotification).ConfigureAwait(false);
                if (notification is null || token.IsCancellationRequested) continue;
                if (IsDuplicate(notification)) continue;
                _bus.Publish(new NotificationReceived(notification));
            }
            catch (Exception ex)
            {
                Log.Warn("Notifications: failed to read a toast", ex);
            }
        }
    }

    private static DateTimeOffset SafeCreationTime(WinNotifications.UserNotification n)
    {
        try { return n.CreationTime; } catch { return DateTimeOffset.Now; }
    }

    private async Task<IslandNotification?> BuildAsync(WinNotifications.UserNotification userNotification)
    {
        WinAppInfo? appInfo = null;
        try { appInfo = userNotification.AppInfo; } catch { /* some system toasts have no AppInfo */ }

        var appName = "";
        var appKey = "";
        try { appName = appInfo?.DisplayInfo?.DisplayName ?? ""; } catch { /* ignore */ }
        try { appKey = appInfo?.AppUserModelId ?? ""; } catch { /* ignore */ }
        if (appKey.Length == 0) appKey = appName;

        if (IsOwnApp(appName, appKey)) return null;

        var (title, body) = ReadTexts(userNotification);
        if (title.Length == 0 && body.Length == 0) return null;

        var icon = appInfo is null || appKey.Length == 0 ? null : await GetLogoAsync(appInfo, appKey).ConfigureAwait(false);

        return new IslandNotification
        {
            Id = "toast:" + userNotification.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AppName = appName.Length > 0 ? appName : "Уведомление",
            AppIcon = icon,
            Glyph = Glyphs.Notification,
            Title = title.Length > 0 ? title : appName,
            Body = body,
            Timestamp = SafeCreationTime(userNotification),
            Priority = IsHighPriority(title) ? NotificationPriority.High : NotificationPriority.Normal,
            Source = NotificationSource.System,
        };
    }

    private static (string Title, string Body) ReadTexts(WinNotifications.UserNotification userNotification)
    {
        try
        {
            var visual = userNotification.Notification?.Visual;
            if (visual is null) return ("", "");

            var binding = visual.GetBinding(WinNotifications.KnownNotificationBindings.ToastGeneric)
                          ?? visual.Bindings.FirstOrDefault();
            if (binding is null) return ("", "");

            var texts = binding.GetTextElements()
                .Select(t => t.Text?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .ToList();
            if (texts.Count == 0) return ("", "");
            return (texts[0], string.Join("\n", texts.Skip(1)));
        }
        catch (Exception ex)
        {
            Log.Debug("Notifications: cannot read toast text: " + ex.Message);
            return ("", "");
        }
    }

    private static bool IsOwnApp(string appName, string appKey)
        => appName.Contains("Dynamic Island", StringComparison.OrdinalIgnoreCase)
           || appKey.Contains("DynamicIsland", StringComparison.OrdinalIgnoreCase);

    private static bool IsHighPriority(string title)
    {
        if (title.Length == 0) return false;
        return HighPriorityMarkers.Any(m => title.Contains(m, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool IsDuplicate(IslandNotification n)
    {
        var key = n.AppName + "\u001f" + n.Title + "\u001f" + n.Body;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var stale in _recent.Where(kv => now - kv.Value > DedupeWindow).Select(kv => kv.Key).ToList())
                _recent.Remove(stale);
            if (_recent.ContainsKey(key)) return true;
            _recent[key] = now;
            return false;
        }
    }

    private async Task<ImageSource?> GetLogoAsync(WinAppInfo appInfo, string appKey)
    {
        if (_logoCache.TryGetValue(appKey, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var reference = appInfo.DisplayInfo.GetLogo(new WinSize(32, 32));
            if (reference is not null)
            {
                using var winStream = await reference.OpenReadAsync().AsTask().ConfigureAwait(false);
                using var managed = winStream.AsStreamForRead();
                using var memory = new MemoryStream();
                await managed.CopyToAsync(memory).ConfigureAwait(false);
                if (memory.Length > 0)
                {
                    var bytes = memory.ToArray();
                    // WPF imaging objects capture Dispatcher.CurrentDispatcher; decoding on a pool thread would create
                    // a dispatcher (and hidden window) for that thread. Decode on the UI thread — a 32 px logo is cheap.
                    var dispatcher = WpfApplication.Current?.Dispatcher;
                    if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                        image = await dispatcher.InvokeAsync(() => DecodeFrozen(bytes)).Task.ConfigureAwait(false);
                    else
                        image = DecodeFrozen(bytes);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Notifications: no logo for {appKey}: {ex.Message}");
            image = null;
        }

        // Cache misses too, so a broken logo is not retried on every toast.
        _logoCache[appKey] = image;
        return image;
    }

    private static ImageSource? DecodeFrozen(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Debug("Notifications: logo decode failed: " + ex.Message);
            return null;
        }
    }

    private void Deactivate()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _pollCts;
            _pollCts = null;
            _known = new HashSet<uint>();
            _recent.Clear();
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lifecycle.Dispose();
}
