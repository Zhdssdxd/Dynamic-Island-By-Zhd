using WindowsDynamicIsland.Core.Models;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class ClipboardEntryTests
{
    private static ClipboardEntry Text(string s, string app = "Notepad")
        => new(ClipboardEntryKind.Text, s, null, null, DateTimeOffset.Now, app);

    [Fact]
    public void SameText_IsDuplicate()
    {
        Assert.True(Text("hello").SameContentAs(Text("hello", "Chrome")));
        Assert.False(Text("hello").SameContentAs(Text("Hello")));
    }

    [Fact]
    public void Files_CompareIgnoringCase()
    {
        var a = new ClipboardEntry(ClipboardEntryKind.Files, null, null, new[] { @"C:\A.txt" }, DateTimeOffset.Now, null);
        var b = new ClipboardEntry(ClipboardEntryKind.Files, null, null, new[] { @"c:\a.TXT" }, DateTimeOffset.Now, null);
        Assert.True(a.SameContentAs(b));
        Assert.Equal("A.txt", a.ShortPreview);
    }

    [Fact]
    public void Search_MatchesTextAndApp()
    {
        var e = Text("Встречаемся в 8", "Telegram");
        Assert.True(e.Matches("встреч"));
        Assert.True(e.Matches("telegram"));
        Assert.True(e.Matches(""));
        Assert.False(e.Matches("пицца"));
    }

    [Fact]
    public void ShortPreview_IsOneLineAndTrimmed()
    {
        var e = Text("line one\nline two " + new string('x', 80));
        Assert.DoesNotContain("\n", e.ShortPreview);
        Assert.True(e.ShortPreview.Length <= 43);
    }
}
