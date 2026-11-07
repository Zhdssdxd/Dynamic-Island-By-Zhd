# Windows Dynamic Island — архитектура и контракты модулей

Документ ведёт Orchestrator (Agent 1). Это обязательный контракт для всех агентов. Любое изменение
публичного API, которого нет здесь, согласуется с Orchestrator (в отчёте: «прошу изменить …»).

## 1. Стек

* C# 12, .NET 8, **WPF** (`net8.0-windows10.0.19041.0`, минимум Windows 10 1809). WinRT API (GSMTC,
  Bluetooth, UserNotificationListener, Battery) доступны напрямую через проекции CsWinRT этого TFM.
* NuGet: `NAudio.Wasapi 2.2.1` (WASAPI loopback, CoreAudio API), `System.Management 8.0.0` (WMI).
  Других пакетов не добавлять без согласования.
* `UseWindowsForms=true` — только для `System.Windows.Forms.Screen` и `NotifyIcon`.
  **Никогда не писать `using System.Windows.Forms;` или `using System.Drawing;`** — только алиасы
  (`using Forms = System.Windows.Forms;`) или полные имена. Иначе конфликты `Application`, `MessageBox`, `Color`, `Point`…
* `ImplicitUsings` выключен; глобально подключены: `System`, `System.Collections.Generic`, `System.IO`,
  `System.Linq`, `System.Threading`, `System.Threading.Tasks`. Остальные `using` пишите сами.
* `Nullable` включён. File-scoped namespaces. Сборка: `AssemblyName=DynamicIsland` (pack URI:
  `pack://application:,,,/DynamicIsland;component/...`).
* В окружении разработки нет .NET SDK — код нельзя скомпилировать. Пишите максимально аккуратно,
  перепроверяйте сигнатуры API (WPF, WinRT, NAudio) и делайте само-ревью перед отчётом.

## 2. Пространства имён и владельцы файлов

Каталог проекта: `src/WindowsDynamicIsland/`. Каждый агент изменяет **только свои файлы**.

| Папка / файлы | Namespace | Владелец |
|---|---|---|
| `Core/**`, `App.xaml(.cs)`, `app.manifest`, `*.csproj`, `UI/Themes/Tokens.xaml`, `UI/Shell/**`, `Settings/SettingsModels.cs`, `Settings/SettingsManager.cs` | `WindowsDynamicIsland`, `.Core`, `.Core.Models`, `.UI.Shell`, `.Settings` | Agent 1 Orchestrator |
| `UI/**` (кроме Tokens.xaml и Shell) | `WindowsDynamicIsland.UI` (+ `.UI.Views`, `.UI.Components`, `.UI.Native`) | Agent 2 UI/UX |
| `Animation/**` | `WindowsDynamicIsland.Animation` | Agent 3 Animation |
| `Media/**` | `WindowsDynamicIsland.Media` | Agent 4 Media |
| `Audio/**` | `WindowsDynamicIsland.Audio` | Agent 5 Audio |
| `SystemIntegration/**`, `Downloads/**` | `WindowsDynamicIsland.SystemIntegration`, `.Downloads` | Agent 6 System |
| `Settings/**` (кроме двух файлов Orchestrator), `Timing/**`, `tests/**`, `build/**`, `installer/**` | `WindowsDynamicIsland.Settings`, `.Timing`, `WindowsDynamicIsland.Tests` | Agent 7 Settings/QA |

⚠ Namespace-ловушки: внутри `WindowsDynamicIsland.*` имена `Media`, `Animation`, `Settings`, `Audio` —
это **наши** пространства имён. Никогда не пишите частично квалифицированные имена вроде
`Media.Brush`, `Animation.Storyboard`, `Windows.Media.Control.X` внутри кода. Используйте `using`
на уровне файла (вне namespace) и короткие имена типов, или `global::`.
Свойство `AppSettings.System` и `AppSettings.Timer` — обычные свойства.

## 3. Ядро (уже реализовано Orchestrator — прочитайте исходники)

* `Core/EventBus.cs` — `IEventBus.Subscribe<T>(Action<T>, bool onUiThread=false)`, `Publish<T>(T)`,
  `GetLast<T>()`. Publish потокобезопасен. Подписки возвращают `IDisposable` — освобождайте в `Dispose`.
  `SubscriptionBag` — для группы подписок.
