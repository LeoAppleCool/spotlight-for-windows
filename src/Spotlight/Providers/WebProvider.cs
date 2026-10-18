using System.Text.RegularExpressions;
using Spotlight.Core;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>"Search the web for …" as the last resort, plus opening typed URLs/domains directly.</summary>
internal sealed partial class WebProvider(AppConfig config) : ISearchProvider
{
    private static readonly HashSet<string> CommonTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "de", "at", "ch", "eu", "io", "dev", "app", "ai", "co", "uk", "us", "info", "me", "tv",
        "gg", "xyz", "fr", "it", "es", "nl", "be", "pl", "cz", "se", "no", "dk", "fi", "ru", "ca", "au", "jp", "cn",
        "in", "br", "edu", "gov", "so", "sh", "to", "ly", "fm", "tech", "online", "site", "shop", "store", "blog",
        "cloud", "page", "link", "live", "news", "media", "studio", "design", "games", "wiki", "email", "art",
    };

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        string text = query.Raw.Trim();
        if (text.Length == 0) return;

        string? url = AsUrl(text);
        if (url != null)
        {
            results.Add(new SearchResult
            {
                Key = "url:" + url.ToLowerInvariant(),
                Title = Loc.Format("Web.Open", text),
                Subtitle = Loc.Get("Sub.Website"),
                Category = Category.Websites,
                Score = 1500,
                Glyph = Glyphs.Globe,
                GlyphFont = Glyphs.Font,
                IconSource = DefaultBrowser.ExePath,
                Execute = () => Launcher.Open(url),
                Launch = new LaunchSpec("open", url),
                CopyValue = url,
            });
        }

        string searchUrl = string.Format(config.WebSearchUrl, Uri.EscapeDataString(text));
        results.Add(new SearchResult
        {
            Key = "web:" + query.Norm,
            Title = Loc.Format("Web.SearchFor", text),
            Subtitle = Loc.Get("Sub.WebSearch") + " · " + new Uri(searchUrl).Host.Replace("www.", ""),
            Category = Category.Web,
            Score = 0,
            Glyph = Glyphs.Search,
            GlyphFont = Glyphs.Font,
            IconSource = DefaultBrowser.ExePath,
            Execute = () => Launcher.Open(searchUrl),
            Launch = new LaunchSpec("open", searchUrl),
            CopyValue = searchUrl,
        });
    }

    public static bool LooksLikeUrl(string text) => AsUrl(text) != null;

    private static string? AsUrl(string text)
    {
        if (text.Contains(' ')) return null;
        if (SchemeRegex().IsMatch(text)) return text;
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return "https://" + text;
        if (LocalhostRegex().IsMatch(text)) return "http://" + text;

        var m = DomainRegex().Match(text);
        return m.Success && CommonTlds.Contains(m.Groups["tld"].Value) ? "https://" + text : null;
    }

    [GeneratedRegex(@"^(https?|ftp)://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^(localhost|\d{1,3}(\.\d{1,3}){3})(:\d{1,5})?(/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalhostRegex();

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+(?<tld>[a-z]{2,})(:\d{1,5})?(/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();
}
