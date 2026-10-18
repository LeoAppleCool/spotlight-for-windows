using System;
using System.Collections.Generic;
using System.Globalization;

namespace Spotlight.Core;

/// <summary>
/// Localized UI strings. The UI language always follows the Windows display language
/// (<see cref="CultureInfo.CurrentUICulture"/>, picked up automatically on first use and
/// re-applied by <see cref="Initialize"/>). Missing text falls back to English, then to the key.
/// </summary>
/// <remarks>
/// Keys ending in <c>.Keywords</c> hold ';'-separated, lower-case search synonyms. Read them through
/// <see cref="Keywords"/>, which merges the current language with English so both always match.
/// The English table is the reference: every language must define every key it has.
/// </remarks>
public static partial class Loc
{
    private const string FallbackLanguage = "en";

    /// <summary>Language code plus its table, swapped as one reference so readers never see a mix.</summary>
    private sealed record State(string Language, Dictionary<string, string> Table);

    private static readonly Dictionary<string, string> English;
    private static volatile State _state;

    static Loc()
    {
        // A static constructor body runs after all static field initializers, so Tables is ready here.
        English = Tables[FallbackLanguage];
        _state = CreateState(CurrentUICultureOrNull());
    }

    /// <summary>Resolved language code in use, e.g. "de", "en", "zh-Hans".</summary>
    public static string Language => _state.Language;

    /// <summary>Selects the UI language that best matches <paramref name="uiCulture"/> (normally the Windows display language).</summary>
    public static void Initialize(CultureInfo uiCulture) =>
        _state = CreateState(uiCulture ?? CurrentUICultureOrNull());

    /// <summary>Text for <paramref name="key"/>: current language, then English, then the key itself. Never throws.</summary>
    public static string Get(string key)
    {
        if (key is null) return string.Empty;
        if (_state.Table.TryGetValue(key, out var text) || English.TryGetValue(key, out text)) return text;
        return key;
    }

    /// <summary>
    /// <see cref="Get"/> formatted with <see cref="CultureInfo.CurrentCulture"/>.
    /// Never throws: on any formatting error the unformatted text is returned.
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var pattern = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, pattern, args);
        }
        catch (Exception)
        {
            // Malformed pattern, missing or null arguments, or an argument whose ToString() throws.
            return pattern;
        }
    }

    /// <summary>
    /// Search synonyms of a ';'-separated keyword key: current language plus English,
    /// trimmed, lower-cased (culture-invariant), de-duplicated, empty entries removed.
    /// </summary>
    public static IReadOnlyList<string> Keywords(string key)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(key)) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var table = _state.Table;
        AddKeywords(table, key, seen, result);
        if (!ReferenceEquals(table, English)) AddKeywords(English, key, seen, result);
        return result;
    }

    private static void AddKeywords(Dictionary<string, string> table, string key, HashSet<string> seen, List<string> result)
    {
        if (!table.TryGetValue(key, out var value)) return;
        foreach (var part in value.Split(';'))
        {
            var keyword = part.Trim().ToLowerInvariant();
            if (keyword.Length > 0 && seen.Add(keyword)) result.Add(keyword);
        }
    }

    private static State CreateState(CultureInfo? culture)
    {
        var language = Resolve(culture);
        return new State(language, Tables[language]);
    }

    private static CultureInfo? CurrentUICultureOrNull()
    {
        try
        {
            return CultureInfo.CurrentUICulture;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Exact culture name first, then its parent chain (de-AT → de, zh-Hant-TW → zh-Hant),
    /// then the two-letter ISO name; English when nothing matches.
    /// </summary>
    private static string Resolve(CultureInfo? culture)
    {
        try
        {
            var current = culture;
            for (var depth = 0; current is not null && !string.IsNullOrEmpty(current.Name) && depth < 8; depth++)
            {
                if (Match(current.Name) is { } language) return language;
                current = current.Parent;
            }

            if (culture is not null && Match(culture.TwoLetterISOLanguageName) is { } byIsoName) return byIsoName;
        }
        catch (Exception)
        {
            // Broken or custom culture data: fall through to English.
        }
        return FallbackLanguage;
    }

    /// <summary>Maps one culture name ("de", "zh-TW", "nn", ...) to a table key, or null.</summary>
    private static string? Match(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().Replace('_', '-').ToLowerInvariant();

        // Chinese: the script decides; a bare region implies it (CN/SG simplified, TW/HK/MO traditional).
        if (n == "zh" || n.StartsWith("zh-", StringComparison.Ordinal))
        {
            if (n.Contains("-hant", StringComparison.Ordinal) || n is "zh-tw" or "zh-hk" or "zh-mo" or "zh-cht") return "zh-Hant";
            if (n.Contains("-hans", StringComparison.Ordinal) || n is "zh" or "zh-cn" or "zh-sg" or "zh-chs") return "zh-Hans";
            return null; // other regions: let the parent chain decide
        }

        // Norwegian: Bokmål, Nynorsk and the "no" macro language all use the Bokmål table.
        if (n is "nb" or "nn" or "no") return "nb";

        return Tables.ContainsKey(n) ? n : null;
    }

    // String tables: language -> key -> text, one partial file per language group in Core/Strings.
    // Placeholders ({0}) must match the English text; use each language's own quotation marks
    // and the official Windows terms where they exist.
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = BuildTables();

    private static Dictionary<string, Dictionary<string, string>> BuildTables()
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        AddWestern(tables);
        AddCentral(tables);
        AddSouthEastern(tables);
        AddEastern(tables);
        AddNordic(tables);
        AddAsian(tables);
        return tables;
    }
}
