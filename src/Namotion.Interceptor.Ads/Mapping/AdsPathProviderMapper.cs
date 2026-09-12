using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// Supplies a property's relative symbol-path segment from a <see cref="PathProviderBase"/> and contributes
/// no ADS settings.
/// </summary>
public class AdsPathProviderMapper : IPropertyMapper<AdsPropertyMapping>
{
    private readonly PathProviderBase _pathProvider;

    public AdsPathProviderMapper(PathProviderBase pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    /// <inheritdoc />
    public virtual bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out AdsPropertyMapping? mapping)
    {
        if (!_pathProvider.IsPropertyIncluded(property))
        {
            mapping = null;
            return false;
        }

        var segment = _pathProvider.TryGetPropertySegment(property);
        if (segment is null)
        {
            mapping = null;
            return false;
        }

        mapping = new AdsPropertyMapping(Segment: segment);
        return true;
    }
}
