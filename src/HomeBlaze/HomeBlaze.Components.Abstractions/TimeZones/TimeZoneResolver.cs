namespace HomeBlaze.Components.Abstractions.TimeZones;

/// <summary>
/// Resolves an IANA (or Windows) zone id to a <see cref="TimeZoneInfo"/>, never throwing.
/// </summary>
public static class TimeZoneResolver
{
    /// <summary>
    /// Resolves the id to a zone. Tries the id directly, then an IANA to Windows mapping for
    /// Windows hosts, and finally falls back to UTC.
    /// </summary>
    public static TimeZoneInfo Resolve(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneId, out var windowsId) && windowsId is not null)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