* `Core/IIslandModule.cs` — контракт модулей (`Name`, `StartAsync(ct)`, `StopAsync()`, `Dispose()`).
  Прочитайте XML-комментарий: конструктор дешёвый, StartAsync не бросает при недоступном API —
  публикует `ModuleStatusChanged(Name, false, "понятная причина по-русски")`, реагирует на `SettingsChanged`.
  При успешном старте тоже публикуйте `ModuleStatusChanged(Name, true, null)` (или с пояснением).
* Все модули имеют конструктор **`(IEventBus bus, SettingsManager settings)`**.
* `Core/Log.cs` — `Log.Info/Warn/Error/Debug`. `AppPaths`.
* `Core/ServiceRegistry.cs` — `ServiceRegistry.Get<T>()`.
* `Core/Models/*.cs` — **все общие модели и события**. Не создавайте дублирующие модели.
* `Core/IslandState.cs`, `Core/IslandController.cs` — «мозг»: принимает события, решает что показывать.
  UI биндингом читает `IslandState`. Действия пользователя — `controller.Handle(IslandUserAction)`
  или `bus.Publish(new IslandActionRequested(...))`.
* `Core/AppManager.cs` — composition root: создаёт всё перечисленное ниже **по этим именам классов**.
* `Settings/SettingsManager.cs` — `Current` (снимок, не мутировать), `Update(Action<AppSettings>)`.
* `UI/Themes/Tokens.xaml` — дизайн-токены (цвета, шрифты, отступы). `IconFont` + `Core.Models.Glyphs`.

Потоки: WinRT/NAudio/WMI-события приходят в фоновых потоках — просто `bus.Publish(...)` (bus сам
маршалит для `onUiThread` подписчиков). `ImageSource` в моделях всегда `Freeze()`.

## 4. Публичные API, которые создают агенты (обязательные имена и сигнатуры)

### Agent 3 — Animation (`WindowsDynamicIsland.Animation`)
```csharp
public readonly record struct SpringParameters(double Stiffness, double Damping, double Mass = 1.0)
{   // response = период колебаний (сек), dampingRatio = ζ (1 = без перескока)
    public static SpringParameters FromResponse(double responseSeconds, double dampingRatio);
}
public static class SpringPresets { Smooth; Bouncy; Snappy; Gentle; }   // static SpringParameters
public sealed class Spring   // чистая математика, без WPF; тестируемая
{
    public Spring(double initialValue, SpringParameters parameters);
    public double Value { get; } public double Velocity { get; } public double Target { get; set; }
    public SpringParameters Parameters { get; set; }
    public bool IsSettled { get; }               // |x-target|<eps && |v|<eps
    public void Step(double dtSeconds);          // стабильно при любом dt (сабстепы)
    public void SnapTo(double value);
}
public sealed class AnimationEngine            // UI-поток, CompositionTarget.Rendering только пока есть активные анимации
{
    public static AnimationEngine Instance { get; }
    public double SpeedMultiplier { get; set; } // 0.5..2, влияет на все пружины и Transitions
    public bool IsRunning { get; }
    // key — любой объект; повторный вызов с тем же key ретаргетит пружину, сохраняя скорость
    public SpringHandle Animate(object key, double from, double to, Action<double> apply,
                                SpringParameters? parameters = null, Action? completed = null);
    // from берётся из текущего значения свойства (или из активной пружины)
    public SpringHandle AnimateProperty(DependencyObject target, DependencyProperty property, double to,
                                        SpringParameters? parameters = null, Action? completed = null);
    public void Stop(object key);
    public bool TryGetCurrent(object key, out double value);
}
public sealed class SpringHandle { public object Key { get; } public double Target { get; } public bool IsCompleted { get; } public void Cancel(); }
public static class Transitions                // WPF DoubleAnimation с easing (GPU-композиция opacity/transform)
{
    public static Task FadeInAsync(UIElement element, TimeSpan? duration = null, double fromScale = 0.94,
                                   double fromOffsetY = 0, TimeSpan? delay = null);
    public static Task FadeOutAsync(UIElement element, TimeSpan? duration = null, double toScale = 0.94,
                                    double toOffsetY = 0);
    public static void Pulse(UIElement element, double scale = 1.06);
    public static void Shake(UIElement element);   // «внимание» (таймер закончился)
    public static Task StaggerInAsync(IEnumerable<UIElement> elements, TimeSpan step);
}
public enum ContentTransition { Fade, ScaleFade, SlideUp, SlideDown, Blur }
public class MorphContentControl : ContentControl
{   // при смене Content или TransitionKey — старое содержимое уходит, новое появляется
    public static readonly DependencyProperty TransitionKeyProperty;   // object
    public static readonly DependencyProperty TransitionProperty;      // ContentTransition, default ScaleFade
    public static readonly DependencyProperty DurationProperty;        // TimeSpan, default 260ms (делится на SpeedMultiplier)
}
```

