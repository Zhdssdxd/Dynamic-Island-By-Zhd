using System.Collections.Concurrent;

namespace WindowsDynamicIsland.Core;

/// <summary>
/// Minimal service registry for the few cross-module services that are not events
/// (e.g. <see cref="Models.ISpectrumSource"/> consumed by the visualizer control which XAML instantiates).
/// Registration happens in AppManager before the UI is created.
/// </summary>
public static class ServiceRegistry
{
    private static readonly ConcurrentDictionary<Type, object> Services = new();

    public static void Register<T>(T instance) where T : class
        => Services[typeof(T)] = instance ?? throw new ArgumentNullException(nameof(instance));

    public static T? Get<T>() where T : class
        => Services.TryGetValue(typeof(T), out var s) ? (T)s : null;

    public static T GetRequired<T>() where T : class
        => Get<T>() ?? throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");

    internal static void Clear() => Services.Clear();
}
