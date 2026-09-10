using System.Globalization;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Mcp;

/// <summary>
/// Pure parsing and normalization for the <c>get_property_history</c> MCP tool. Kept separate so
/// the boundary parsing is unit-testable without a live MCP server or registry.
/// </summary>
public static class HistoryToolParsing
{
    /// <summary>All recognized aggregation identifiers (canonical PascalCase), from <see cref="HistoryAggregations.All"/>.</summary>
    public static readonly IReadOnlySet<string> AllAggregations =
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    /// <summary>
    /// Normalizes a case-insensitive aggregation name to its canonical form, defaulting to
    /// <see cref="HistoryAggregations.Last"/> when omitted. Returns null when the name is unknown.
    /// </summary>
    public static string? NormalizeAggregation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HistoryAggregations.Last;
        }

        foreach (var known in AllAggregations)
        {
            if (string.Equals(known, raw, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a bucket size: a number with an s/m/h/d (or ms) unit suffix (for example "5m"),
    /// or a TimeSpan literal ("00:05:00"). Null/blank means a raw (unbucketed) query. The result is
    /// always positive. Throws <see cref="FormatException"/> for an unrecognized or non-positive value.
    /// A bare number ("5") is rejected so it is not silently read as a count of days.
    /// </summary>
    public static TimeSpan? ParseBucket(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var splitIndex = 0;
        while (splitIndex < text.Length && (char.IsDigit(text[splitIndex]) || text[splitIndex] is '.' or '+' or '-'))
        {
            splitIndex++;
        }

        var unit = text[splitIndex..];
        if (splitIndex > 0 && unit.Length > 0 && unit.All(char.IsLetter)
            && double.TryParse(text[..splitIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            // double.TryParse succeeds on an overflowing literal by returning infinity, and the
            // TimeSpan factories then throw OverflowException, which the caller does not catch.
            if (value <= 0 || !double.IsFinite(value))
            {
                throw new FormatException("Bucket size must be positive and finite.");
            }

            try
            {
                return unit.ToLowerInvariant() switch
                {
                    "ms" => TimeSpan.FromMilliseconds(value),
                    "s" => TimeSpan.FromSeconds(value),
                    "m" => TimeSpan.FromMinutes(value),
                    "h" => TimeSpan.FromHours(value),
                    "d" => TimeSpan.FromDays(value),
                    _ => throw new FormatException($"Unknown bucket unit '{unit}'.")
                };
            }
            catch (OverflowException)
            {
                throw new FormatException($"Bucket size '{text}' is out of range.");
            }
        }

        if (text.Contains(':'))
        {
            TimeSpan span;
            try
            {
                span = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                throw new FormatException($"Bucket size '{text}' is out of range.");
            }

            if (span <= TimeSpan.Zero)
            {
                throw new FormatException("Bucket size must be positive.");
            }

            return span;
        }

        throw new FormatException(
            $"Could not parse bucket '{raw}'. Use a unit suffix like 5m, 30s, 1h, 7d, or a TimeSpan literal like 00:05:00.");
    }

    /// <summary>Parses an ISO 8601 timestamp; a bare (offset-less) timestamp is treated as UTC.</summary>
    public static DateTimeOffset ParseTimestamp(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Maps a property's declared type to the response value-type hint (number / string / boolean / enum).
    /// Derived from the type itself, not the storage column, so bool and enum are distinguished from their columns.
    /// </summary>
    public static string ValueType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return "enum";
        }

        if (underlying == typeof(bool))
        {
            return "boolean";
        }

        if (underlying == typeof(string))
        {
            return "string";
        }

        return "number";
    }
}
