using System.Globalization;

namespace Spotlight.Calc;

/// <summary>
/// Unit conversion: "10 km in mi", "5ft to cm", "100 °F", "1,5 GB in MB", "3 Tassen in ml". Without a target
/// unit the most useful counterpart is shown (metric ↔ imperial, bytes ↔ binary units, ...).
/// </summary>
public static class UnitConverter
{
    /// <summary>A value in base units is (value + Offset) * Factor.</summary>
    private sealed record Unit(string Symbol, string Category, double Factor, double Offset = 0);

    private sealed record Alias(string Text, Unit Unit, bool CaseSensitive, bool NeedsTarget);

    private static readonly string[] Connectors =
    [
        "->", "→", "=", "into", "in", "to", "as", "nach", "zu", "als", "en", "à", "a", "em", "para", "naar", "i", "på",
        "til", "w", "na", "v", "во", "в",
    ];

    private static readonly List<Alias> Aliases = [];
    private static readonly Dictionary<string, Unit> BySymbol = new(StringComparer.Ordinal);

    /// <summary>Default targets when only a source unit is given (first entry is shown first).</summary>
    private static readonly Dictionary<string, string[]> Defaults = new(StringComparer.Ordinal)
    {
        ["km"] = ["mi"], ["m"] = ["ft"], ["cm"] = ["in"], ["mm"] = ["in"], ["mi"] = ["km"], ["ft"] = ["m", "cm"],
        ["in"] = ["cm"], ["yd"] = ["m"], ["nmi"] = ["km"],
        ["kg"] = ["lb"], ["g"] = ["oz"], ["lb"] = ["kg"], ["oz"] = ["g"], ["st"] = ["kg"], ["t"] = ["lb"],
        ["°C"] = ["°F"], ["°F"] = ["°C"], ["K"] = ["°C"],
        ["l"] = ["gal"], ["ml"] = ["fl oz"], ["gal"] = ["l"], ["qt"] = ["l"], ["pt"] = ["ml"], ["cup"] = ["ml"],
        ["fl oz"] = ["ml"], ["tbsp"] = ["ml"], ["tsp"] = ["ml"], ["m³"] = ["l"], ["cl"] = ["ml"], ["dl"] = ["ml"],
        ["m²"] = ["ft²"], ["km²"] = ["mi²"], ["ha"] = ["ac"], ["ac"] = ["ha"], ["ft²"] = ["m²"], ["mi²"] = ["km²"],
        ["cm²"] = ["in²"], ["in²"] = ["cm²"],
        ["km/h"] = ["mph"], ["mph"] = ["km/h"], ["m/s"] = ["km/h"], ["kn"] = ["km/h"], ["ft/s"] = ["m/s"],
        ["kB"] = ["B", "KiB"], ["MB"] = ["kB", "MiB"], ["GB"] = ["MB", "GiB"], ["TB"] = ["GB", "TiB"], ["PB"] = ["TB"],
        ["KiB"] = ["kB"], ["MiB"] = ["MB"], ["GiB"] = ["GB"], ["TiB"] = ["TB"],
        ["Mbit"] = ["MB"], ["Gbit"] = ["GB"], ["kbit"] = ["kB"],
        ["s"] = ["min"], ["min"] = ["h"], ["h"] = ["min"], ["d"] = ["h"], ["wk"] = ["d"], ["mo"] = ["d"], ["yr"] = ["d"], ["ms"] = ["s"],
        ["kcal"] = ["kJ"], ["kJ"] = ["kcal"], ["cal"] = ["J"], ["kWh"] = ["MJ"],
        ["bar"] = ["psi"], ["psi"] = ["bar"], ["atm"] = ["bar"], ["kPa"] = ["psi"], ["mmHg"] = ["hPa"],
    };

