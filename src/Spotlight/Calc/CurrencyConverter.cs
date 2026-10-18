using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;

namespace Spotlight.Calc;

/// <summary>
/// Currency conversion with the European Central Bank's daily reference rates ("100 usd in eur", "€50 in $",
/// "20 Franken"). Rates are cached on disk and refreshed every 12 hours; conversion itself never does I/O.
/// </summary>
public sealed class CurrencyConverter
{
    private const string RatesUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EUR", "USD", "JPY", "BGN", "CZK", "DKK", "GBP", "HUF", "PLN", "RON", "SEK", "CHF", "ISK", "NOK", "TRY",
        "AUD", "BRL", "CAD", "CNY", "HKD", "IDR", "ILS", "INR", "KRW", "MXN", "MYR", "NZD", "PHP", "SGD", "THB", "ZAR",
    };

    private static readonly HashSet<string> WholeUnits = new(StringComparer.OrdinalIgnoreCase) { "JPY", "KRW", "HUF", "ISK", "IDR" };
    private static readonly HashSet<string> NordicKrone = new(StringComparer.OrdinalIgnoreCase) { "SEK", "NOK", "DKK", "ISK" };

    /// <summary>"KRONE" marks names that mean the local Scandinavian crown; "GBP?" marks "pound", which is also a weight.</summary>
    private static readonly (string Text, string Code)[] Names = BuildNames();

    private readonly string _cacheFile;
    private readonly object _gate = new();
    private volatile Dictionary<string, double>? _rates; // units per 1 EUR
    private DateTime? _ratesDate;
    private Task? _fetch;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public CurrencyConverter(string cacheDirectory)
    {
        _cacheFile = Path.Combine(cacheDirectory, "ecb-rates.xml");
        try
        {
            if (File.Exists(_cacheFile)) LoadRatesFromXml(File.ReadAllText(_cacheFile));
        }
        catch (Exception)
        {
            // unreadable cache: download again later
        }
    }

    public event Action? RatesUpdated;

    public bool HasRates => _rates != null;
    public DateTime? RatesDate => _ratesDate;

    /// <summary>Downloads the rates if there are none or they are older than 12 hours. Never throws.</summary>
    public Task EnsureRatesAsync()
    {
        lock (_gate)
        {
            if (_fetch is { IsCompleted: false }) return _fetch;
            if (HasRates && !CacheIsStale()) return Task.CompletedTask;
            if (DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromMinutes(2)) return Task.CompletedTask; // offline: don't retry on every key
            _lastAttemptUtc = DateTime.UtcNow;
            return _fetch = FetchAsync();
        }
    }

    private bool CacheIsStale()
    {
        try { return !File.Exists(_cacheFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) > MaxAge; }
        catch (Exception) { return true; }
    }

    private async Task FetchAsync()
    {
        try
        {
            string xml = await Http.GetStringAsync(RatesUrl).ConfigureAwait(false);
            if (!LoadRatesFromXml(xml)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            await File.WriteAllTextAsync(_cacheFile, xml).ConfigureAwait(false);
            RatesUpdated?.Invoke();
        }
        catch (Exception)
        {
            // offline or ECB unreachable: keep the old rates
        }
    }

    public bool LoadRatesFromXml(string ecbXml)
    {
        try
        {
            var doc = XDocument.Parse(ecbXml);
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1 };
            DateTime? date = null;
            foreach (var cube in doc.Descendants().Where(e => e.Name.LocalName == "Cube"))
            {
                if ((string?)cube.Attribute("time") is { } time
                    && DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    date = d;
                if ((string?)cube.Attribute("currency") is { } currency && (string?)cube.Attribute("rate") is { } rate
                    && double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0)
                    rates[currency.Trim()] = r;
            }
            if (rates.Count < 2) return false;
            _ratesDate = date;
            _rates = rates;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryConvert(string input, CultureInfo culture, string localCurrency, out IReadOnlyList<CalcResult> results)
    {
        results = [];
        try
        {
            if (!TryParse(input.Trim(), culture, localCurrency, out double amount, out string from, out string? to, out bool fromAmbiguous))
                return false;

            to ??= fromAmbiguous ? null : DefaultTarget(from, localCurrency);
            if (to == null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;

            var rates = _rates;
            if (rates == null)
            {
                _ = EnsureRatesAsync(); // first currency query: fetch, RatesUpdated re-runs the search
                return false;
            }
            if (CacheIsStale()) _ = EnsureRatesAsync();
            if (!rates.TryGetValue(from, out double fromRate) || !rates.TryGetValue(to, out double toRate)) return false;

            double converted = amount / fromRate * toRate;
            bool whole = WholeUnits.Contains(to) && Math.Abs(converted) >= 100;
            string display = converted.ToString(whole ? "#,##0" : "#,##0.00", culture);
            string copy = converted.ToString(whole ? "0" : "0.00", culture);
            string inputText = $"{NumberParsing.Format(amount, culture, grouping: false, maxDecimals: 2)} {from.ToUpperInvariant()}";
            results = [new CalcResult(CalcKind.Currency, inputText, $"{display} {to.ToUpperInvariant()}", copy, "Currency", _ratesDate)];
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True if the input is a currency conversion (regardless of whether rates are loaded yet).</summary>
    public bool IsConversionRequest(string input, CultureInfo culture, string localCurrency)
    {
        try
        {
            return TryParse(input.Trim(), culture, localCurrency, out _, out _, out string? to, out bool fromAmbiguous)
                   && (to != null || !fromAmbiguous);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DefaultTarget(string from, string local)
    {
        if (!string.Equals(from, local, StringComparison.OrdinalIgnoreCase) && Codes.Contains(local)) return local;
        return string.Equals(from, "USD", StringComparison.OrdinalIgnoreCase) ? "EUR" : "USD";
    }

    /// <summary>"€50", "50 €", "100usd", "100 dollars in euro", "$100 to €", "10 britische Pfund in Euro".</summary>
    private static bool TryParse(string s, CultureInfo culture, string local, out double amount, out string from, out string? to, out bool fromAmbiguous)
    {
        amount = 0;
        from = "";
        to = null;
        fromAmbiguous = false;
        if (s.Length == 0 || s.Length > 80) return false;

        int pos = 0;
        string? prefix = null;
        bool prefixAmbiguous = false;
        if (!char.IsAsciiDigit(s[0]) && MatchCurrency(s, 0, local, out var code, out int length, out bool ambiguous))
        {
            prefix = code;
            prefixAmbiguous = ambiguous;
            pos = length;
            while (pos < s.Length && s[pos] == ' ') pos++;
        }

        if (!NumberParsing.TryRead(s, ref pos, culture, out amount)) return false;
        while (pos < s.Length && s[pos] == ' ') pos++;

        if (prefix != null)
        {
            from = prefix;
            fromAmbiguous = prefixAmbiguous;
        }
        else
        {
            if (!MatchCurrency(s, pos, local, out from, out int length2, out fromAmbiguous)) return false;
            pos += length2;
        }

        string rest = s[pos..].Trim();
        if (rest.Length == 0) return true;

        int connector = UnitConverter.MatchConnector(rest);
        if (connector <= 0) return false;
        string target = rest[connector..].Trim();
        if (!MatchCurrency(target, 0, local, out var toCode, out int targetLength, out bool toAmbiguous) || targetLength != target.Length)
            return false;
        // "10 pounds in kg" is weight; "10 pounds in euro" is money: at least one side must be clearly a currency.
        if (fromAmbiguous && toAmbiguous) return false;
        to = toCode;
        fromAmbiguous = false;
        return true;
    }

    private static bool MatchCurrency(string s, int start, string local, out string code, out int length, out bool ambiguous)
    {
        foreach (var (text, value) in Names)
        {
            if (start + text.Length > s.Length || string.Compare(s, start, text, 0, text.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            int end = start + text.Length;
            if (char.IsLetter(text[^1]) && end < s.Length && char.IsLetter(s[end])) continue; // "euro" in "europe"
            ambiguous = value == "GBP?";
            code = value switch
            {
                "GBP?" => "GBP",
                "KRONE" => NordicKrone.Contains(local) ? local.ToUpperInvariant() : "SEK",
                _ => value,
            };
            length = text.Length;
            return true;
        }
        code = "";
        length = 0;
        ambiguous = false;
        return false;
    }

    private static (string, string)[] BuildNames()
    {
        var names = new List<(string, string)>();
        void Map(string code, params string[] texts) { foreach (var t in texts) names.Add((t, code)); }

        foreach (var c in Codes) Map(c.ToUpperInvariant(), c);
        Map("USD", "$", "us$", "dollar", "dollars", "us dollar", "us dollars", "us-dollar", "us-dollars", "usdollar", "amerikanische dollar", "amerikanischer dollar");
        Map("EUR", "€", "euro", "euros");
        Map("GBP", "£", "pound sterling", "pounds sterling", "british pound", "british pounds", "britische pfund", "britisches pfund", "britischen pfund", "sterling");
        Map("GBP?", "pound", "pounds", "pfund");
        Map("JPY", "¥", "yen", "japanese yen", "japanische yen");
        Map("CNY", "元", "yuan", "renminbi", "rmb", "chinese yuan");
        Map("CHF", "fr.", "sfr", "sfr.", "franc", "francs", "franken", "swiss franc", "swiss francs", "schweizer franken");
        Map("INR", "₹", "rupee", "rupees", "rupie", "rupien", "indian rupee", "indian rupees", "indische rupie", "indische rupien");
        Map("KRW", "₩", "won", "korean won", "südkoreanische won");
        Map("TRY", "₺", "lira", "turkish lira", "türkische lira");
        Map("PLN", "zł", "zloty", "złoty", "zlotys", "polish zloty", "polnische zloty");
        Map("BRL", "r$", "real", "reais", "brazilian real", "brasilianische real");
        Map("SEK", "krona", "kronor", "swedish krona", "swedish kronor", "schwedische krone", "schwedische kronen");
        Map("NOK", "norwegian krone", "norwegian kroner", "norwegische krone", "norwegische kronen");
        Map("DKK", "danish krone", "danish kroner", "dänische krone", "dänische kronen");
        Map("ISK", "icelandic krona", "icelandic kronur", "isländische krone", "isländische kronen");
        Map("KRONE", "kr", "kr.", "krone", "kroner", "kronen");
        Map("CZK", "kč", "koruna", "czech koruna", "tschechische krone", "tschechische kronen");
        Map("HUF", "forint", "hungarian forint");
        Map("RON", "lei", "leu", "romanian leu");
        Map("BGN", "lev", "leva", "bulgarian lev");
        Map("MXN", "peso", "pesos", "mexican peso", "mexican pesos", "mexikanische peso");
        Map("ZAR", "rand", "south african rand");
        Map("ILS", "₪", "shekel", "shekels", "sheqel", "schekel");
        Map("THB", "฿", "baht", "thai baht");
        Map("IDR", "rp", "rupiah");
        Map("MYR", "rm", "ringgit");
        Map("PHP", "₱", "philippine peso", "philippine pesos");
        Map("CAD", "c$", "ca$", "canadian dollar", "canadian dollars", "kanadische dollar", "kanadischer dollar");
        Map("AUD", "a$", "au$", "australian dollar", "australian dollars", "australische dollar", "australischer dollar");
        Map("HKD", "hk$", "hong kong dollar", "hong kong dollars", "hongkong-dollar");
        Map("NZD", "nz$", "new zealand dollar", "new zealand dollars", "neuseeland-dollar");
        Map("SGD", "s$", "singapore dollar", "singapore dollars", "singapur-dollar");

        // Longest first: "us dollar" before "dollar", "hk$" before "$".
        return names.OrderByDescending(n => n.Item1.Length).ToArray();
    }
}
