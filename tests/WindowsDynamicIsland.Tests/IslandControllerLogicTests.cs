using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Settings;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class IslandControllerLogicTests
{
    private static MediaSessionInfo Media(MediaPlaybackState state, MediaKind kind = MediaKind.Music) => new()
    {
        SessionId = "s1",
        SourceAppId = "Spotify.exe",
        SourceDisplayName = "Spotify",
        Title = "Song",
        State = state,
        Kind = kind,
    };

    private static TimerStateChanged Timer(TimerRunState state) => new() { Kind = TimerKind.Stopwatch, State = state };

    private static DownloadProgressChanged Download(DownloadState state) => new() { Id = "d", FileName = "f.zip", State = state };

    [Fact]
    public void Nothing_active_gives_no_activity()
    {
        var (p, s) = IslandController.ChooseActivities(null, null, null);
        Assert.Equal(IslandActivity.None, p);
        Assert.Equal(IslandActivity.None, s);
    }

    [Fact]
    public void Running_timer_beats_playing_music()
    {
        var (p, s) = IslandController.ChooseActivities(Timer(TimerRunState.Running), Media(MediaPlaybackState.Playing), null);
        Assert.Equal(IslandActivity.Timer, p);
        Assert.Equal(IslandActivity.Music, s);
    }

    [Fact]
    public void Playing_media_beats_paused_timer_and_download()
    {
        var (p, s) = IslandController.ChooseActivities(Timer(TimerRunState.Paused), Media(MediaPlaybackState.Playing, MediaKind.Video),
            Download(DownloadState.InProgress));
        Assert.Equal(IslandActivity.Video, p);
        Assert.Equal(IslandActivity.Download, s);
    }

    [Fact]
    public void Active_download_beats_paused_media()
    {
        var (p, _) = IslandController.ChooseActivities(null, Media(MediaPlaybackState.Paused), Download(DownloadState.InProgress));
        Assert.Equal(IslandActivity.Download, p);
    }

    [Fact]
    public void Finished_timer_is_not_an_activity()
    {
        var (p, _) = IslandController.ChooseActivities(Timer(TimerRunState.Finished), null, null);
        Assert.Equal(IslandActivity.None, p);
    }

    [Fact]
    public void Presentation_priorities()
    {
        var indicator = new SystemIndicatorEvent { Kind = IndicatorKind.Volume, Glyph = Glyphs.Volume, Title = "Громкость" };
        var notification = new IslandNotification { Id = "n", AppName = "App" };
        var critical = notification with { Priority = NotificationPriority.Critical };

        Assert.Equal(IslandPresentation.Hidden,
            IslandController.ComputePresentation(true, false, indicator, notification, true, IslandActivity.Music, true));
        Assert.Equal(IslandPresentation.Hidden,
            IslandController.ComputePresentation(false, true, indicator, notification, false, IslandActivity.Music, true));
        Assert.Equal(IslandPresentation.Notification,
            IslandController.ComputePresentation(false, true, null, critical, false, IslandActivity.None, true));
        Assert.Equal(IslandPresentation.Indicator,
            IslandController.ComputePresentation(false, false, indicator, notification, true, IslandActivity.Music, true));
        Assert.Equal(IslandPresentation.Notification,
            IslandController.ComputePresentation(false, false, null, notification, true, IslandActivity.Music, true));
        Assert.Equal(IslandPresentation.Expanded,
            IslandController.ComputePresentation(false, false, null, null, true, IslandActivity.None, true));
        Assert.Equal(IslandPresentation.Compact,
            IslandController.ComputePresentation(false, false, null, null, false, IslandActivity.Timer, false));
        Assert.Equal(IslandPresentation.Idle,
            IslandController.ComputePresentation(false, false, null, null, false, IslandActivity.None, true));
        Assert.Equal(IslandPresentation.Hidden,
            IslandController.ComputePresentation(false, false, null, null, false, IslandActivity.None, false));
    }

    [Fact]
    public void FilterMedia_respects_settings_and_state()
    {
        var f = new FeatureSettings();
        Assert.Null(IslandController.FilterMedia(null, f));
        Assert.Null(IslandController.FilterMedia(Media(MediaPlaybackState.Stopped), f));
        Assert.Null(IslandController.FilterMedia(Media(MediaPlaybackState.Closed), f));
        Assert.NotNull(IslandController.FilterMedia(Media(MediaPlaybackState.Paused), f));

        f.VideoModeEnabled = false;
        Assert.Null(IslandController.FilterMedia(Media(MediaPlaybackState.Playing, MediaKind.Video), f));
        Assert.NotNull(IslandController.FilterMedia(Media(MediaPlaybackState.Playing, MediaKind.Music), f));

        f.MusicModeEnabled = false;
        f.VideoModeEnabled = true;
        Assert.Null(IslandController.FilterMedia(Media(MediaPlaybackState.Playing, MediaKind.Unknown), f));
        Assert.NotNull(IslandController.FilterMedia(Media(MediaPlaybackState.Playing, MediaKind.Video), f));
    }

    [Fact]
    public void Indicator_toggles_map_to_settings()
    {
        var s = new IndicatorSettings { Volume = false, Usb = false };
        Assert.False(IslandController.IsIndicatorEnabled(IndicatorKind.Volume, s));
        Assert.False(IslandController.IsIndicatorEnabled(IndicatorKind.UsbDevice, s));
        Assert.True(IslandController.IsIndicatorEnabled(IndicatorKind.Battery, s));
        Assert.True(IslandController.IsIndicatorEnabled(IndicatorKind.Bluetooth, s));
    }

    [Fact]
    public void Media_position_is_extrapolated_while_playing()
    {
        var t0 = DateTimeOffset.UtcNow;
        var m = Media(MediaPlaybackState.Playing) with
        {
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(100),
            PositionUpdatedAt = t0,
        };
        Assert.Equal(TimeSpan.FromSeconds(15), m.GetEstimatedPosition(t0.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(100), m.GetEstimatedPosition(t0.AddSeconds(500)));

        var paused = m with { State = MediaPlaybackState.Paused };
        Assert.Equal(TimeSpan.FromSeconds(10), paused.GetEstimatedPosition(t0.AddSeconds(5)));
    }
}
