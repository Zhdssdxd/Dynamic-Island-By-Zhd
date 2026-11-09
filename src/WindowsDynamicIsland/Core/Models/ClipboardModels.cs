using System.Globalization;
using System.Windows.Media.Imaging;

namespace WindowsDynamicIsland.Core.Models;

public enum ClipboardEntryKind
{
    Text,
    Image,
    Files,
}

/// <summary>One item of the clipboard history (UI thread only — bound by the history window).</summary>
public sealed class ClipboardEntry : ObservableObject
{
    private const int PreviewLength = 400;

    private bool _isPinned;

    public ClipboardEntry(ClipboardEntryKind kind, string? text, BitmapSource? image, IReadOnlyList<string>? files,
        DateTimeOffset copiedAt, string? sourceApp)
    {
        Kind = kind;
        Text = text ?? "";
        Image = image;
        Files = files ?? Array.Empty<string>();
        CopiedAt = copiedAt;
        SourceApp = sourceApp ?? "";
    }

    public ClipboardEntryKind Kind { get; }
    public string Text { get; }
    public BitmapSource? Image { get; }
    public IReadOnlyList<string> Files { get; }
    public DateTimeOffset CopiedAt { get; private set; }
    public string SourceApp { get; }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value)) OnPropertyChanged(nameof(PinGlyph));
        }
    }

    public bool IsText => Kind == ClipboardEntryKind.Text;
    public bool IsImage => Kind == ClipboardEntryKind.Image;
    public bool IsFiles => Kind == ClipboardEntryKind.Files;

    public string KindGlyph => Kind switch
    {
        ClipboardEntryKind.Image => Glyphs.Picture,
        ClipboardEntryKind.Files => Glyphs.Folder,
        _ => Glyphs.Copy,
    };

    public string PinGlyph => IsPinned ? Glyphs.Unpin : Glyphs.Pin;

    /// <summary>Text shown in the card (trimmed; files as a list of names).</summary>
    public string Preview => Kind switch
    {
        ClipboardEntryKind.Files => string.Join("\n", Files.Take(4).Select(f => Path.GetFileName(f)))
                                    + (Files.Count > 4 ? $"\n… и ещё {Files.Count - 4}" : ""),
        ClipboardEntryKind.Image => Image is null ? "Изображение" : $"Изображение {Image.PixelWidth}×{Image.PixelHeight}",
        _ => Text.Length > PreviewLength ? Text[..PreviewLength].TrimEnd() + "…" : Text.Trim(),
    };

    /// <summary>Short one-line description for the island indicator.</summary>
    public string ShortPreview
    {
        get
        {
            var s = Kind switch
            {
                ClipboardEntryKind.Image => "Изображение",
                ClipboardEntryKind.Files => Files.Count == 1 ? Path.GetFileName(Files[0]) : $"Файлов: {Files.Count}",
                _ => Text.Trim().Replace("\r", " ").Replace("\n", " ").Replace("\t", " "),
            };
            return s.Length > 42 ? s[..42].TrimEnd() + "…" : s;
        }
    }

    /// <summary>"Chrome · 5 мин назад".</summary>
    public string Meta
    {
        get
        {
            var age = DateTimeOffset.Now - CopiedAt;
            var when = age.TotalSeconds < 60 ? "только что"
                : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} мин назад"
                : age.TotalHours < 24 ? $"{(int)age.TotalHours} ч назад"
                : CopiedAt.LocalDateTime.ToString("d MMM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
            return string.IsNullOrWhiteSpace(SourceApp) ? when : $"{SourceApp} · {when}";
        }
    }

    /// <summary>True when both entries hold the same content (used to de-duplicate).</summary>
    public bool SameContentAs(ClipboardEntry other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            ClipboardEntryKind.Text => string.Equals(Text, other.Text, StringComparison.Ordinal),
            ClipboardEntryKind.Files => Files.SequenceEqual(other.Files, StringComparer.OrdinalIgnoreCase),
            _ => Image is not null && other.Image is not null
                 && Image.PixelWidth == other.Image.PixelWidth && Image.PixelHeight == other.Image.PixelHeight
                 && ReferenceEquals(Image, other.Image),
        };
    }

    /// <summary>Moves the entry to "now" (copied again).</summary>
    public void Touch()
    {
        CopiedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(Meta));
    }

    /// <summary>Re-evaluates the relative time text.</summary>
    public void RefreshMeta() => OnPropertyChanged(nameof(Meta));

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return Kind switch
        {
            ClipboardEntryKind.Files => Files.Any(f => f.Contains(q, StringComparison.CurrentCultureIgnoreCase)),
            ClipboardEntryKind.Image => "изображение картинка image".Contains(q, StringComparison.CurrentCultureIgnoreCase),
            _ => Text.Contains(q, StringComparison.CurrentCultureIgnoreCase),
        } || SourceApp.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>Requests the app shell to open the clipboard history window (hotkey, tray, island menu).</summary>
public sealed record OpenClipboardHistoryRequested();
