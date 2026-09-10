namespace HomeBlaze.Components.Abstractions.TimeZones;

/// <summary>
/// Per-circuit display timezone. Formats absolute timestamps into the user's chosen zone and parses
/// picker input from that zone back to UTC. Storage stays UTC; this only affects presentation.
/// </summary>
public interface ITimeZoneDisplay
{
    /// <summary>True once a zone has been resolved (browser detected or restored from the saved choice).</summary>
    bool IsResolved { get; }

    /// <summary>The resolved zone, or null while unresolved.</summary>
    TimeZoneInfo? Zone { get; }

    /// <summary>The active preference (Automatic or a specific zone).</summary>
    TimeZonePreference Preference { get; }

    /// <summary>Text shown for a timestamp while the zone is not yet resolved.</summary>
    string Placeholder { get; }

    /// <summary>Raised when the resolved zone changes, so consumers can re-render.</summary>
    event Action? Changed;

    /// <summary>Formats an absolute instant in the chosen zone (placeholder while unresolved).</summary>
    string Format(DateTimeOffset value);

    /// <summary>Formats a <see cref="DateTime"/>: Utc kind is converted; Local/Unspecified render as-is.</summary>
    string Format(DateTime value);

    /// <summary>Returns the wall-clock <see cref="DateTime"/> of an instant in the chosen zone.</summary>
    DateTime ToZoned(DateTimeOffset value);

    /// <summary>Interprets a picker's wall-clock value as being in the chosen zone and returns the UTC instant.</summary>
    DateTimeOffset ToUtc(DateTime wallClock);

    /// <summary>Sets the resolved zone for a preference and raises <see cref="Changed"/>.</summary>
    void SetResolved(TimeZonePreference preference, TimeZoneInfo zone);
}
