using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Spotlight.Calc;
using Spotlight.Core;
using static Spotlight.Tests.Assert;

namespace Spotlight.Tests;

internal static class Program
{
    private static int Main()
    {
        CalculatorTests.Run();
        UnitTests.Run();
        CurrencyTests.Run();
        MatcherTests.Run();
        LocalizationTests.Run();

        Console.WriteLine();
        Console.WriteLine($"{Passed} passed, {Failed} failed");
        return Failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static int Passed { get; private set; }
    public static int Failed { get; private set; }

    public static void Check(string name, bool ok, string actual = "")
    {
        if (ok)
        {
            Passed++;
            return;
        }
        Failed++;
        Console.WriteLine($"FAIL  {name}{(actual.Length > 0 ? $"   (got: {actual})" : "")}");
    }

    public static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);
}

internal static class CalculatorTests
{
    public static void Run()
    {
        Expect("2+2", "en-US", "4");
        Expect("2*(3+4)", "en-US", "14");
        Expect("2(3+4)", "en-US", "14");
        Expect("(1+2)(3+4)", "en-US", "21");
        Expect("2^10", "en-US", "1024");
        Expect("2**3", "en-US", "8");
        Expect("2^3^2", "en-US", "512");
        Expect("-2^2", "en-US", "-4");
        Expect("10000*3", "en-US", "30,000");
        Expect("10000*3", "de-DE", "30.000");
        Expect("10000*3", "en-DE", "30.000");
        Expect("3,5*2", "de-DE", "7");
        Expect("3.5*2", "de-DE", "7");
        Expect("3,5*2", "en-US", "7");
        Expect("1.000*2", "de-DE", "2000");
        Expect("1,000*2", "en-US", "2000");
        Expect("1.234.567+1", "de-DE", "1.234.568");
        Expect("2.000,5+1", "de-DE", "2001,5");
        Expect("1/3", "en-US", "0.3333333333");
        Expect("1/3", "de-DE", "0,3333333333");
        Expect("0.1+0.2", "en-US", "0.3");
        Expect("10:4", "de-DE", "2,5");
        Expect("3x4", "en-US", "12");
        Expect("5!", "en-US", "120");
        Expect("5²", "en-US", "25");
        Expect("sqrt 16", "en-US", "4");
        Expect("sqrt(16)+1", "en-US", "5");
        Expect("√16", "en-US", "4");
        Expect("sin(90°)", "en-US", "1");
        Expect("cos(180°)", "en-US", "-1");
        Expect("sin(180°)", "en-US", "0");
        Expect("2pi", "en-US", "6.2831853072");
        Expect("pi", "en-US", "3.1415926536");
        Expect("2e", "en-US", "5.4365636569");
        Expect("1e3*2", "en-US", "2000");
        Expect("log(1000)", "en-US", "3");
        Expect("log2(8)", "en-US", "3");
        Expect("ln(e)", "en-US", "1");
        Expect("0xff+1", "en-US", "256");
        Expect("0b1010*2", "en-US", "20");
        Expect("50%", "en-US", "0.5");
        Expect("200*15%", "en-US", "30");
        Expect("200+10%", "en-US", "220");
        Expect("200-10%", "en-US", "180");
        Expect("15% of 200", "en-US", "30");
        Expect("15% von 200", "de-DE", "30");
        Expect("10 % 3", "en-US", "1");
        Expect("10 mod 3", "en-US", "1");
        Expect("=2+2", "en-US", "4");
        Expect("2^60", "en-US", "1.152921505E+18");
        Expect("round(2.5)", "en-US", "3");

        foreach (var input in new[] { "42", "2024", "-5", "3,5", "1e3", "e", "90°", "hello", "2+", "(3*", "1/0", "2 3",
                                      "10 km", "100 usd", "chrome", @"C:\Users", "github.com", "x", "sqrt", "" })
            Expect(input, "en-US", null);

        bool ok = MathEvaluator.TryEvaluate("10000*3", C("en-US"), out var r);
        Check("math copy text has no grouping", ok && r.CopyText == "30000", ok ? r.CopyText : "");
        ok = MathEvaluator.TryEvaluate("2*(3+4)", C("en-US"), out r);
        Check("math input is rendered", ok && r.Input == "2 × (3 + 4)", ok ? r.Input : "");
        Check("math kind", ok && r.Kind == CalcKind.Math && r.Category == null);
    }

    private static void Expect(string input, string culture, string? expected)
    {
        bool ok = MathEvaluator.TryEvaluate(input, C(culture), out var r);
        if (expected == null) Check($"math '{input}' [{culture}] gives no result", !ok, ok ? r.Display : "");
        else Check($"math '{input}' [{culture}] = {expected}", ok && r.Display == expected, ok ? r.Display : "(none)");
    }
}

