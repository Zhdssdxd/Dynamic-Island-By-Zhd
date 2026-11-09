using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.UI;

/// <summary>Concrete layout the capsule shows. Derived from <see cref="IslandState"/> by <see cref="IslandLayout.ResolveView"/>.</summary>
public enum IslandViewKind
{
    Hidden,
    Idle,
    CompactMusic,
    CompactVideo,
    CompactTimer,
    CompactDownload,
    ExpandedMusic,
    ExpandedVideo,
    ExpandedTimer,
    ExpandedDownload,
    Dashboard,
    Clipboard,
    Notification,
    Indicator,
}

/// <summary>Target capsule geometry in DIPs.</summary>
public readonly record struct IslandGeometry(double Width, double Height, double CornerRadius, double Opacity)
{
    public double Area => Width * Height;
}

/// <summary>
/// Single place for every capsule size. Tweak numbers here; views are laid out to fit these boxes
/// (paddings: compact 10 px sides, expanded 18/14).
/// </summary>
public static class IslandLayout
{
    // Host window (must match IslandHostWidth / IslandHostHeight in Tokens.xaml).
    public const double HostWidth = 560;
    public const double HostHeight = 280;

    /// <summary>Room above the capsule inside the host so the shadow is not cut (taken from TopOffset).</summary>
    public const double ShadowRoom = 8;
    /// <summary>Side margin of the capsule inside the host for Left/Right positions and for the shadow.</summary>
    public const double SideMargin = 16;
    /// <summary>Room below the tallest capsule for the shadow.</summary>
    public const double BottomShadowRoom = 16;

    // ── Compact: added to the user's compact width.
    public const double CompactMediaExtra = 124;
    public const double CompactTimerExtra = 64;
    public const double CompactDownloadExtra = 84;
    public const double SecondaryBadgeExtra = 26;

    // ── Expanded.
    public static readonly Size ExpandedMusicSize = new(400, 164);
    public static readonly Size ExpandedMusicNoTimelineSize = new(400, 136);
    /// <summary>Extra height of the expanded music view when synchronized lyrics are shown.</summary>
    public const double LyricsExtraHeight = 48;
    public static readonly Size ExpandedVideoSize = new(400, 128);
    public static readonly Size ExpandedVideoNoTimelineSize = new(400, 100);
    public static readonly Size ExpandedTimerCountdownSize = new(340, 116);
    public static readonly Size ExpandedTimerStopwatchSize = new(340, 80);
    public static readonly Size ExpandedDownloadSize = new(380, 104);
    public static readonly Size DashboardSize = new(380, 128);
    public static readonly Size ClipboardSize = new(420, 248);

    // ── Transients.
    public const double IndicatorWidth = 300;
    public const double IndicatorExtraHeight = 8;
    public const double NotificationWidth = 380;
    public const double NotificationMinHeight = 64;
    public const double NotificationMaxHeight = 96;

    // Notification text metrics — must match NotificationView.xaml.
    public const double NotificationPaddingVertical = 13;
    public const double NotificationHeaderHeight = 17;
    public const double NotificationTitleLineHeight = 19;
    public const double NotificationBodyLineHeight = 17;
    public const double NotificationBodyFontSize = 13;
    /// <summary>380 − 2×16 padding − 36 icon − 12 gap.</summary>
    public const double NotificationTextWidth = 300;

    // ── Hidden.
    public const double HiddenWidth = 40;
    public const double HiddenHeight = 8;

    public static IslandViewKind ResolveView(IslandState s) => s.Presentation switch
    {
        IslandPresentation.Hidden => IslandViewKind.Hidden,
        IslandPresentation.Idle => IslandViewKind.Idle,
        IslandPresentation.Compact => s.Activity switch
        {
            IslandActivity.Music when s.Media is not null => IslandViewKind.CompactMusic,
            IslandActivity.Video when s.Media is not null => IslandViewKind.CompactVideo,
            IslandActivity.Timer when s.Timer is not null => IslandViewKind.CompactTimer,
            IslandActivity.Download when s.Download is not null => IslandViewKind.CompactDownload,
            _ => IslandViewKind.Idle,
        },
        IslandPresentation.Expanded when s.IsClipboardOpen => IslandViewKind.Clipboard,
        IslandPresentation.Expanded => s.Activity switch
        {
            IslandActivity.Music when s.Media is not null => IslandViewKind.ExpandedMusic,
            IslandActivity.Video when s.Media is not null => IslandViewKind.ExpandedVideo,
            IslandActivity.Timer when s.Timer is not null => IslandViewKind.ExpandedTimer,
            IslandActivity.Download when s.Download is not null => IslandViewKind.ExpandedDownload,
            _ => IslandViewKind.Dashboard,
        },
        IslandPresentation.Notification => s.Notification is null ? IslandViewKind.Idle : IslandViewKind.Notification,
        IslandPresentation.Indicator => s.Indicator is null ? IslandViewKind.Idle : IslandViewKind.Indicator,
        _ => IslandViewKind.Idle,
    };

