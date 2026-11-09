using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WindowsDynamicIsland.UI.Shell;

/// <summary>
/// Notification-area icon with a dark context menu: expand, timers, visibility, settings, exit.
/// Everything is routed through the event bus — the tray owns no app logic.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly IEventBus _bus;
    private readonly SettingsManager _settings;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly IDisposable _settingsSub;
    private bool _disposed;

    public TrayIcon(IEventBus bus, SettingsManager settings)
    {
        _bus = bus;
        _settings = settings;

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            Font = new Drawing.Font("Segoe UI", 9.5f),
            ForeColor = Drawing.Color.White,
            BackColor = Drawing.Color.FromArgb(28, 28, 30),
        };
        _menu.Opening += (_, _) => RebuildMenu();

        _icon = new Forms.NotifyIcon
        {
            Text = "Dynamic Island",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = settings.Current.System.ShowTrayIcon,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _bus.Publish(new IslandActionRequested(IslandUserAction.ToggleExpanded));
        };
        _icon.DoubleClick += (_, _) => _bus.Publish(new OpenSettingsRequested());

        _settingsSub = bus.Subscribe<SettingsChanged>(e =>
        {
            if (!_disposed) _icon.Visible = e.Settings.System.ShowTrayIcon;
        }, onUiThread: true);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(Item("Развернуть / свернуть", () => _bus.Publish(new IslandActionRequested(IslandUserAction.ToggleExpanded))));
        _menu.Items.Add(Item("Показать / скрыть остров", () => _bus.Publish(new IslandActionRequested(IslandUserAction.ToggleVisibility))));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var timer = _bus.GetLast<TimerStateChanged>();
        if (timer is { IsActive: true })
        {
            _menu.Items.Add(Item(timer.State == TimerRunState.Running ? "Пауза таймера" : "Продолжить таймер",
                () => _bus.Publish(new TimerCommand(TimerCommandKind.TogglePause))));
            _menu.Items.Add(Item("Остановить таймер", () => _bus.Publish(new TimerCommand(TimerCommandKind.Stop))));
        }
        else
        {
            _menu.Items.Add(Item("Запустить секундомер", () => _bus.Publish(new TimerCommand(TimerCommandKind.StartStopwatch))));
            var timers = new Forms.ToolStripMenuItem("Таймер") { ForeColor = Drawing.Color.White };
            foreach (var minutes in _settings.Current.Timer.Presets)
            {
                var m = minutes;
                timers.DropDownItems.Add(Item(FormatMinutes(m),
                    () => _bus.Publish(new TimerCommand(TimerCommandKind.StartCountdown, TimeSpan.FromMinutes(m)))));
            }
            _menu.Items.Add(timers);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Item("История буфера обмена", () => _bus.Publish(new OpenClipboardHistoryRequested())));
        _menu.Items.Add(Item("Настройки…", () => _bus.Publish(new OpenSettingsRequested())));
        _menu.Items.Add(Item("Выход", () => _bus.Publish(new ExitRequested())));
    }

    private static string FormatMinutes(double m)
        => m < 1 ? $"{Math.Round(m * 60)} сек" : $"{m:0.#} мин";

    private static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = Drawing.Color.White };
        item.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Log.Error("Tray menu action failed", ex); }
        };
        return item;
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DynamicIsland;component/Assets/island.ico"));
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    var size = Forms.SystemInformation.SmallIconSize;
                    return new Drawing.Icon(stream, size);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Tray icon resource missing, using default icon", ex);
        }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settingsSub.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(28, 28, 30);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(58, 58, 60);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(58, 58, 60);

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
    }
}