### Agent 5 — Audio (`WindowsDynamicIsland.Audio`)
```csharp
public sealed class AudioAnalyzer : IIslandModule, ISpectrumSource   // WASAPI loopback + FFT
public sealed class FftProcessor        // чистая математика: FFT radix-2, окно Ханна, лог-полосы. Тестируемая.
public sealed class VisualizerControl : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty;        // VisualizerStyle (Core.Models), НЕ "Style"
    public static readonly DependencyProperty IsActiveProperty;    // bool: false → плавное затухание и стоп рендера
    public static readonly DependencyProperty BarBrushProperty;    // Brush, default белый
    public static readonly DependencyProperty BarCountProperty;    // int, 0 = авто по режиму
    public static readonly DependencyProperty SensitivityProperty; // double, default 1
}
```
VisualizerControl сам берёт `ServiceRegistry.Get<ISpectrumSource>()`, вызывает `AcquireConsumer()` пока
`IsActive && IsVisible && IsLoaded`, рендерит через `OnRender`/`DrawingContext` и подписан на
`CompositionTarget.Rendering` только пока анимирует. Если источника нет — рисует полосы в покое (без рандома).

### Agent 4 — Media (`WindowsDynamicIsland.Media`)
`public sealed class MediaManager : IIslandModule` — публикует `MediaSessionsChanged`, обрабатывает `MediaCommand`.

### Agent 6 — System (`WindowsDynamicIsland.SystemIntegration`, `WindowsDynamicIsland.Downloads`)
`NotificationManager`, `VolumeMonitor`, `BrightnessMonitor`, `DeviceMonitor` (USB + Bluetooth),
`BatteryMonitor`, `MicrophoneMonitor`, `FullscreenMonitor` (namespace SystemIntegration),
`DownloadMonitor` (namespace Downloads) — все `public sealed class X : IIslandModule`.

### Agent 7 — Settings/Timer/QA
`WindowsDynamicIsland.Timing.TimerManager : IIslandModule` (обрабатывает `TimerCommand`, публикует `TimerStateChanged`).
`WindowsDynamicIsland.Settings.HotkeyManager : IIslandModule`, `AutostartManager : IIslandModule`.
`WindowsDynamicIsland.Settings.SettingsWindow : Window` с конструктором
`SettingsWindow(SettingsManager settings, IEventBus bus, ModuleStatusRegistry statuses)`.

### Agent 2 — UI (`WindowsDynamicIsland.UI`)
`public partial class IslandWindow : Window` с конструктором `IslandWindow(IslandController controller, SettingsManager settings)`.
`UI/Themes/Components.xaml` — ResourceDictionary общих стилей (App.xaml уже его подключает; файл обязан существовать).

## 5. Поведение (сводка для всех)

* Приоритеты отображения задаёт `IslandController` (см. комментарий в файле). UI только визуализирует `IslandState`.
* Никаких фиктивных данных: если API не даёт значения — поле пустое/null, UI его скрывает.
  Если функция недоступна на ПК — `ModuleStatusChanged(false, причина)`; настройки показывают это в «Диагностике».
* Ресурсы: ничего не опрашивать чаще необходимого; таймеры и захват звука выключаются, когда функция выключена
  или нечего показывать. В простое приложение должно потреблять ~0% CPU.
* Все тексты интерфейса — на русском.
