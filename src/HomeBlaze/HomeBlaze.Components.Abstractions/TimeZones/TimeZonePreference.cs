namespace HomeBlaze.Components.Abstractions.TimeZones;

/// <summary>
/// A user's display timezone choice: either "follow this browser" or a specific IANA zone.
/// </summary>
public readonly record struct TimeZonePreference
{
    /// <summary>The cookie/state token used for the Automatic choice.</summary>
    public const string AutomaticToken = "Automatic";

    private TimeZonePreference(string? zoneId) => ZoneId = zoneId;

    /// <summary>The pinned IANA zone id, or null when following the browser.</summary>
    public string? ZoneId { get; }

    /// <summary>True when display should follow the browser's reported zone.</summary>
    public bool IsAutomatic => string.IsNullOrEmpty(ZoneId);

    /// <summary>Follow the browser's zone (also the default value).</summary>
    public static TimeZonePreference Automatic => default;

    /// <summary>Pin a specific IANA zone.</summary>
    public static TimeZonePreference Specific(string zoneId) => new(zoneId);

    /// <summary>The value stored in the cookie / persisted state.</summary>
    public string ToCookieValue() => IsAutomatic ? AutomaticToken : ZoneId!;

    /// <summary>Parse a stored cookie/state value back into a preference.</summary>
    public static TimeZonePreference FromCookieValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == AutomaticToken
            ? Automatic
            : Specific(value.Trim());
}
