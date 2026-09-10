using System.Buffers;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.GraphQL;

/// <summary>
/// Matches property changes against GraphQL selection paths.
/// </summary>
public static class GraphQLSelectionMatcher
{
    private const int MaxPathDepth = 16;

    /// <summary>
    /// Checks if a property change matches any of the selected paths.
    /// The paths use '.' as separator, matching the GraphQL field nesting convention.
    /// </summary>
    public static bool IsPropertyInSelection(
        SubjectPropertyChange change,
        IReadOnlySet<string> selectedPaths,
        IPathProvider pathProvider,
        IInterceptorSubject rootSubject)
    {
        var registeredProperty = change.Property.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return false;
        }

        // Collect path segments leaf-to-root using a pooled array to avoid List allocation.
        var segments = ArrayPool<string>.Shared.Rent(MaxPathDepth);
        try
        {
            var segmentCount = 0;
            var current = registeredProperty;

            while (current is not null)
            {
                var segment = pathProvider.TryGetPropertySegment(current);
                if (segment is not null)
                {
                    if (segmentCount >= MaxPathDepth)
                    {
                        // Deeper than expected; fall through to receive all changes.
                        return true;
                    }

                    segments[segmentCount++] = segment;
                }

                if (ReferenceEquals(current.Parent.Subject, rootSubject))
                {
                    break;
                }

                var parents = current.Parent.Parents;
                current = parents.Length > 0 ? parents[0].Property : null;
            }

            if (segmentCount == 0)
            {
                return false;
            }

            // Segments are in leaf-to-root order.
            // Match each selected path (root-to-leaf) by walking segments from end to start,
            // avoiding both Array.Reverse and path string construction.
            foreach (var selectedPath in selectedPaths)
            {
                if (PathMatchesSegments(selectedPath.AsSpan(), segments, segmentCount))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(segments, clearArray: true);
        }
    }

    /// <summary>
    /// Checks whether a dot-separated selected path matches the given segments (stored leaf-to-root).
    /// Handles exact match, parent match (change is ancestor of selection),
    /// and child match (change is descendant of selection).
    /// </summary>
    private static bool PathMatchesSegments(
        ReadOnlySpan<char> selectedPath,
        string[] segments,
        int segmentCount)
    {
        var pathPosition = 0;
        var segmentIndex = segmentCount - 1; // Start from root

        while (segmentIndex >= 0 && pathPosition < selectedPath.Length)
        {
            var remaining = selectedPath[pathPosition..];
            var dotIndex = remaining.IndexOf('.');
            var pathPiece = dotIndex >= 0 ? remaining[..dotIndex] : remaining;

            if (!pathPiece.SequenceEqual(segments[segmentIndex].AsSpan()))
            {
                return false;
            }

            pathPosition += pathPiece.Length + 1;
            segmentIndex--;
        }

        // If we reach here, all compared segments matched.
        // - Both exhausted: exact match
        // - Segments exhausted, path has more: change is ancestor of selection
        // - Path exhausted, more segments: change is descendant of selection
        return true;
    }
}
