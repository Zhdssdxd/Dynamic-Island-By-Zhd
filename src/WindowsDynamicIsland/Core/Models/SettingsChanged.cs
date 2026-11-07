using WindowsDynamicIsland.Settings;

namespace WindowsDynamicIsland.Core.Models;

/// <summary>
/// Published on load and after every settings update. <see cref="Settings"/> is a fresh snapshot — never mutate it;
/// use <c>SettingsManager.Update</c> instead.
/// </summary>
public sealed record SettingsChanged(AppSettings Settings);
