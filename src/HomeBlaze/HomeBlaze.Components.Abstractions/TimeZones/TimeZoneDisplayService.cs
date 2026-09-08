using System.Globalization;

namespace HomeBlaze.Components.Abstractions.TimeZones;

/// <inheritdoc />
public sealed class TimeZoneDisplayService : ITimeZoneDisplay
{
    /// <inheritdoc />
    public bool IsResolved => Zone is not null;

    /// <inheritdoc />
    public TimeZoneInfo? Zone { get; private set; }

    /// <inheritdoc />
    public TimeZonePreference Preference { get; private set; } = TimeZonePreference.Automatic;

    /// <inheritdoc />
    public string Placeholder => "…";

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void SetResolved(TimeZonePreference preference, TimeZoneInfo zone)
    {
        Preference = preference;
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public string Format(DateTimeOffset value)
    {
        if (Zone is null)
        {
            return Placeholder;
        }

        var zoned = TimeZoneInfo.ConvertTime(value, Zone);

        // The offset is formatted invariantly: "zzz" separates hours and minutes with the culture's
        // time separator, so under a culture using "." it renders "+02.00".
        return $"{zoned.ToString("g", CultureInfo.CurrentCulture)} " +
               $"{zoned.ToString("zzz", CultureInfo.InvariantCulture)}";
    }

    /// <inheritdoc />
    public string Format(DateTime value)
    {
        if (Zone is null)
        {
            return Placeholder;
        }

        // Local is the server's zone, not the viewer's, so it has to be converted rather than shown
        // verbatim: System.Text.Json gives Local kind to any timestamp carrying a non-Z offset.
        // Unspecified is treated as UTC, matching the invariant that stored timestamps are UTC; without
        // that, the same property renders converted or raw depending on which provider produced it.
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utc, Zone).ToString("g", CultureInfo.CurrentCulture);
    }

    /// <inheritdoc />
    public DateTime ToZoned(DateTimeOffset value) =>
        Zone is null ? value.UtcDateTime : TimeZoneInfo.ConvertTime(value, Zone).DateTime;

    /// <inheritdoc />
    public DateTimeOffset ToUtc(DateTime wallClock)
    {
        if (Zone is null)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Utc));
        }

        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        // Both DST edge cases resolve deliberately rather than through GetUtcOffset's silent defaults,
        // which pick the daylight offset for a time that never happened and the standard offset for one
        // that happened twice. A picked date landing on a spring-forward midnight otherwise loses an
        // hour off the end of the requested range with nothing to indicate it.
        if (Zone.IsInvalidTime(unspecified))
        {
            // The wall clock never showed this instant; take the first instant that did exist.
            var beforeGap = new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified.AddDays(-1)));
            return beforeGap.ToUniversalTime();
        }

        if (Zone.IsAmbiguousTime(unspecified))
        {
            // It happened twice; take the earlier occurrence, so a range built from wall-clock dates
            // covers the whole ambiguous hour instead of only its second half.
            var offsets = Zone.GetAmbiguousTimeOffsets(unspecified);
            var earliest = offsets[0];
            foreach (var candidate in offsets)
            {
                if (candidate > earliest)
                {
                    earliest = candidate;
                }
            }

            return new DateTimeOffset(unspecified, earliest).ToUniversalTime();
        }

        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
