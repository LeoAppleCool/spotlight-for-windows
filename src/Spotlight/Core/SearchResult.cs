using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Spotlight.Core;

/// <summary>Result categories in display order.</summary>
public enum Category
{
    Calculator,
    Conversion,
    Applications,
    Settings,
    System,
    Websites,
    Bookmarks,
    Folders,
    Documents,
    Images,
    Media,
    Files,
    Web,
}

public abstract class ListEntry : INotifyPropertyChanged
{
    public virtual bool IsHeader => false;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class HeaderEntry(string title) : ListEntry
{
    public string Title { get; } = title;
    public override bool IsHeader => true;
}

public sealed class SearchResult : ListEntry
{
    private string? _subtitle;
    private ImageSource? _image;
    private bool _awaitingConfirmation;

    /// <summary>Stable identity used for usage learning and for keeping the selection across refreshes.</summary>
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required Category Category { get; init; }

    public string? Subtitle
    {
        get => _awaitingConfirmation ? Loc.Get("Action.ConfirmHint") : _subtitle;
        init => _subtitle = value;
    }

    /// <summary>Relevance (match quality plus provider bonuses); the engine adds usage and category weights.</summary>
    public double Score { get; set; }

    /// <summary>Final ranking score computed by the engine (for diagnostics).</summary>
    public double Rank { get; set; }

    // ---- Icon: either a glyph from an icon font or an image loaded asynchronously ----
    public string? Glyph { get; init; }
    public FontFamily? GlyphFont { get; init; }
    public bool HasGlyph => Glyph != null && _image == null;

    /// <summary>Shell parsing name or "res:file,index" the icon loader understands.</summary>
    public string? IconSource { get; init; }
    public string? IconCacheKey { get; init; }

    public ImageSource? Image
    {
        get => _image;
        set { _image = value; Raise(); Raise(nameof(HasGlyph)); }
    }

    // ---- Actions ----
    public Action? Execute { get; init; }
    public Action? Reveal { get; init; }
    public Action? RunAsAdmin { get; init; }
    public string? CopyValue { get; init; }
    public bool CopyOnEnter { get; init; }
    public bool NeedsConfirmation { get; init; }
    /// <summary>Text that Tab puts into the search box (path completion).</summary>
    public string? Completion { get; init; }

    /// <summary>How to open this result again from the history; null = not remembered (e.g. calculations).</summary>
    public LaunchSpec? Launch { get; init; }

    /// <summary>Shown in the "Recent" list (Delete removes it).</summary>
    public bool FromHistory { get; init; }

    public string Hint => CopyOnEnter ? Loc.Get("Hint.Copy") : Loc.Get("Hint.Enter");

    public bool IsCalculation => Category is Category.Calculator or Category.Conversion;

    public bool AwaitingConfirmation
    {
        get => _awaitingConfirmation;
        set { _awaitingConfirmation = value; Raise(); Raise(nameof(Subtitle)); }
    }
}
