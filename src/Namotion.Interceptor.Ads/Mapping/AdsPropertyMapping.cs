using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// ADS connector-specific property mapping carrying the relative symbol-path segment and the
/// notification/polling settings. All fields are nullable; a null field defers to the configured default.
/// </summary>
public sealed record AdsPropertyMapping(
    string? Segment = null,
    AdsReadMode? ReadMode = null,
    int? CycleTime = null,
    int? MaxDelay = null,
    int? Priority = null)
    : IPropertyMapping<AdsPropertyMapping>
{
    public static AdsPropertyMapping Merge(AdsPropertyMapping primary, AdsPropertyMapping fallback) => new(
        Segment: primary.Segment ?? fallback.Segment,
        ReadMode: primary.ReadMode ?? fallback.ReadMode,
        CycleTime: primary.CycleTime ?? fallback.CycleTime,
        MaxDelay: primary.MaxDelay ?? fallback.MaxDelay,
        Priority: primary.Priority ?? fallback.Priority);
}
