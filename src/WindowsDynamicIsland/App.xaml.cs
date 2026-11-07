using System.Windows;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\WindowsDynamicIsland.SingleInstance.v1";
    private const string ActivateEventName = @"Local\WindowsDynamicIsland.Activate.v1";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private AppManager? _manager;
    private int _shutdownStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Single instance: a second launch asks the running instance to open settings and exits.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
                signal.Set();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not signal the running instance", ex);
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        RegisterGlobalExceptionHandlers();
        Log.Info($"Starting Windows Dynamic Island {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}");

        try
        {
            _manager = new AppManager(this);
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
                (_, _) => Dispatcher.InvokeAsync(() => _manager?.ShowSettings()), null, Timeout.Infinite, executeOnlyOnce: false);

            await _manager.StartAsync(e.Args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal startup error", ex);
            MessageBox.Show("Не удалось запустить Dynamic Island:\n" + ex.Message, "Dynamic Island",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ShutdownAppAsync(1);
        }
    }

    /// <summary>Graceful shutdown: stops modules, saves settings, releases handles. Safe to call multiple times.</summary>
    public async Task ShutdownAppAsync(int exitCode = 0)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        Log.Info("Shutting down");
        try
        {
            if (_manager is not null) await _manager.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Error during shutdown", ex);
        }
        finally
        {
            _activateWait?.Unregister(null);
            _activateEvent?.Dispose();
            try { _singleInstance?.ReleaseMutex(); } catch { /* not owned */ }
            _singleInstance?.Dispose();
            Log.Flush();
            Shutdown(exitCode);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown: save synchronously-ish; the process may be killed soon after.
        base.OnSessionEnding(e);
        _ = ShutdownAppAsync();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true; // keep the island alive; the faulty operation is skipped
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled domain exception (terminating=" + args.IsTerminating + ")", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
