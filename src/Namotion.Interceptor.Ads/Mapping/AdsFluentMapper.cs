using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// Code-based ADS mapper produced by <see cref="AdsFluentMapperBuilder{TRoot}.Build"/>. Resolves the relative
/// segment via a <see cref="FluentPathProvider{TMetadata}"/> and layers the registered ADS settings on top.
/// </summary>
public sealed class AdsFluentMapper : AdsPathProviderMapper
{
    private readonly FluentMappingRegistry<AdsPropertyMapping> _registry;

    public AdsFluentMapper(FluentMappingRegistry<AdsPropertyMapping> registry, char pathSeparator = '.')
        : base(new FluentPathProvider<AdsPropertyMapping>(registry, pathSeparator))
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public override bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out AdsPropertyMapping? mapping)
    {
        if (!base.TryGetMapping(property, rootSubject, out var segmentMapping))
        {
            mapping = null;
            return false;
        }

        mapping = _registry.TryGetPropertyMetadata(property.Subject.GetType(), property.Name, out var metadata)
            ? AdsPropertyMapping.Merge(metadata, segmentMapping)
            : segmentMapping;

        return true;
    }
}