    /// <summary>Key that changes whenever the content must be replaced with a transition.</summary>
    public static string ViewKey(IslandViewKind kind, IslandState s) => kind switch
    {
        IslandViewKind.Notification or IslandViewKind.Indicator =>
            kind.ToString() + ":" + s.TransientSequence.ToString(CultureInfo.InvariantCulture),
        _ => kind.ToString(),
    };

    public static bool IsCompactLike(IslandViewKind kind) => kind is IslandViewKind.Idle or IslandViewKind.CompactMusic
        or IslandViewKind.CompactVideo or IslandViewKind.CompactTimer or IslandViewKind.CompactDownload;

    /// <summary>
    /// Target geometry for a view. <paramref name="notificationHeight"/> measures a notification banner;
    /// <paramref name="compactHeight"/> &gt; 0 overrides the compact height (top-bar mode: exactly the bar's height).
    /// </summary>
    public static IslandGeometry Measure(IslandViewKind kind, IslandState s, Func<IslandNotification, double>? notificationHeight = null,
        bool hasLyrics = false, double compactHeight = 0)
    {
        var a = s.Settings.Appearance;
        var cw = a.CompactWidth;
        var ch = compactHeight > 0 ? compactHeight : a.CompactHeight;
        var badge = s.SecondaryActivity != IslandActivity.None && s.SecondaryActivity != s.Activity ? SecondaryBadgeExtra : 0;

        Size size;
        var expanded = true;
        switch (kind)
        {
            case IslandViewKind.Hidden:
                return new IslandGeometry(HiddenWidth, HiddenHeight, HiddenHeight / 2, 0);
            case IslandViewKind.Idle:
                size = new Size(cw, ch);
                expanded = false;
                break;
            case IslandViewKind.CompactMusic:
            case IslandViewKind.CompactVideo:
                size = new Size(cw + CompactMediaExtra + badge, ch);
                expanded = false;
                break;
            case IslandViewKind.CompactTimer:
                size = new Size(cw + CompactTimerExtra + badge, ch);
                expanded = false;
                break;
            case IslandViewKind.CompactDownload:
                size = new Size(cw + CompactDownloadExtra + badge, ch);
                expanded = false;
                break;
            case IslandViewKind.ExpandedMusic:
                size = s.Media is { HasTimeline: true } ? ExpandedMusicSize : ExpandedMusicNoTimelineSize;
                if (hasLyrics) size = new Size(size.Width, size.Height + LyricsExtraHeight);
                break;
            case IslandViewKind.ExpandedVideo:
                size = s.Media is { HasTimeline: true } ? ExpandedVideoSize : ExpandedVideoNoTimelineSize;
                // The session switcher adds a row of chips under the title.
                if (s.MediaSessions.Count > 1) size = new Size(size.Width, size.Height + 18);
                break;
            case IslandViewKind.ExpandedTimer:
                size = s.Timer is { Kind: TimerKind.Countdown } ? ExpandedTimerCountdownSize : ExpandedTimerStopwatchSize;
                break;
            case IslandViewKind.ExpandedDownload:
                size = ExpandedDownloadSize;
                break;
            case IslandViewKind.Dashboard:
                size = DashboardSize;
                break;
            case IslandViewKind.Clipboard:
                size = ClipboardSize;
                break;
            case IslandViewKind.Notification:
                var h = s.Notification is { } n && notificationHeight is not null ? notificationHeight(n) : 80;
                size = new Size(NotificationWidth, Math.Clamp(h, NotificationMinHeight, NotificationMaxHeight));
                break;
            case IslandViewKind.Indicator:
                size = new Size(IndicatorWidth, ch + IndicatorExtraHeight);
                break;
            default:
                size = new Size(cw, ch);
                expanded = false;
                break;
        }

        var w = Math.Round(Math.Min(size.Width, HostWidth - 2 * SideMargin));
        var hh = Math.Round(Math.Min(size.Height, HostHeight - ShadowRoom - BottomShadowRoom));
        var r = expanded ? a.ExpandedCornerRadius : a.CornerRadius;
        r = Math.Max(0, Math.Min(r, hh / 2));
        return new IslandGeometry(w, hh, r, 1);
    }

    /// <summary>
    /// Banner height for a notification: header + optional title + 1–2 body lines (measured with the real font).
    /// </summary>
    public static double MeasureNotificationHeight(IslandNotification n, double pixelsPerDip, FontFamily font)
    {
        var height = NotificationPaddingVertical * 2 + NotificationHeaderHeight;
        if (!string.IsNullOrWhiteSpace(n.Title)) height += NotificationTitleLineHeight;

        if (!string.IsNullOrWhiteSpace(n.Body))
        {
            var lines = 1;
            try
            {
                var typeface = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(n.Body.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, NotificationBodyFontSize, Brushes.White, pixelsPerDip > 0 ? pixelsPerDip : 1.0)
                {
                    MaxTextWidth = NotificationTextWidth,
                    LineHeight = NotificationBodyLineHeight,
                };
                lines = ft.Height > NotificationBodyLineHeight * 1.5 ? 2 : 1;
            }
            catch (Exception ex)
            {
                Log.Warn("Notification text measurement failed", ex);
            }
            height += lines * NotificationBodyLineHeight;
        }

        return Math.Clamp(height, NotificationMinHeight, NotificationMaxHeight);
    }
}