    static UnitConverter()
    {
        // Length (metre)
        Add("Length", "nm", 1e-9, "nanometer", "nanometre", "nanometers", "nanometres");
        Add("Length", "µm", 1e-6, "μm", "um", "micrometer", "micrometre", "micron", "microns", "mikrometer");
        Add("Length", "mm", 1e-3, "millimeter", "millimetre", "millimeters", "millimetres");
        Add("Length", "cm", 1e-2, "centimeter", "centimetre", "centimeters", "centimetres", "zentimeter");
        Add("Length", "dm", 0.1, "decimeter", "decimetre", "dezimeter");
        Add("Length", "m", 1, "meter", "metre", "meters", "metres");
        Add("Length", "km", 1000, "kilometer", "kilometre", "kilometers", "kilometres");
        Add("Length", "in", 0.0254, "inch", "inches", "zoll", "\"", "″");
        Add("Length", "ft", 0.3048, "foot", "feet", "fuß", "fuss", "'", "′");
        Add("Length", "yd", 0.9144, "yard", "yards");
        Add("Length", "mi", 1609.344, "mile", "miles", "meile", "meilen");
        Add("Length", "nmi", 1852, "nautical mile", "nautical miles", "seemeile", "seemeilen", "sm");

        // Mass (kilogram)
        Add("Mass", "mg", 1e-6, "milligram", "milligrams", "milligramm");
        Add("Mass", "g", 1e-3, "gram", "grams", "gramm", "gramme", "grammes");
        Add("Mass", "kg", 1, "kilogram", "kilograms", "kilogramm", "kilo", "kilos");
        Add("Mass", "t", 1000, "tonne", "tonnes", "tonnen", "metric ton", "metric tons");
        Add("Mass", "oz", 0.028349523125, "ounce", "ounces", "unze", "unzen");
        Add("Mass", "lb", 0.45359237, "lbs", "pound", "pounds", "pfund");
        Add("Mass", "st", 6.35029318, "stone", "stones");

        // Temperature (kelvin)
        AddUnit(new Unit("°C", "Temperature", 1, 273.15), ["°c", "℃", "celsius", "grad celsius", "degrees celsius", "degree celsius"], ["c"]);
        AddUnit(new Unit("°F", "Temperature", 5.0 / 9, 459.67), ["°f", "℉", "fahrenheit", "grad fahrenheit", "degrees fahrenheit", "degree fahrenheit"], ["f"]);
        AddUnit(new Unit("K", "Temperature", 1), ["kelvin"], ["K"]);

        // Volume (litre)
        Add("Volume", "ml", 1e-3, "milliliter", "millilitre", "milliliters", "millilitres");
        Add("Volume", "cl", 1e-2, "centiliter", "centilitre", "zentiliter");
        Add("Volume", "dl", 0.1, "deciliter", "decilitre", "deziliter");
        Add("Volume", "l", 1, "L", "liter", "litre", "liters", "litres");
        Add("Volume", "m³", 1000, "m3", "cbm", "cubic meter", "cubic meters", "kubikmeter");
        Add("Volume", "cm³", 1e-3, "cm3", "ccm", "cubic centimeter", "cubic centimeters", "kubikzentimeter");
        Add("Volume", "gal", 3.785411784, "gallon", "gallons", "gallone", "gallonen");
        Add("Volume", "qt", 0.946352946, "quart", "quarts");
        Add("Volume", "pt", 0.473176473, "pint", "pints");
        Add("Volume", "cup", 0.2365882365, "cups", "tasse", "tassen");
        Add("Volume", "fl oz", 0.0295735295625, "floz", "fl. oz", "fl.oz", "fluid ounce", "fluid ounces");
        Add("Volume", "tbsp", 0.01478676478125, "tablespoon", "tablespoons", "esslöffel", "el");
        Add("Volume", "tsp", 0.00492892159375, "teaspoon", "teaspoons", "teelöffel", "tl");

        // Area (square metre)
        Add("Area", "mm²", 1e-6, "mm2", "square millimeter", "square millimeters", "quadratmillimeter");
        Add("Area", "cm²", 1e-4, "cm2", "square centimeter", "square centimeters", "quadratzentimeter");
        Add("Area", "m²", 1, "m2", "sqm", "qm", "square meter", "square meters", "square metre", "square metres", "quadratmeter");
        Add("Area", "km²", 1e6, "km2", "square kilometer", "square kilometers", "quadratkilometer");
        Add("Area", "ha", 1e4, "hectare", "hectares", "hektar");
        Add("Area", "ac", 4046.8564224, "acre", "acres");
        Add("Area", "ft²", 0.09290304, "ft2", "sqft", "sq ft", "square foot", "square feet");
        Add("Area", "in²", 0.00064516, "in2", "sqin", "sq in", "square inch", "square inches");
        Add("Area", "mi²", 2589988.110336, "mi2", "sqmi", "sq mi", "square mile", "square miles");

        // Speed (metre per second)
        Add("Speed", "m/s", 1, "mps", "meters per second", "metres per second");
        Add("Speed", "km/h", 1 / 3.6, "kmh", "kph", "km/std", "kilometers per hour", "kilometres per hour", "stundenkilometer");
        Add("Speed", "mph", 0.44704, "miles per hour", "mi/h");
        Add("Speed", "kn", 1852.0 / 3600, "knot", "knots", "knoten", "kt", "kts");
        Add("Speed", "ft/s", 0.3048, "fps", "feet per second");

        // Data (byte); "B" is a byte and "b" a bit, the rest is case-insensitive
        AddUnit(new Unit("bit", "Data", 0.125), ["bits"], ["b"]);
        AddUnit(new Unit("B", "Data", 1), ["byte", "bytes"], ["B"]);
        Add("Data", "kB", 1e3, "kb", "kilobyte", "kilobytes");
        Add("Data", "MB", 1e6, "mb", "megabyte", "megabytes");
        Add("Data", "GB", 1e9, "gb", "gigabyte", "gigabytes");
        Add("Data", "TB", 1e12, "tb", "terabyte", "terabytes");
        Add("Data", "PB", 1e15, "pb", "petabyte", "petabytes");
        Add("Data", "KiB", 1024, "kib", "kibibyte", "kibibytes");
        Add("Data", "MiB", 1024d * 1024, "mib", "mebibyte", "mebibytes");
        Add("Data", "GiB", 1024d * 1024 * 1024, "gib", "gibibyte", "gibibytes");
        Add("Data", "TiB", 1024d * 1024 * 1024 * 1024, "tib", "tebibyte", "tebibytes");
        Add("Data", "kbit", 125, "kilobit", "kilobits");
        Add("Data", "Mbit", 125_000, "megabit", "megabits");
        Add("Data", "Gbit", 125_000_000, "gigabit", "gigabits");

        // Time (second)
        Add("Time", "ms", 1e-3, "millisecond", "milliseconds", "millisekunde", "millisekunden");
        Add("Time", "s", 1, "sec", "secs", "second", "seconds", "sekunde", "sekunden");
        Add("Time", "min", 60, "mins", "minute", "minutes", "minuten");
        Add("Time", "h", 3600, "hr", "hrs", "hour", "hours", "std", "stunde", "stunden");
        Add("Time", "d", 86400, "day", "days", "tag", "tage", "tagen");
        Add("Time", "wk", 604800, "week", "weeks", "woche", "wochen");
        Add("Time", "mo", 30.436875 * 86400, "month", "months", "monat", "monate", "monaten");
        Add("Time", "yr", 365.2425 * 86400, "year", "years", "jahr", "jahre", "jahren");

        // Energy (joule)
        Add("Energy", "J", 1, "joule", "joules");
        Add("Energy", "kJ", 1e3, "kilojoule", "kilojoules");
        Add("Energy", "MJ", 1e6, "megajoule", "megajoules");
        Add("Energy", "cal", 4.184, "calorie", "calories", "kalorie", "kalorien");
        Add("Energy", "kcal", 4184, "kilocalorie", "kilocalories", "kilokalorie", "kilokalorien");
        Add("Energy", "Wh", 3600, "watt hour", "watt hours", "wattstunde", "wattstunden");
        Add("Energy", "kWh", 3.6e6, "kilowatt hour", "kilowatt hours", "kilowattstunde", "kilowattstunden");

        // Pressure (pascal)
        Add("Pressure", "Pa", 1, "pascal");
        Add("Pressure", "hPa", 100, "hectopascal", "hektopascal");
        Add("Pressure", "kPa", 1e3, "kilopascal");
        Add("Pressure", "bar", 1e5);
        Add("Pressure", "mbar", 100, "millibar");
        Add("Pressure", "psi", 6894.757293168);
        Add("Pressure", "atm", 101325, "atmosphere", "atmospheres", "atmosphäre");
        Add("Pressure", "mmHg", 133.322387415, "mm hg", "torr");

        // Longest first, so "min" wins over "mi" and "m", and "fl oz" over "oz".
        Aliases.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));
    }

    private static void Add(string category, string symbol, double factor, params string[] aliases) =>
        AddUnit(new Unit(symbol, category, factor), [symbol, .. aliases], []);

    /// <param name="needsTarget">Letters that only count as this unit in "x c in f" (never alone, "5 c" is not 5 °C).</param>
    private static void AddUnit(Unit unit, string[] aliases, string[] needsTarget)
    {
        BySymbol[unit.Symbol] = unit;
        bool symbolIsCaseSensitive = unit.Symbol is "B" or "K";
        if (!symbolIsCaseSensitive && unit.Symbol.Length > 0 && !needsTarget.Contains(unit.Symbol))
            Aliases.Add(new Alias(unit.Symbol, unit, false, false));
        foreach (var a in aliases)
            if (!string.Equals(a, unit.Symbol, StringComparison.Ordinal)) Aliases.Add(new Alias(a, unit, false, false));
        foreach (var a in needsTarget)
            Aliases.Add(new Alias(a, unit, CaseSensitive: a is "B" or "b" or "K", NeedsTarget: a is not ("B" or "b")));
    }

    public static bool TryConvert(string input, CultureInfo culture, bool preferMetric, out IReadOnlyList<CalcResult> results)
    {
        results = [];
        try
        {
            string s = input.Trim();
            if (s.Length == 0 || s.Length > 100) return false;

            int pos = 0;
            bool negative = s[0] is '-' or '−';
            if (negative) pos++;
            if (!NumberParsing.TryRead(s, ref pos, culture, out double value)) return false;
            if (negative) value = -value;

            string rest = s[pos..].TrimStart();
            if (!MatchUnit(rest, allowNeedsTarget: true, out var from, out int length, out bool fromNeedsTarget)) return false;
            rest = rest[length..].Trim();

            var targets = new List<Unit>();
            if (rest.Length == 0)
            {
                if (fromNeedsTarget || !Defaults.TryGetValue(from.Symbol, out var defaults)) return false;
                if (from.Symbol == "K" && !preferMetric) defaults = ["°F"];
                targets.AddRange(defaults.Select(d => BySymbol[d]).Where(u => Sensible(value, from, u)));
            }
            else
            {
                int connector = MatchConnector(rest);
                if (connector <= 0) return false;
                string target = rest[connector..].Trim();
                if (!MatchUnit(target, allowNeedsTarget: true, out var to, out int targetLength, out _) || targetLength != target.Length) return false;
                if (to.Category != from.Category || to == from) return false;
                targets.Add(to);
            }
            if (targets.Count == 0) return false;

            double baseValue = (value + from.Offset) * from.Factor;
            string inputText = $"{NumberParsing.Format(value, culture, grouping: false)} {from.Symbol}";
            var list = new List<CalcResult>();
            foreach (var to in targets.Take(2))
            {
                double converted = NumberParsing.RoundSignificant(baseValue / to.Factor - to.Offset, 6);
                if (double.IsNaN(converted) || double.IsInfinity(converted)) continue;
                list.Add(new CalcResult(
                    CalcKind.Unit,
                    inputText,
                    $"{NumberParsing.Format(converted, culture, grouping: true)} {to.Symbol}",
                    NumberParsing.Format(converted, culture, grouping: false),
                    from.Category,
                    null));
            }
            results = list;
            return list.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Skip defaults that would read badly ("3 s" → 0.05 min, "500 h" → 30,000 min).</summary>
    private static bool Sensible(double value, Unit from, Unit to) => (from.Symbol, to.Symbol) switch
    {
        ("h", "min") => Math.Abs(value) <= 48,
        ("s", "min") => Math.Abs(value) >= 60,
        _ => true,
    };

    private static bool MatchUnit(string text, bool allowNeedsTarget, out Unit unit, out int length, out bool needsTarget)
    {
        foreach (var alias in Aliases)
        {
            if (alias.NeedsTarget && !allowNeedsTarget) continue;
            if (text.Length < alias.Text.Length) continue;
            var comparison = alias.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!text.StartsWith(alias.Text, comparison)) continue;
            int end = alias.Text.Length;
            if (end < text.Length && char.IsLetterOrDigit(text[end]) && char.IsLetterOrDigit(alias.Text[^1])) continue; // "min" in "mine"
            unit = alias.Unit;
            length = end;
            needsTarget = alias.NeedsTarget;
            return true;
        }
        unit = null!;
        length = 0;
        needsTarget = false;
        return false;
    }

    /// <summary>Length of the connector word at the start of <paramref name="text"/> ("in", "to", "→", ...), or 0.</summary>
    internal static int MatchConnector(string text)
    {
        foreach (string c in Connectors)
        {
            if (!text.StartsWith(c, StringComparison.OrdinalIgnoreCase)) continue;
            bool symbol = !char.IsLetter(c[0]);
            if (symbol || text.Length > c.Length && char.IsWhiteSpace(text[c.Length])) return c.Length;
        }
        return 0;
    }
}
