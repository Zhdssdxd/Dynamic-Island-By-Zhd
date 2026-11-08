using WindowsDynamicIsland.Core.Models;
using WindowsDynamicIsland.Media;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class LyricsTests
{
    [Fact]
    public void Parses_timed_lines_sorted_and_ignores_metadata()
    {
        const string lrc = "[ar:Artist]\n[ti:Song]\n[00:12.50]Second\n[00:01.00]First\n[01:02.345]Third\nno tag here\n";
        var lines = LrcParser.Parse(lrc);
        Assert.Equal(3, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].Time);
        Assert.Equal("First", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(12.5), lines[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(62345), lines[2].Time);
    }

    [Fact]
    public void Multiple_time_tags_repeat_the_line_and_offset_applies()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00][00:20.00]Chorus\r\n");
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("Chorus", l.Text));
        Assert.Equal(TimeSpan.FromSeconds(9.5), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(19.5), lines[1].Time);
    }

    [Fact]
    public void IndexAt_finds_the_current_line()
    {
        var lines = LrcParser.Parse("[00:05.00]a\n[00:10.00]b\n[00:15.00]c");
        Assert.Equal(-1, LrcParser.IndexAt(lines, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, LrcParser.IndexAt(lines, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, LrcParser.IndexAt(lines, TimeSpan.FromSeconds(14.9)));
        Assert.Equal(2, LrcParser.IndexAt(lines, TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void Empty_or_garbage_input_gives_no_lines()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse("   "));
        Assert.Empty(LrcParser.Parse("just text\nmore text"));
    }

    [Theory]
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("Song [with X] - Live Version", "Song")]
    [InlineData("Plain", "Plain")]
    public void Title_cleanup(string input, string expected) => Assert.Equal(expected, LyricsProvider.CleanTitle(input));

    [Theory]
    [InlineData("A, B", "A")]
    [InlineData("A & B", "A")]
    [InlineData("Solo", "Solo")]
    public void First_artist(string input, string expected) => Assert.Equal(expected, LyricsProvider.FirstArtist(input));

    [Fact]
    public void Track_key_ignores_videos_and_is_case_insensitive()
    {
        var music = new MediaSessionInfo
        {
            SessionId = "s", SourceAppId = "Spotify.exe", SourceDisplayName = "Spotify",
            Title = "Song", Artist = "Artist", Kind = MediaKind.Music, Duration = TimeSpan.FromSeconds(200.4),
        };
        Assert.Equal("artist|song|200", LyricsProvider.TrackKey(music));
        Assert.Equal(LyricsProvider.TrackKey(music), LyricsProvider.TrackKey(music with { Title = "SONG" }));
        Assert.Equal("", LyricsProvider.TrackKey(music with { Kind = MediaKind.Video }));
        Assert.Equal("", LyricsProvider.TrackKey(null));
    }
}