internal static class UnitTests
{
    public static void Run()
    {
        Expect("10 km in mi", "en-US", "6.21371 mi");
        Expect("10km to mi", "en-US", "6.21371 mi");
        Expect("10 km", "en-US", "6.21371 mi");
        Expect("5 ft to cm", "en-US", "152.4 cm");
        Expect("6 Fuß in m", "de-DE", "1,8288 m");
        Expect("10 Meilen in km", "de-DE", "16,0934 km");
        Expect("1 mile in km", "en-US", "1.60934 km");
        Expect("12 in to cm", "en-US", "30.48 cm");
        Expect("100 °F in °C", "en-US", "37.7778 °C");
        Expect("20 c in f", "en-US", "68 °F");
        Expect("20°C", "de-DE", "68 °F");
        Expect("0 K in °C", "en-US", "-273.15 °C");
        Expect("300 kelvin", "de-DE", "26,85 °C");
        Expect("1,5 GB in MB", "de-DE", "1500 MB");
        Expect("1 GiB in MB", "en-US", "1073.74 MB");
        Expect("1.5 GB", "en-US", "1500 MB");
        Expect("3 Tassen in ml", "de-DE", "709,765 ml");
        Expect("2 Esslöffel in ml", "de-DE", "29,5735 ml");
        Expect("100 km/h in mph", "en-US", "62.1371 mph");
        Expect("1 ha in m²", "de-DE", "10.000 m²");
        Expect("90 min", "en-US", "1.5 h");
        Expect("2 h in min", "en-US", "120 min");
        Expect("1 kWh in kJ", "en-US", "3600 kJ");
        Expect("1 bar in psi", "en-US", "14.5038 psi");
        Expect("10 pounds in kg", "en-US", "4.53592 kg");
        Expect("5 kg in lb", "de-DE", "11,0231 lb");
        Expect("5 kg", "en-US", "11.0231 lb");
        Expect("2 l to gal", "en-US", "0.528344 gal");

        foreach (var input in new[] { "2 e", "5 a", "5 c", "10 km in kg", "hello", "10", "10 km in", "100 usd", "5 x" })
            Expect(input, "en-US", null);

        bool ok = UnitConverter.TryConvert("10 km in mi", C("en-US"), true, out var r);
        Check("unit category", ok && r[0].Category == "Length" && r[0].Kind == CalcKind.Unit);
        Check("unit input text", ok && r[0].Input == "10 km", ok ? r[0].Input : "");
        Check("unit copy text", ok && r[0].CopyText == "6.21371", ok ? r[0].CopyText : "");
    }

    private static void Expect(string input, string culture, string? expected)
    {
        bool ok = UnitConverter.TryConvert(input, C(culture), preferMetric: true, out var r);
        string got = ok ? string.Join(" | ", r.Select(x => x.Display)) : "(none)";
        if (expected == null) Check($"unit '{input}' gives no result", !ok, got);
        else Check($"unit '{input}' [{culture}] = {expected}", ok && r[0].Display == expected, got);
    }
}

