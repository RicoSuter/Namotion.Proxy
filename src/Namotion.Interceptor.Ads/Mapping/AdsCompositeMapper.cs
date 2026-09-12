using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// Combines multiple forward ADS mappers, merging partial mappings field by field with "last wins" semantics
/// (a later mapper's non-null field overrides an earlier one).
/// </summary>
public sealed class AdsCompositeMapper : IPropertyMapper<AdsPropertyMapping>
{
    private readonly IPropertyMapper<AdsPropertyMapping>[] _mappers;

    public AdsCompositeMapper(params IPropertyMapper<AdsPropertyMapping>[] mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);

        _mappers = (IPropertyMapper<AdsPropertyMapping>[])mappers.Clone();
        for (var i = 0; i < _mappers.Length; i++)
        {
            if (_mappers[i] is null)
            {
                throw new ArgumentException($"Mapper at index {i} must not be null.", nameof(mappers));
            }
        }
    }

    /// <summary>
    /// Builds the default ADS mapper: a path-provider mapper for the segment plus an attribute mapper for the
    /// ADS settings. Compose a fluent mapper after this (it wins on overlap) by passing both to a new instance.
    /// </summary>
    public static AdsCompositeMapper CreateDefault(string? connectorName = null, PathProviderBase? pathProvider = null)
    {
        var name = connectorName ?? AdsConstants.DefaultConnectorName;
        return new AdsCompositeMapper(
            new AdsPathProviderMapper(pathProvider ?? new AttributeBasedPathProvider(name, '.')),
            new AdsAttributeMapper(name));
    }

    /// <inheritdoc />
    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out AdsPropertyMapping? mapping)
    {
        mapping = null;
        var found = false;
        foreach (var inner in _mappers)
        {
            if (inner.TryGetMapping(property, rootSubject, out var partial))
            {
                // Later mappers override earlier ones (partial = primary, accumulated = fallback).
                mapping = found ? AdsPropertyMapping.Merge(partial, mapping!) : partial;
                found = true;
            }
        }

        return found;
    }
}
