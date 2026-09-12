using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Recursively loads the subject graph and maps properties to ADS symbol paths.
/// Pure and testable - no ADS client dependency.
/// </summary>
internal sealed class AdsSubjectLoader
{
    private readonly IPropertyMapper<AdsPropertyMapping> _mapper;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdsSubjectLoader"/> class.
    /// </summary>
    /// <param name="mapper">The property mapper used to resolve per-property segments and ADS settings.</param>
    public AdsSubjectLoader(IPropertyMapper<AdsPropertyMapping> mapper)
    {
        _mapper = mapper;
    }

    /// <summary>
    /// Loads all leaf properties from the subject graph with their composed ADS symbol paths and mappings.
    /// </summary>
    /// <param name="rootSubject">The root subject to start loading from.</param>
    /// <returns>A list of tuples containing the registered property, its resolved ADS symbol path, and its mapping.</returns>
    public IReadOnlyList<(RegisteredSubjectProperty Property, string SymbolPath, AdsPropertyMapping Mapping)> LoadSubjectGraph(
        IInterceptorSubject rootSubject)
    {
        var result = new List<(RegisteredSubjectProperty, string, AdsPropertyMapping)>();
        var loadedSubjects = new HashSet<IInterceptorSubject>();
        LoadSubjectRecursive(rootSubject, rootSubject, null, loadedSubjects, result);
        return result;
    }

    private void LoadSubjectRecursive(
        IInterceptorSubject subject,
        IInterceptorSubject rootSubject,
        string? parentPath,
        HashSet<IInterceptorSubject> loadedSubjects,
        List<(RegisteredSubjectProperty, string, AdsPropertyMapping)> result)
    {
        if (!loadedSubjects.Add(subject))
        {
            return;
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        foreach (var property in registeredSubject.Properties)
        {
            if (!_mapper.TryGetMapping(property, rootSubject, out var mapping) || mapping.Segment is null)
            {
                continue;
            }

            var segment = mapping.Segment;
            var propertyPath = parentPath is null ? segment : $"{parentPath}.{segment}";

            // Check dictionary and collection before subject reference because
            // interface types like IList<T> and IDictionary<K,V> also satisfy IsSubjectReference.
            if (property.IsSubjectDictionary)
            {
                foreach (var child in property.Children)
                {
                    var childPath = $"{propertyPath}.{child.Index}";
                    LoadSubjectRecursive(child.Subject, rootSubject, childPath, loadedSubjects, result);
                }
            }
            else if (property.IsSubjectCollection)
            {
                var index = 0;
                foreach (var child in property.Children)
                {
                    var childPath = $"{propertyPath}[{index}]";
                    LoadSubjectRecursive(child.Subject, rootSubject, childPath, loadedSubjects, result);
                    index++;
                }
            }
            else if (property.IsSubjectReference)
            {
                var child = property.Children.SingleOrDefault();
                if (child.Subject is not null)
                {
                    LoadSubjectRecursive(child.Subject, rootSubject, propertyPath, loadedSubjects, result);
                }
            }
            else
            {
                // Scalar or primitive array - single ADS symbol
                result.Add((property, propertyPath, mapping));
            }
        }
    }
}