internal static class CurrencyTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <gesmes:subject>Reference rates</gesmes:subject>
          <Cube>
            <Cube time="2026-10-16">
              <Cube currency="USD" rate="1.0800"/>
              <Cube currency="GBP" rate="0.8500"/>
              <Cube currency="CHF" rate="0.9500"/>
              <Cube currency="JPY" rate="160.00"/>
              <Cube currency="SEK" rate="11.50"/>
            </Cube>
          </Cube>
        </gesmes:Envelope>
        """;

    public static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spotlight-tests-" + Guid.NewGuid().ToString("N"));
        var converter = new CurrencyConverter(dir);
        Check("no rates before loading", !converter.HasRates);
        Check("rates load", converter.LoadRatesFromXml(Xml));
        Check("rates date", converter.RatesDate == new DateTime(2026, 10, 16), converter.RatesDate?.ToString() ?? "null");

        Expect(converter, "100 usd in eur", "en-US", "EUR", "92.59 EUR");
        Expect(converter, "100 USD", "de-DE", "EUR", "92,59 EUR");
        Expect(converter, "€50 in $", "en-US", "EUR", "54.00 USD");
        Expect(converter, "100$ to €", "en-US", "EUR", "92.59 EUR");
        Expect(converter, "50 eur", "en-US", "EUR", "54.00 USD");
        Expect(converter, "10 britische Pfund in Euro", "de-DE", "EUR", "11,76 EUR");
        Expect(converter, "10 pounds in euro", "en-US", "EUR", "11.76 EUR");
        Expect(converter, "100 Franken in Euro", "de-DE", "EUR", "105,26 EUR");
        Expect(converter, "1000 eur in jpy", "en-US", "EUR", "160,000 JPY");
        Expect(converter, "20 kr", "en-US", "SEK", "1.88 USD");
        Expect(converter, "100 dollars in euro", "en-US", "EUR", "92.59 EUR");

        foreach (var input in new[] { "10 pounds", "10 pounds in kg", "hello", "100", "100 xyz", "10 km in mi", "eur" })
            Expect(converter, input, "en-US", "EUR", null);

        bool ok = converter.TryConvert("100 usd in eur", C("en-US"), "EUR", out var r);
        Check("currency copy text", ok && r[0].CopyText == "92.59", ok ? r[0].CopyText : "");
        Check("currency input text", ok && r[0].Input == "100 USD", ok ? r[0].Input : "");
        Check("currency category", ok && r[0].Category == "Currency" && r[0].Kind == CalcKind.Currency);
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void Expect(CurrencyConverter converter, string input, string culture, string local, string? expected)
    {
        bool ok = converter.TryConvert(input, C(culture), local, out var r);
        string got = ok ? r[0].Display : "(none)";
        if (expected == null) Check($"currency '{input}' gives no result", !ok, got);
        else Check($"currency '{input}' [{culture}] = {expected}", ok && r[0].Display == expected, got);
    }
}

internal static class MatcherTests
{
    public static void Run()
    {
        AtLeast("chrome", "Google Chrome", 800);
        AtLeast("chrome", "Chrome", 1000);
        AtLeast("chr", "Chrome Remote Desktop", 900);
        AtLeast("vsc", "Visual Studio Code", 700);
        AtLeast("studio visual", "Visual Studio Code", 750);
        AtLeast("wifi", "Wi-Fi settings", 900);
        AtLeast("wi-fi", "Wi-Fi settings", 900);
        AtLeast("uber", "Über uns", 900);
        AtLeast("über", "Über uns", 900);
        AtLeast("vscode", "Visual Studio Code", 300);
        AtLeast("onedrive", "OneDrive", 1000);
        AtLeast("drive", "OneDrive", 800);
        AtLeast("strasse", "Hauptstraße 5", 600);
        Never("code", "iCloud Shared Albums");
        Never("x", "Chrome");
        Never("chrome", "Crunchyroll - Watch Popular Anime");
        Never("c", "Microsoft Teams");

        int whole = Matcher.Score(new Query("chrome"), new MatchTarget("Google Chrome"));
        int partial = Matcher.Score(new Query("chro"), new MatchTarget("Google Chrome"));
        Check("whole word beats word prefix", whole > partial, $"{whole} vs {partial}");
        int prefix = Matcher.Score(new Query("note"), new MatchTarget("Notepad"));
        int inner = Matcher.Score(new Query("note"), new MatchTarget("OneNote"));
        Check("prefix beats inner word", prefix > inner, $"{prefix} vs {inner}");
    }

    private static void AtLeast(string query, string text, int min)
    {
        int score = Matcher.Score(new Query(query), new MatchTarget(text));
        Check($"match '{query}' ~ '{text}' >= {min}", score >= min, score.ToString());
    }

    private static void Never(string query, string text)
    {
        int score = Matcher.Score(new Query(query), new MatchTarget(text));
        Check($"no match '{query}' ~ '{text}'", score == 0, score.ToString());
    }
}

internal static partial class LocalizationTests
{
    public static void Run()
    {
        var field = typeof(Loc).GetField("Tables", BindingFlags.NonPublic | BindingFlags.Static);
        var tables = (Dictionary<string, Dictionary<string, string>>)field!.GetValue(null)!;
        var english = tables["en"];
        Check("27 languages", tables.Count == 27, tables.Count.ToString());

        foreach (var (language, table) in tables)
        {
            var missing = english.Keys.Where(k => !table.ContainsKey(k)).ToList();
            Check($"{language}: all keys present", missing.Count == 0, string.Join(", ", missing));
            var extra = table.Keys.Where(k => !english.ContainsKey(k)).ToList();
            Check($"{language}: no unknown keys", extra.Count == 0, string.Join(", ", extra));
            foreach (var (key, text) in english)
            {
                if (!table.TryGetValue(key, out var translated)) continue;
                var want = Placeholder().Matches(text).Select(m => m.Value).Order();
                var have = Placeholder().Matches(translated).Select(m => m.Value).Order();
                Check($"{language}: placeholders of {key}", want.SequenceEqual(have), translated);
            }
        }

        Resolve("de-DE", "de");
        Resolve("de-AT", "de");
        Resolve("en-DE", "en");
        Resolve("zh-TW", "zh-Hant");
        Resolve("zh-CN", "zh-Hans");
        Resolve("nn-NO", "nb");
        Resolve("pt-PT", "pt");
        Resolve("ga-IE", "en");

        Loc.Initialize(C("de-DE"));
        Check("German placeholder", Loc.Get("Search.Placeholder") == "Spotlight-Suche", Loc.Get("Search.Placeholder"));
        Check("German keywords include English", Loc.Keywords("Cmd.Shutdown.Keywords") is var k && k.Contains("herunterfahren") && k.Contains("shut down"));
        Check("unknown key falls back to key", Loc.Get("No.Such.Key") == "No.Such.Key");
        Check("format never throws", Loc.Format("Web.SearchFor") is { Length: > 0 });
        Loc.Initialize(C("en-US"));
    }

    private static void Resolve(string culture, string expected)
    {
        Loc.Initialize(C(culture));
        Check($"language for {culture} is {expected}", Loc.Language == expected, Loc.Language);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
