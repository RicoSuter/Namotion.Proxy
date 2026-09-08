namespace HomeBlaze.Components.Abstractions.TimeZones;

/// <summary>A selectable timezone entry: an IANA id and a human label with its base offset.</summary>
public readonly record struct TimeZoneOption(string Id, string DisplayName);

/// <summary>
/// Builds the list of selectable timezones from the system zone database, presented with IANA ids.
/// </summary>
public static class TimeZoneCatalog
{
    /// <summary>
    /// Returns the system timezones mapped to IANA ids, deduplicated and sorted by id. Zones that have
    /// no IANA mapping (deprecated Windows-only zones) are dropped. The base UTC offset in the label is a
    /// hint for the picker and ignores daylight saving.
    /// </summary>
    public static IReadOnlyList<TimeZoneOption> GetZones()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<TimeZoneOption>();

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (CreateOption(zone) is { } option && seen.Add(option.Id))
            {
                options.Add(option);
            }
        }

        options.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return options;
    }

    /// <summary>
    /// Creates a selectable option for a zone, or null when the zone has no IANA id. Used both for the
    /// catalog and to make an out-of-catalog zone (for example the browser's own zone) selectable.
    /// </summary>
    public static TimeZoneOption? CreateOption(TimeZoneInfo zone) =>
        ToIanaId(zone.Id) is { } ianaId
            ? new TimeZoneOption(ianaId, $"{ianaId} ({FormatOffset(zone.BaseUtcOffset)})")
            : null;

    private static string? ToIanaId(string systemId)
    {
        // A Windows zone id maps to its canonical IANA id.
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(systemId, out var ianaId) && ianaId is not null)
        {
            return ianaId;
        }

        // Already an IANA id (non-Windows hosts, or a browser-reported zone): keep it as-is.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(systemId, out _))
        {
            return systemId;
        }

        // A deprecated or unmappable Windows-only zone (for example "Mid-Atlantic Standard Time"): drop it.
        return null;
    }

    private static string FormatOffset(TimeSpan offset) =>
        offset == TimeSpan.Zero
            ? "UTC"
            : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
}
