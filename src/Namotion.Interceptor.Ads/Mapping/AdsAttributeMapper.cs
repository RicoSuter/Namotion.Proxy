using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Ads.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// Layers the ADS read-mode, cycle-time, max-delay, and priority settings from
/// <see cref="AdsVariableAttribute"/> onto the mapping. Sets no segment. Read mode and the millisecond
/// settings convert their "unset" sentinels to null; priority has no sentinel (0, and negatives, are valid
/// priorities), so it always passes through.
/// </summary>
public sealed class AdsAttributeMapper : IPropertyMapper<AdsPropertyMapping>
{
    private readonly string _connectorName;

    public AdsAttributeMapper(string? connectorName = null)
    {
        _connectorName = connectorName ?? AdsConstants.DefaultConnectorName;
    }

    /// <inheritdoc />
    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out AdsPropertyMapping? mapping)
    {
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is AdsVariableAttribute ads && ads.Name == _connectorName)
            {
                mapping = new AdsPropertyMapping(
                    Segment: null,
                    ReadMode: ads.ReadMode == AdsReadMode.Auto ? null : ads.ReadMode,
                    CycleTime: ads.CycleTime == int.MinValue ? null : ads.CycleTime,
                    MaxDelay: ads.MaxDelay == int.MinValue ? null : ads.MaxDelay,
                    Priority: ads.Priority);
                return true;
            }
        }

        mapping = null;
        return false;
    }
}
