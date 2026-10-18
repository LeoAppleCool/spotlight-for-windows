using System.Globalization;
using Spotlight.Calc;
using Spotlight.Core;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>Calculator, unit and currency conversion (Enter copies the result).</summary>
internal sealed class CalcProvider : ISearchProvider
{
    private readonly CurrencyConverter _currency = new(AppPaths.CacheDir);

    /// <summary>Raised when exchange rates arrive so the current query can be evaluated again.</summary>
    public event Action? RatesUpdated
    {
        add => _currency.RatesUpdated += value;
        remove => _currency.RatesUpdated -= value;
    }

    /// <summary>Downloads exchange rates now if they are missing or outdated (used by the diagnostics mode).</summary>
    public Task EnsureRatesAsync() => _currency.EnsureRatesAsync();

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        string raw = query.Raw.Trim();
        if (raw.Length == 0 || raw.Length > 200) return;

        var culture = CultureInfo.CurrentCulture;
        var region = RegionInfo.CurrentRegion;

        if (MathEvaluator.TryEvaluate(raw, culture, out var math))
            results.Add(Create(math, Category.Calculator, Glyphs.Calculator, math.Input, 3000));

        if (UnitConverter.TryConvert(raw, culture, region.IsMetric, out var units))
        {
            int i = 0;
            foreach (var u in units)
                results.Add(Create(u, Category.Conversion, Glyphs.Ruler, $"{u.Input} · {Loc.Get("Unit." + u.Category)}", 2900 - i++));
        }

        if (_currency.TryConvert(raw, culture, region.ISOCurrencySymbol, out var money))
        {
            int i = 0;
            foreach (var m in money)
            {
                string date = m.RatesDate?.ToString("d", culture) ?? "";
                results.Add(Create(m, Category.Conversion, Glyphs.Currency, $"{m.Input} · {Loc.Format("Calc.Rates", date)}", 2950 - i++));
            }
        }
        else if (!_currency.HasRates && _currency.IsConversionRequest(raw, culture, region.ISOCurrencySymbol))
        {
            // First currency query ever: the rates are being downloaded; RatesUpdated re-runs the search.
            results.Add(new SearchResult
            {
                Key = "calc:rates-loading",
                Title = Loc.Get("Calc.RatesLoading"),
                Subtitle = raw,
                Category = Category.Conversion,
                Score = 2950,
                Glyph = Glyphs.Currency,
                GlyphFont = Glyphs.Font,
                CopyOnEnter = true,
            });
        }
    }

    private static SearchResult Create(CalcResult r, Category category, string glyph, string subtitle, double score)
    {
        string copy = r.CopyText;
        return new SearchResult
        {
            Key = $"calc:{r.Kind}:{r.Input}={r.Display}",
            Title = r.Kind == CalcKind.Math ? "= " + r.Display : r.Display,
            Subtitle = subtitle,
            Category = category,
            Score = score,
            Glyph = glyph,
            GlyphFont = Glyphs.Font,
            CopyValue = copy,
            CopyOnEnter = true,
            Execute = () => ClipboardHelper.SetText(copy),
        };
    }
}
