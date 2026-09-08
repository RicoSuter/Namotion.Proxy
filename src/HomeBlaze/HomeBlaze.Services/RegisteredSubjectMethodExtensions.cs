using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for discovering methods on subjects via MethodMetadata properties.
/// </summary>
public static class RegisteredSubjectMethodExtensions
{
    /// <summary>
    /// Gets all methods (operations and queries) for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetAllMethods(this RegisteredSubject subject)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(p => p.GetValue())
            .OfType<MethodMetadata>()
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>
    /// Gets all operation methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetOperationMethods(this RegisteredSubject subject)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(p => p.GetValue())
            .OfType<MethodMetadata>()
            .Where(m => m.Kind == MethodKind.Operation)
            .OrderBy(m => m.Position)
            .ToList();
    }

    /// <summary>
    /// Gets all query methods for the subject, ordered by Position.
    /// </summary>
    public static IReadOnlyList<MethodMetadata> GetQueryMethods(this RegisteredSubject subject)
    {
        return subject.Properties
            .Where(CanContainMethodMetadata)
            .Select(p => p.GetValue())
            .OfType<MethodMetadata>()
            .Where(m => m.Kind == MethodKind.Query)
            .OrderBy(m => m.Position)
            .ToList();
    }

    private static bool CanContainMethodMetadata(RegisteredSubjectProperty property)
    {
        // A metadata subclass may implement an interface not implemented by MethodMetadata itself.
        return property.Type.IsInterface ||
            property.Type.IsAssignableFrom(typeof(MethodMetadata)) ||
            typeof(MethodMetadata).IsAssignableFrom(property.Type);
    }

}
