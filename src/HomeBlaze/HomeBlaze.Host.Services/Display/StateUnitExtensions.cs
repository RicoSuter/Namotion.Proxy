using System.Globalization;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// Extension methods for formatting property values with unit support.
/// </summary>
public static class StateUnitExtensions
{
    private static readonly (StateUnit Unit, string Suffix, decimal Factor)[][] UnitFamilies =
    [
        [(StateUnit.Watt, "W", 1m), (StateUnit.Kilowatt, "kW", 1000m)],
        [(StateUnit.WattHour, "Wh", 1m), (StateUnit.KilowattHour, "kWh", 1000m)],
        [(StateUnit.Millimeter, "mm", 1m), (StateUnit.Meter, "m", 1000m), (StateUnit.Kilometer, "km", 1_000_000m)],
        [(StateUnit.Milliampere, "mA", 1m), (StateUnit.Ampere, "A", 1000m)],
        [(StateUnit.Byte, "B", 1m), (StateUnit.Kilobyte, "kB", 1000m), (default, "MB", 1_000_000m), (default, "GB", 1_000_000_000m)],
        [(StateUnit.KilobytePerSecond, "kB/s", 1m), (default, "MB/s", 1000m)],
    ];

    private static readonly Dictionary<StateUnit, (int FamilyIndex, int UnitIndex)> UnitFamilyLookup = BuildLookup();

    private static Dictionary<StateUnit, (int FamilyIndex, int UnitIndex)> BuildLookup()
    {
        var lookup = new Dictionary<StateUnit, (int, int)>();
        for (var familyIndex = 0; familyIndex < UnitFamilies.Length; familyIndex++)
        {
            var family = UnitFamilies[familyIndex];
            for (var unitIndex = 0; unitIndex < family.Length; unitIndex++)
            {
                var entry = family[unitIndex];
                if (entry.Unit != default)
                {
                    lookup[entry.Unit] = (familyIndex, unitIndex);
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Renders a property value with proper formatting including unit support.
    /// </summary>
    public static string GetPropertyDisplayValue(
        this RegisteredSubjectProperty property, object? value, ITimeZoneDisplay? timeZone = null)
    {
        if (value == null)
        {
            return "null";
        }

        // Handle TimeSpan specially (before unit formatting)
        if (value is TimeSpan ts)
        {
            return FormatTimeSpan(ts);
        }

        // Apply unit formatting if specified
        var stateMetadata = property.GetStateMetadata();
        if (stateMetadata != null && stateMetadata.Unit != StateUnit.Default)
        {
            var unit = stateMetadata.Unit;

            // Percent and Currency use special formatting, not auto-scaling
            if (unit == StateUnit.Percent)
            {
                var percent = Math.Round(Convert.ToDecimal(value) * 100m, 1);
                return $"{percent.ToString("0.##", CultureInfo.InvariantCulture)}%";
            }

            if (unit == StateUnit.Currency)
            {
                return $"{value:C}";
            }

            if (unit == StateUnit.HexColor)
            {
                return value.ToString() ?? "";
            }

            // Try to convert to decimal for auto-scaling
            try
            {
                var decimalValue = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return FormatWithUnit(decimalValue, unit);
            }
            catch
            {
                // Fall through to default formatting for non-convertible types
            }
        }

        // Default formatting for common types
        return value switch
        {
            bool b => b ? "Yes" : "No",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            float f => f.ToString("0.##", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
            DateTime dt => timeZone is not null
                ? timeZone.Format(dt)
                : dt.ToString("g"),
            DateTimeOffset dto => timeZone is not null
                ? timeZone.Format(dto)
                : $"{dto.ToLocalTime().ToString("g")} {dto.ToLocalTime():zzz}",
            Enum e => e.ToString(),
            IEnumerable<string> strings => string.Join("\n", strings),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    /// Formats a decimal value with auto-scaling within its unit family.
    /// </summary>
    public static string FormatWithUnit(decimal value, StateUnit unit)
    {
        // Check if this unit belongs to a scalable family
        if (UnitFamilyLookup.TryGetValue(unit, out var lookup))
        {
            var family = UnitFamilies[lookup.FamilyIndex];
            var inputEntry = family[lookup.UnitIndex];

            // Convert to base unit (multiply by the input unit's factor)
            var baseValue = value * inputEntry.Factor;

            // Find the best display unit: largest unit where |baseValue / factor| >= 1
            var bestIndex = 0; // default to smallest
            for (var i = family.Length - 1; i >= 0; i--)
            {
                if (Math.Abs(baseValue / family[i].Factor) >= 1m)
                {
                    bestIndex = i;
                    break;
                }
            }

            var bestEntry = family[bestIndex];
            var displayValue = baseValue / bestEntry.Factor;

            return FormatDecimalValue(displayValue, bestEntry.Suffix);
        }

        // No family found: use static suffix with space handling
        var unitInfo = GetUnitInfo(unit);
        if (unitInfo != null)
        {
            var rounded = Math.Round(value, 2);
            var formatted = rounded.ToString("0.##", CultureInfo.InvariantCulture);
            var separator = unitInfo.Value.NeedsSpace ? " " : "";
            return $"{formatted}{separator}{unitInfo.Value.Suffix}";
        }

        return unit == StateUnit.Default
            ? value.ToString(CultureInfo.InvariantCulture)
            : $"{value.ToString(CultureInfo.InvariantCulture)} {unit}";
    }

    private static string FormatDecimalValue(decimal value, string suffix)
    {
        // Round to 2 decimal places, strip trailing zeros
        var rounded = Math.Round(value, 2);
        var formatted = rounded.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{formatted} {suffix}";
    }

    /// <summary>
    /// Gets the unit suffix and whether it needs a space separator.
    /// </summary>
    public static (string Suffix, bool NeedsSpace)? GetUnitInfo(StateUnit unit) => unit switch
    {
        StateUnit.Percent => ("%", false),
        StateUnit.DegreeCelsius => ("°C", false),
        StateUnit.Degree => ("°", false),
        StateUnit.Watt => ("W", true),
        StateUnit.Kilowatt => ("kW", true),
        StateUnit.WattHour => ("Wh", true),
        StateUnit.Volt => ("V", true),
        StateUnit.Ampere => ("A", true),
        StateUnit.Hertz => ("Hz", true),
        StateUnit.Lumen => ("lm", true),
        StateUnit.Lux => ("lx", true),
        StateUnit.Kilometer => ("km", true),
        StateUnit.Meter => ("m", true),
        StateUnit.Millimeter => ("mm", true),
        StateUnit.MillimeterPerHour => ("mm/h", true),
        StateUnit.Byte => ("B", true),
        StateUnit.Kilobyte => ("kB", true),
        StateUnit.KilobytePerSecond => ("kB/s", true),
        StateUnit.MegabitPerSecond => ("Mbit/s", true),
        StateUnit.KilowattHour => ("kWh", true),
        StateUnit.Milliampere => ("mA", true),
        StateUnit.LiterPerHour => ("l/h", true),
        StateUnit.MeterPerSecond => ("m/s", true),
        StateUnit.Hectopascal => ("hPa", true),
        StateUnit.UvIndex => ("UV", true),
        StateUnit.HexColor => ("hex", true),
        _ => null
    };

    /// <summary>
    /// Gets the unit suffix string for a given StateUnit.
    /// </summary>
    public static string? GetUnitSuffix(StateUnit unit) => GetUnitInfo(unit)?.Suffix;

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalSeconds < 5
            ? $"{ts.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)} ms"
            : ts.TotalHours >= 1
                ? $"{ts.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h"
                : ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
}
