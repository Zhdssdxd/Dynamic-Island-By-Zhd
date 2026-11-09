using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.ClipboardHistory;

/// <summary>
/// Clipboard history: listens for WM_CLIPBOARDUPDATE (AddClipboardFormatListener on a message-only window), keeps the
/// last N text / image / file-list items in memory and can put any of them back on the clipboard.
/// Respects the opt-out formats password managers set (ExcludeClipboardContentFromMonitorProcessing,
/// CanIncludeInClipboardHistory = 0, Clipboard Viewer Ignore). Only pinned text / file items are saved to disk.
/// UI thread only (the history window binds <see cref="Entries"/> directly).
/// </summary>
public sealed class ClipboardHistoryManager : IIslandModule
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxTextLength = 200_000;
    private const int MaxImages = 12;
    private const int IndicatorPreviewLength = 22;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromMilliseconds(120);

    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private Dispatcher? _dispatcher;
    private HwndSource? _source;
    private bool _listening;
    private DispatcherTimer? _debounce;
    private IDisposable? _settingsSub;
    private bool _stopped;
    private bool _disposed;
    private bool _capturing;

    private static readonly string PinnedFile = Path.Combine(AppPaths.RoamingDataDir, "clipboard-pinned.json");

    public ClipboardHistoryManager(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _settings = settings;
    }

    public string Name => "История буфера обмена";

    /// <summary>Newest first. UI thread only.</summary>
    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            _bus.Publish(new ModuleStatusChanged(Name, false, "Нет UI-потока"));
            return;
        }
        _dispatcher = dispatcher;
        try
        {
            if (dispatcher.CheckAccess()) Initialize();
            else await dispatcher.InvokeAsync(Initialize).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard history failed to start", ex);
            _bus.Publish(new ModuleStatusChanged(Name, false, "Не удалось подписаться на буфер обмена: " + ex.Message));
        }
    }

    private void Initialize()
    {
        if (_stopped || _disposed) return;
        ServiceRegistry.Register(this);
        LoadPinned();
        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!) { Interval = CaptureDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce!.Stop();
            _ = CaptureAsync();
        };
        Apply(_settings.Current.Features);
        _settingsSub = _bus.Subscribe<SettingsChanged>(e => Apply(e.Settings.Features), onUiThread: true);
    }

    private void Apply(FeatureSettings f)
    {
        if (_stopped || _disposed) return;
        if (f.ClipboardHistoryEnabled) StartListening();
        else StopListening();
        Trim(f.ClipboardMaxItems);
    }

    private void StartListening()
    {
        if (_listening) return;
        try
        {
            if (_source is null)
            {
                _source = new HwndSource(new HwndSourceParameters("DynamicIslandClipboard")
                {
                    ParentWindow = HWND_MESSAGE,
                    WindowStyle = 0,
                    Width = 0,
                    Height = 0,
                });
                _source.AddHook(WndProc);
            }
            if (!AddClipboardFormatListener(_source.Handle))
                throw new InvalidOperationException("AddClipboardFormatListener: " + Marshal.GetLastWin32Error());
            _listening = true;
            _bus.Publish(new ModuleStatusChanged(Name, true, null));
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard listener failed", ex);
            _bus.Publish(new ModuleStatusChanged(Name, false, "Не удалось подписаться на буфер обмена"));
        }
    }

    private void StopListening()
    {
        if (_listening && _source is not null)
        {
            try { RemoveClipboardFormatListener(_source.Handle); } catch (Exception ex) { Log.Warn("RemoveClipboardFormatListener failed", ex); }
        }
        _listening = false;
        _debounce?.Stop();
        if (!_stopped) _bus.Publish(new ModuleStatusChanged(Name, true, "Выключено в настройках"));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _listening)
        {
            // Apps often write several formats in a row — capture once it has settled.
            _debounce?.Stop();
            _debounce?.Start();
        }
        return IntPtr.Zero;
    }

    // ───────────────────────────── Capture ─────────────────────────────

    private async Task CaptureAsync()
    {
        if (_capturing || !_listening) return;
        _capturing = true;
        try
        {
            if (IsExcludedByOwner()) return;

            IDataObject? data = null;
            for (var attempt = 0; attempt < 4 && data is null; attempt++)
            {
                try
                {
                    data = System.Windows.Clipboard.GetDataObject();
                }
                catch (COMException)
                {
                    await Task.Delay(40); // clipboard is held open by another process
                }
            }
            if (data is null || !CanIncludeInHistory(data)) return;

            var entry = Read(data);
            if (entry is null) return;
            Add(entry);
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard capture failed", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private static ClipboardEntry? Read(IDataObject data)
    {
        var now = DateTimeOffset.Now;
        var app = OwnerAppName();

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            return new ClipboardEntry(ClipboardEntryKind.Files, null, null, files, now, app);

        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];
            return new ClipboardEntry(ClipboardEntryKind.Text, text, null, null, now, app);
        }

        if (data.GetDataPresent(DataFormats.Bitmap) && System.Windows.Clipboard.GetImage() is BitmapSource image)
        {
            if (image.CanFreeze) image.Freeze();
            return new ClipboardEntry(ClipboardEntryKind.Image, null, image, null, now, app);
        }
        return null;
    }

    private void Add(ClipboardEntry entry)
    {
        var existing = Entries.FirstOrDefault(e => e.SameContentAs(entry));
        if (existing is not null)
        {
            existing.Touch();
            var index = Entries.IndexOf(existing);
            if (index > 0) Entries.Move(index, 0);
            return; // copied again (e.g. picked from the history) — no indicator
        }

        Entries.Insert(0, entry);
        Trim(_settings.Current.Features.ClipboardMaxItems);

        if (_settings.Current.Features.ClipboardIndicator)
        {
            var preview = entry.ShortPreview;
            if (preview.Length > IndicatorPreviewLength) preview = preview[..IndicatorPreviewLength].TrimEnd() + "…";
            _bus.Publish(new SystemIndicatorEvent
            {
                Kind = IndicatorKind.Clipboard,
                Glyph = entry.KindGlyph,
                Title = "Скопировано",
                Subtitle = preview,
            });
        }
    }

    /// <summary>Drops the oldest unpinned items beyond <paramref name="max"/> (and keeps at most a few images).</summary>
    private void Trim(int max)
    {
        var images = 0;
        var kept = 0;
        for (var i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            if (e.IsPinned) continue;
            kept++;
            if (e.IsImage) images++;
            if (kept > max || (e.IsImage && images > MaxImages))
            {
                Entries.RemoveAt(i);
                i--;
                kept--;
                if (e.IsImage) images--;
            }
        }
    }

    // ───────────────────────────── Actions (window) ─────────────────────────────

    /// <summary>Puts the entry back on the clipboard. Returns false if the clipboard stayed busy.</summary>
    public bool CopyToClipboard(ClipboardEntry entry)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                switch (entry.Kind)
                {
                    case ClipboardEntryKind.Text:
                        System.Windows.Clipboard.SetText(entry.Text, TextDataFormat.UnicodeText);
                        break;
                    case ClipboardEntryKind.Image when entry.Image is not null:
                        System.Windows.Clipboard.SetImage(entry.Image);
                        break;
                    case ClipboardEntryKind.Files:
                        var list = new StringCollection();
                        list.AddRange(entry.Files.ToArray());
                        System.Windows.Clipboard.SetFileDropList(list);
                        break;
                }
                entry.Touch();
                var index = Entries.IndexOf(entry);
                if (index > 0) Entries.Move(index, 0);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                Log.Warn("Clipboard write failed", ex);
                return false;
            }
        }
        return false;
    }

    public void Remove(ClipboardEntry entry)
    {
        Entries.Remove(entry);
        if (entry.IsPinned) SavePinned();
    }

    public void TogglePin(ClipboardEntry entry)
    {
        entry.IsPinned = !entry.IsPinned;
        SavePinned();
    }

    /// <summary>Removes everything except pinned items.</summary>
    public void ClearUnpinned()
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
            if (!Entries[i].IsPinned) Entries.RemoveAt(i);
    }

    // ───────────────────────────── Pinned persistence ─────────────────────────────

    private sealed record PinnedItem(string Kind, string? Text, string[]? Files, DateTimeOffset CopiedAt, string? SourceApp);

    private void LoadPinned()
    {
        try
        {
            if (!File.Exists(PinnedFile)) return;
            var items = JsonSerializer.Deserialize<List<PinnedItem>>(File.ReadAllText(PinnedFile)) ?? new();
            foreach (var p in items)
            {
                var kind = p.Kind == nameof(ClipboardEntryKind.Files) ? ClipboardEntryKind.Files : ClipboardEntryKind.Text;
                if (kind == ClipboardEntryKind.Text && string.IsNullOrEmpty(p.Text)) continue;
                if (kind == ClipboardEntryKind.Files && p.Files is not { Length: > 0 }) continue;
                Entries.Add(new ClipboardEntry(kind, p.Text, null, p.Files, p.CopiedAt, p.SourceApp) { IsPinned = true });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Loading pinned clipboard items failed", ex);
        }
    }

    private void SavePinned()
    {
        try
        {
            var items = Entries.Where(e => e.IsPinned && !e.IsImage)
                .Select(e => new PinnedItem(e.Kind.ToString(), e.IsText ? e.Text : null, e.IsFiles ? e.Files.ToArray() : null,
                    e.CopiedAt, e.SourceApp))
                .ToList();
            Directory.CreateDirectory(AppPaths.RoamingDataDir);
            File.WriteAllText(PinnedFile, JsonSerializer.Serialize(items));
        }
        catch (Exception ex)
        {
            Log.Warn("Saving pinned clipboard items failed", ex);
        }
    }

    // ───────────────────────────── Privacy / owner ─────────────────────────────

    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnoreFormat = RegisterClipboardFormat("Clipboard Viewer Ignore");

    private static bool IsExcludedByOwner()
    {
        try
        {
            return (ExcludeFormat != 0 && IsClipboardFormatAvailable(ExcludeFormat))
                   || (ViewerIgnoreFormat != 0 && IsClipboardFormatAvailable(ViewerIgnoreFormat));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows' own history opt-out: "CanIncludeInClipboardHistory" DWORD 0 → never record.</summary>
    private static bool CanIncludeInHistory(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent("CanIncludeInClipboardHistory")) return true;
            if (data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            {
                var buffer = new byte[4];
                ms.Position = 0;
                _ = ms.Read(buffer, 0, 4);
                return BitConverter.ToInt32(buffer, 0) != 0;
            }
        }
        catch
        {
            // unreadable → be safe and skip it
            return false;
        }
        return true;
    }

    private static string? OwnerAppName()
    {
        try
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return null;
            GetWindowThreadProcessId(owner, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var p = Process.GetProcessById((int)pid);
            try
            {
                var description = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch
            {
                // access denied for elevated processes — fall back to the process name
            }
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── Shutdown ─────────────────────────────

    public Task StopAsync()
    {
        _stopped = true;
        RunOnUi(Teardown);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopped = true;
        RunOnUi(Teardown);
    }

    private void Teardown()
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        _debounce?.Stop();
        if (_listening && _source is not null)
        {
            try { RemoveClipboardFormatListener(_source.Handle); } catch { /* shutting down */ }
        }
        _listening = false;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private void RunOnUi(Action action)
    {
        var d = _dispatcher;
        try
        {
            if (d is null || d.CheckAccess()) action();
            else if (!d.HasShutdownStarted) d.Invoke(action);
        }
        catch (Exception ex)
        {
            Log.Warn("Clipboard history teardown failed", ex);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
