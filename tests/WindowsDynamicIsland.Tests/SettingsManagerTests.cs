using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class SettingsManagerTests
{
    [Fact]
    public void Normalize_clamps_out_of_range_values()
    {
        var s = new AppSettings();
        s.Appearance.CompactWidth = 5000;
        s.Appearance.CompactHeight = -3;
        s.Appearance.Opacity = double.NaN;
        s.Appearance.AnimationSpeed = 10;
        s.Features.NotificationDurationSeconds = 0;
        s.Timer.Presets = new List<double> { -1, 5, 5, 3 };

        SettingsManager.Normalize(s);

        Assert.Equal(400, s.Appearance.CompactWidth);
        Assert.Equal(24, s.Appearance.CompactHeight);
        Assert.Equal(0.97, s.Appearance.Opacity);
        Assert.Equal(2.0, s.Appearance.AnimationSpeed);
        Assert.Equal(2, s.Features.NotificationDurationSeconds);
        Assert.Equal(new List<double> { 3, 5 }, s.Timer.Presets);
    }

    [Fact]
    public void Update_publishes_and_persists_round_trip()
    {
        using var env = new TestEnvironment();
        using var rec = env.Record<SettingsChanged>();
        env.Settings.Load();
        env.Settings.Update(s =>
        {
            s.Appearance.CompactWidth = 222;
            s.Features.VisualizerStyle = VisualizerStyle.Waveform;
            s.Hotkeys.ToggleExpand = "Ctrl+Shift+K";
        });
        env.Settings.SaveNow();

        Assert.Equal(2, rec.Count);
        Assert.Equal(222, rec.Last.Settings.Appearance.CompactWidth);

        var bus2 = new EventBus(null);
        using var reloaded = new SettingsManager(bus2, env.SettingsPath);
        reloaded.Load();
        Assert.Equal(222, reloaded.Current.Appearance.CompactWidth);
        Assert.Equal(VisualizerStyle.Waveform, reloaded.Current.Features.VisualizerStyle);
        Assert.Equal("Ctrl+Shift+K", reloaded.Current.Hotkeys.ToggleExpand);
    }

    [Fact]
    public void Snapshots_are_not_mutated_by_updates()
    {
        using var env = new TestEnvironment();
        env.Settings.Load();
        var before = env.Settings.Current;
        env.Settings.Update(s => s.Appearance.CompactWidth = 300);
        Assert.NotSame(before, env.Settings.Current);
        Assert.Equal(168, before.Appearance.CompactWidth);
        Assert.Equal(300, env.Settings.Current.Appearance.CompactWidth);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_keeps_backup()
    {
        using var env = new TestEnvironment();
        File.WriteAllText(env.SettingsPath, "{ this is not json");
        env.Settings.Load();
        Assert.Equal(new AppSettings().Appearance.CompactWidth, env.Settings.Current.Appearance.CompactWidth);
        Assert.True(File.Exists(env.SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Missing_sections_in_old_files_get_defaults()
    {
        using var env = new TestEnvironment();
        File.WriteAllText(env.SettingsPath, "{ \"Appearance\": { \"CompactWidth\": 200 } }");
        env.Settings.Load();
        Assert.Equal(200, env.Settings.Current.Appearance.CompactWidth);
        Assert.NotNull(env.Settings.Current.Hotkeys);
        Assert.True(env.Settings.Current.Features.MusicModeEnabled);
    }
}
