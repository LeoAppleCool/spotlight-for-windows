using System.Globalization;

namespace Spotlight.Calc;

/// <summary>Culture-aware number reading and formatting shared by the calculator and the converters.</summary>
internal static class NumberParsing
{
    /// <summary>
    /// Reads a number at <paramref name="pos"/>. The culture's decimal separator is always decimal; the other one
    /// ("," vs ".") is a group separator only when exactly three digits follow ("1.000" in German, "1,000" in
    /// English), otherwise a decimal separator — so "3.5" and "3,5" both work everywhere.
    /// Also reads scientific notation (2.5e-3), hex (0xFF) and binary (0b1010).
    /// </summary>
    public static bool TryRead(string s, ref int pos, CultureInfo culture, out double value)
    {
        value = 0;
        int start = pos;
        if (pos >= s.Length) return false;

        if (s[pos] == '0' && pos + 2 < s.Length && (s[pos + 1] is 'x' or 'X' or 'b' or 'B'))
        {
            bool hex = s[pos + 1] is 'x' or 'X';
            int p = pos + 2;
            long v = 0;
            int digits = 0;
            while (p < s.Length && digits < 15)
            {
                int d = hex ? HexDigit(s[p]) : s[p] is '0' or '1' ? s[p] - '0' : -1;
                if (d < 0) break;
                v = v * (hex ? 16 : 2) + d;
                p++;
                digits++;
            }
            if (digits > 0 && (p >= s.Length || !char.IsLetterOrDigit(s[p])))
            {
                value = v;
                pos = p;
                return true;
            }
        }

        char dec = culture.NumberFormat.NumberDecimalSeparator is "," ? ',' : '.';
        char other = dec == ',' ? '.' : ',';
        var digitsText = new System.Text.StringBuilder();
        bool seenDecimal = false, seenGroup = false;
        int i = pos;
        if (i < s.Length && (s[i] == dec || s[i] == other) && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
        {
            digitsText.Append("0."); // ".5" / ",5": a leading separator is always decimal
            seenDecimal = true;
            i++;
        }
        else if (i >= s.Length || !char.IsAsciiDigit(s[i]))
        {
            return false;
        }

        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c))
            {
                digitsText.Append(c);
                i++;
                continue;
            }
            if ((c == dec || c == other) && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
            {
                if (c == other && !seenDecimal && ExactlyThreeDigitsFollow(s, i + 1) && (digitsText.Length > 0 && digitsText.Length <= 3 || seenGroup))
                {
                    seenGroup = true; // thousands separator
                    i++;
                    continue;
                }
                if (seenDecimal) break;
                seenDecimal = true;
                digitsText.Append('.');
                i++;
                continue;
            }
            break;
        }

        // Exponent: "1e3", "2.5E-4" (but "2e" alone is 2 × e, handled by the caller).
        if (i < s.Length && (s[i] is 'e' or 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] is '+' or '-')) j++;
            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                digitsText.Append('e');
                if (s[i + 1] is '+' or '-') digitsText.Append(s[i + 1]);
                while (j < s.Length && char.IsAsciiDigit(s[j])) digitsText.Append(s[j++]);
                i = j;
            }
        }

        if (!double.TryParse(digitsText.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        pos = i;
        return pos > start;
    }

    private static bool ExactlyThreeDigitsFollow(string s, int from)
    {
        int n = 0;
        while (from + n < s.Length && char.IsAsciiDigit(s[from + n])) n++;
        return n == 3;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Up to 10 decimals, trailing zeros trimmed, group separators from 10,000 on (optional), exponent notation
    /// for very large or very small values.
    /// </summary>
    public static string Format(double value, CultureInfo culture, bool grouping = true, int maxDecimals = 10)
    {
        if (value == 0 || Math.Abs(value) < 1e-300) value = 0; // no "-0"
        double abs = Math.Abs(value);
        if (abs >= 1e15 || (abs > 0 && abs < 1e-9))
            return value.ToString("0.#########E+0", culture);
        string decimals = maxDecimals > 0 ? "." + new string('#', maxDecimals) : "";
        string pattern = (grouping && abs >= 10000 ? "#,##0" : "0") + decimals;
        string text = value.ToString(pattern, culture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Rounds to a number of significant digits (6.213711922 → 6.21371).</summary>
    public static double RoundSignificant(double value, int digits)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value)) return value;
        int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(value))) + 1;
        int decimals = Math.Clamp(digits - magnitude, 0, 15);
        double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return magnitude > digits ? Math.Round(value) : rounded;
    }
}
