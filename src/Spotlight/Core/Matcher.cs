using System.Globalization;
using System.Text;

namespace Spotlight.Core;

/// <summary>A search query in normalized form (lower case, no diacritics, separators collapsed to single spaces).</summary>
public sealed class Query
{
    public Query(string raw)
    {
        Raw = raw;
        Norm = Matcher.Prepare(raw).Norm;
        Tokens = Norm.Length == 0 ? [] : Norm.Split(' ');
    }

    public string Raw { get; }
    public string Norm { get; }
    public string[] Tokens { get; }
}

/// <summary>Pre-processed candidate text; build once at index time and reuse for every keystroke.</summary>
public sealed class MatchTarget
{
    public MatchTarget(string text) => (Norm, WordStarts) = Matcher.Prepare(text);

    public string Norm { get; }
    public int[] WordStarts { get; }
}

/// <summary>
/// Spotlight-style relevance: exact &gt; prefix &gt; word prefix &gt; acronym &gt; substring &gt; fuzzy.
/// Scores are roughly 0..1000; 0 means no match.
/// </summary>
public static class Matcher
{
    private static readonly Dictionary<char, string> FoldCache = new();

    public static (string Norm, int[] WordStarts) Prepare(string text)
    {
        var sb = new StringBuilder(text.Length);
        var starts = new List<int>(8);
        char prev = ' ';
        bool prevSep = true;
        bool prevJoiner = false;
        foreach (char c in text)
        {
            // "Wi-Fi" → "wifi" (with a word start at "fi"), "Spotlight's" → "spotlights": people type them without.
            if (c is '-' or '\'' or '’' && sb.Length > 0 && sb[^1] != ' ')
            {
                prevJoiner = true;
                prev = c;
                continue;
            }
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                prevSep = true;
                prevJoiner = false;
                prev = c;
                continue;
            }
            bool wordStart = prevSep || (prevJoiner && prev == '-')
                || (char.IsUpper(c) && char.IsLower(prev))
                || (char.IsDigit(c) != char.IsDigit(prev));
            int before = sb.Length;
            AppendFolded(sb, c);
            if (sb.Length > before)
            {
                if (wordStart) starts.Add(before);
                prevSep = false;
            }
            prevJoiner = false;
            prev = c;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return (sb.ToString(), starts.ToArray());
    }

    private static void AppendFolded(StringBuilder sb, char c)
    {
        if (c < 128)
        {
            sb.Append(char.ToLowerInvariant(c));
            return;
        }
        string folded;
        lock (FoldCache)
        {
            if (!FoldCache.TryGetValue(c, out folded!))
            {
                folded = c switch
                {
                    'ß' or 'ẞ' => "ss",
                    'æ' or 'Æ' => "ae",
                    'œ' or 'Œ' => "oe",
                    'ø' or 'Ø' => "o",
                    'ł' or 'Ł' => "l",
                    'đ' or 'Đ' => "d",
                    'þ' or 'Þ' => "th",
                    'ı' => "i",
                    _ => StripMarks(c),
                };
                FoldCache[c] = folded;
            }
        }
        sb.Append(folded);
    }

    private static string StripMarks(char c)
    {
        var sb = new StringBuilder(2);
        foreach (char d in c.ToString().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(d));
        return sb.ToString();
    }

    public static int Score(Query q, MatchTarget t, bool allowFuzzy = true)
    {
        string s = t.Norm, qn = q.Norm;
        if (qn.Length == 0 || s.Length == 0 || s.IndexOf(qn[0]) < 0) return 0;
        if (s.Length == qn.Length && s == qn) return 1000;
        if (s.StartsWith(qn, StringComparison.Ordinal)) return 900 + 60 * qn.Length / s.Length;

        return q.Tokens.Length <= 1 ? ScoreSingle(qn, t, allowFuzzy) : ScoreTokens(q, t);
    }

    /// <summary>Best score of a query against several keyword targets, discounted because keywords are indirect.</summary>
    public static int ScoreKeywords(Query q, MatchTarget[]? keywords)
    {
        if (keywords == null) return 0;
        int best = 0;
        foreach (var k in keywords)
            best = Math.Max(best, Score(q, k, allowFuzzy: false));
        return best * 7 / 10;
    }

