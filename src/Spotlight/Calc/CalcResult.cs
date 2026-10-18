namespace Spotlight.Calc;

public enum CalcKind { Math, Unit, Currency }

/// <summary>One calculator / converter answer shown as a launcher result row.</summary>
/// <param name="Kind">Which engine produced the result.</param>
/// <param name="Input">Canonical rendering of the input, e.g. "2 × (3 + 4)", "10 km", "100 USD".</param>
/// <param name="Display">Formatted result, e.g. "14", "6.21371 mi", "92.35 EUR".</param>
/// <param name="CopyText">Number only (culture decimal separator, no group separators); copied on Enter.</param>
/// <param name="Category">Unit category, "Currency", or null for math.</param>
/// <param name="RatesDate">ECB reference date (currency results only).</param>
public sealed record CalcResult(
    CalcKind Kind,
    string Input,
    string Display,
    string CopyText,
    string? Category,
    DateTime? RatesDate);
