using System.Windows.Media;
using Spotlight.Core;

namespace Spotlight.Providers;

/// <summary>In-memory provider; runs on a background thread for every keystroke and must be fast.</summary>
internal interface ISearchProvider
{
    void Search(Query query, List<SearchResult> results, CancellationToken ct);
}

/// <summary>Slower provider (e.g. the Windows Search index) whose results are merged in when they arrive.</summary>
internal interface IAsyncSearchProvider
{
    bool Accepts(Query query);
    Task<List<SearchResult>> SearchAsync(Query query, CancellationToken ct);
}

internal static class Glyphs
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public const string Search = "";
    public const string Calculator = "";
    public const string Ruler = "";
    public const string Currency = "";
    public const string Globe = "";
    public const string Link = "";
    public const string Settings = "";
    public const string Lock = "";
    public const string Sleep = "";
    public const string Hibernate = "";
    public const string Power = "";
    public const string Restart = "";
    public const string SignOut = "";
    public const string Delete = "";
    public const string Folder = "";
    public const string Document = "";
    public const string Shield = "";
    public const string Bookmark = "";
}