    private static int ScoreSingle(string qn, MatchTarget t, bool allowFuzzy)
    {
        string s = t.Norm;
        var starts = t.WordStarts;
        for (int i = 0; i < starts.Length; i++)
        {
            int ws = starts[i];
            if (ws > 0 && ws + qn.Length <= s.Length && string.CompareOrdinal(s, ws, qn, 0, qn.Length) == 0)
            {
                // "chrome" matching the whole word in "Google Chrome" beats a mere prefix like "chr".
                int end = ws + qn.Length;
                bool wholeWord = end == s.Length || s[end] == ' ' || IsWordStart(t, end);
                return 800 - Math.Min(i, 5) * 8 + 40 * qn.Length / s.Length + (wholeWord ? 50 : 0);
            }
        }
        if (qn.Length < 2) return 0;

        int acronym = AcronymScore(qn, t);
        if (acronym > 0) return acronym;

        int idx = s.IndexOf(qn, StringComparison.Ordinal);
        if (idx >= 0) return 600 - Math.Min(idx, 50) + 30 * qn.Length / s.Length;

        int compact = IndexOfIgnoringSpaces(s, qn);
        if (compact >= 0) return 560 - Math.Min(compact, 50);

        return allowFuzzy && qn.Length >= 3 ? FuzzyScore(qn, t) : 0;
    }

    private static int ScoreTokens(Query q, MatchTarget t)
    {
        string s = t.Norm;
        int phrase = s.IndexOf(q.Norm, StringComparison.Ordinal);
        if (phrase >= 0) return (IsWordStart(t, phrase) ? 790 : 620) - Math.Min(phrase, 50) / 2;

        // Every token must start a (different) word, in any order — "studio visual" still finds "Visual Studio".
        Span<bool> used = stackalloc bool[Math.Min(t.WordStarts.Length, 64)];
        bool allPrefix = true, inOrder = true;
        int lastWord = -1;
        foreach (string token in q.Tokens)
        {
            int found = -1;
            for (int w = 0; w < used.Length; w++)
            {
                int ws = t.WordStarts[w];
                if (!used[w] && ws + token.Length <= s.Length && string.CompareOrdinal(s, ws, token, 0, token.Length) == 0)
                {
                    found = w;
                    break;
                }
            }
            if (found < 0)
            {
                allPrefix = false;
                if (s.IndexOf(token, StringComparison.Ordinal) < 0) return 0;
                continue;
            }
            used[found] = true;
            if (found < lastWord) inOrder = false;
            lastWord = found;
        }
        return allPrefix ? (inOrder ? 780 : 750) : 520;
    }

    private static int AcronymScore(string qn, MatchTarget t)
    {
        var starts = t.WordStarts;
        if (starts.Length < qn.Length || qn.Contains(' ')) return 0;
        int qi = 0, skipped = 0;
        for (int w = 0; w < starts.Length && qi < qn.Length; w++)
        {
            if (t.Norm[starts[w]] == qn[qi]) qi++;
            else skipped++;
        }
        return qi == qn.Length ? 760 - Math.Min(skipped, 6) * 12 : 0;
    }

    /// <summary>
    /// Characters in order, preferring word starts ("vscode" → Visual Studio Code). Only accepted when it hits
    /// several words or is mostly contiguous, otherwise "code" would match "iCloud Shared Albums".
    /// </summary>
    private static int FuzzyScore(string qn, MatchTarget t)
    {
        string s = t.Norm;
        int best = 0;
        foreach (int start in t.WordStarts)
        {
            if (s[start] != qn[0]) continue;
            int qi = 1, gaps = 0, runs = 0, wordHits = 1, pos = start;
            while (qi < qn.Length)
            {
                int next = NextWordStart(t, pos + 1, qn[qi]);
                if (next < 0 || (pos + 1 < s.Length && s[pos + 1] == qn[qi])) next = s.IndexOf(qn[qi], pos + 1);
                if (next < 0) break;
                if (next == pos + 1) runs++;
                if (IsWordStart(t, next)) wordHits++;
                for (int k = pos + 1; k < next; k++)
                    if (s[k] != ' ') gaps++;
                pos = next;
                qi++;
            }
            if (qi < qn.Length || gaps > qn.Length * 2) continue;
            if (wordHits < 2 && runs < qn.Length - 2) continue; // scattered letters inside one word: not meant
            int score = 320 + runs * 14 + wordHits * 22 - gaps * 4;
            if (score >= 300) best = Math.Max(best, Math.Min(score, 520));
        }
        return best;
    }

    private static int NextWordStart(MatchTarget t, int from, char c)
    {
        foreach (int ws in t.WordStarts)
            if (ws >= from && t.Norm[ws] == c) return ws;
        return -1;
    }

    private static bool IsWordStart(MatchTarget t, int index) => Array.BinarySearch(t.WordStarts, index) >= 0;

    private static int IndexOfIgnoringSpaces(string s, string q)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != q[0]) continue;
            int si = i, qi = 0;
            while (si < s.Length && qi < q.Length)
            {
                if (s[si] == ' ') { si++; continue; }
                if (s[si] != q[qi]) break;
                si++;
                qi++;
            }
            if (qi == q.Length) return i;
        }
        return -1;
    }
}
