using System;
using System.Threading;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// The runtime tier of the path validation boundary: walks the decomposed <see cref="PathSegment"/>s
/// against a concrete subject graph and resolves the leaf value. Resolution is by property name against
/// the runtime subject, so a shape that was valid at subscribe time may still be unreachable now (a null
/// intermediate, an out-of-range index, a missing key). The walk never throws: any missing property,
/// non-intercepted segment, throwing getter or container operation, out-of-range index, missing key, or a
/// leaf value not assignable to the requested value type resolves the whole path to
/// <see cref="SubjectPathValue{TValue}.Unresolved"/> rather than propagating the failure.
/// </summary>
internal static class PathWalker
{
    /// <summary>
    /// Walks <paramref name="segments"/> from <paramref name="root"/> and returns the leaf value, or
    /// unresolved when any segment is unreachable. <paramref name="resolvedSubjects"/> (length ==
    /// <paramref name="segments"/>.Length) records the subject each segment is read on: index 0 is the
    /// root and each resolved intermediate sets the next entry; entries beyond the resolved prefix are
    /// left null so a caller never observes a stale subject from a prior walk.
    /// </summary>
    internal static SubjectPathValue<TValue> Walk<TValue>(
        PathSegment[] segments,
        IInterceptorSubject root,
        IInterceptorSubject?[] resolvedSubjects,
        ResolvedPathSegment<TValue>?[]? resolvedSegments = null)
    {
        // Clear any stale subjects from a prior walk up front so an early bail-out below leaves every
        // entry beyond the resolved prefix null without further bookkeeping.
        Array.Clear(resolvedSubjects, 0, resolvedSubjects.Length);
        resolvedSubjects[0] = root;

        // Single never-throw boundary around the whole walk: there is no recover-and-continue path, so
        // any exception from a getter, an accessor build/invoke or a container operation means the leaf is
        // unresolved. The already-resolved prefix stays recorded in resolvedSubjects; nothing past it was
        // written, so it remains null.
        try
        {
            IInterceptorSubject? current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                if (current is null)
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                var resolvedSegment = resolvedSegments is null
                    ? null
                    : Volatile.Read(ref resolvedSegments[i]);
                if (resolvedSegment is not null && ReferenceEquals(resolvedSegment.Subject, current))
                {
                    if (segment.IsLeaf)
                    {
                        return resolvedSegment.ReadLeaf(current);
                    }

                    var cachedChild = resolvedSegment.ResolveChild(current, segment);
                    if (cachedChild is null)
                    {
                        return SubjectPathValue<TValue>.Unresolved;
                    }

                    resolvedSubjects[i + 1] = cachedChild;
                    current = cachedChild;
                    continue;
                }

                if (!current.Properties.TryGetValue(segment.PropertyName, out var metadata))
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                if (!(metadata.IsIntercepted || metadata.IsDerived))
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                if (segment.IsLeaf)
                {
                    var leafAccessor = BuildLeafAccessor<TValue>(current.GetType(), metadata, out var isTypedLeaf);
                    return ReadLeaf<TValue>(current, leafAccessor, isTypedLeaf);
                }

                var childAccessor = BuildChildAccessor(current.GetType(), metadata, segment);
                var child = ResolveChild(current, childAccessor, segment);
                if (child is null)
                {
                    return SubjectPathValue<TValue>.Unresolved;
                }

                resolvedSubjects[i + 1] = child;
                current = child;
            }

            // A well-formed path always ends in a leaf segment and returns above; reaching here means the
            // segments carried no leaf, which the decomposer rejects, so treat it as unresolved.
            return SubjectPathValue<TValue>.Unresolved;
        }
        catch
        {
            return SubjectPathValue<TValue>.Unresolved;
        }
    }

    /// <summary>
    /// Builds the leaf accessor for one (subject type, metadata) binding; shared by the walk's by-name
    /// slow path and the <see cref="ResolvedPathSegment{TValue}"/> cache so the resolution logic exists
    /// once. When the declared property type is assignable to <typeparamref name="TValue"/> the compiled
    /// typed accessor is returned (<paramref name="isTyped"/> true); otherwise (a dynamic property with
    /// no PropertyInfo, or a type mismatch) the boxed metadata getter drives the fallback read.
    /// </summary>
    internal static Delegate? BuildLeafAccessor<TValue>(
        Type subjectType, SubjectPropertyMetadata metadata, out bool isTyped)
    {
        var propertyInfo = metadata.PropertyInfo;
        if (propertyInfo is not null && typeof(TValue).IsAssignableFrom(propertyInfo.PropertyType))
        {
            isTyped = true;
            return PathValueAccessors.GetLeafAccessor<TValue>(subjectType, propertyInfo);
        }

        isTyped = false;
        return metadata.GetValue;
    }

    /// <summary>
    /// Reads the leaf through a previously built accessor. The typed path reads a value-typed leaf
    /// without boxing and delivers a resolved-null for a null reference leaf; the fallback path accepts
    /// the boxed value only when it is actually a <typeparamref name="TValue"/>, so a type mismatch (or
    /// a null, which cannot satisfy the type test) is unresolved. <paramref name="isTyped"/> is needed
    /// because a fallback getter is itself a <c>Func&lt;IInterceptorSubject, object?&gt;</c> and would
    /// satisfy the typed pattern when <typeparamref name="TValue"/> is object.
    /// </summary>
    internal static SubjectPathValue<TValue> ReadLeaf<TValue>(
        IInterceptorSubject current, Delegate? accessor, bool isTyped)
    {
        if (isTyped && accessor is Func<IInterceptorSubject, TValue> typedAccessor)
        {
            return SubjectPathValue<TValue>.Resolved(typedAccessor(current));
        }

        var getValue = accessor as Func<IInterceptorSubject, object?>;
        return getValue?.Invoke(current) is TValue typedValue
            ? SubjectPathValue<TValue>.Resolved(typedValue)
            : SubjectPathValue<TValue>.Unresolved;
    }

    /// <summary>
    /// Builds the child accessor for one (subject type, metadata, segment) binding; the single place
    /// that switches on the segment kind for accessor construction, shared by the walk's by-name slow
    /// path and the <see cref="ResolvedPathSegment{TValue}"/> cache. A Property intermediate stays on
    /// the boxed metadata getter so every walk re-reads the live child (the next position's reference
    /// compare is the divergence signal); a collection without a compiled indexer also stays on the
    /// getter for the lenient <see cref="PathValueAccessors.IndexReferenceCollection"/> path.
    /// </summary>
    internal static Delegate? BuildChildAccessor(Type subjectType, SubjectPropertyMetadata metadata, PathSegment segment)
    {
        return segment.Kind switch
        {
            PathSegmentKind.Property => metadata.GetValue,
            PathSegmentKind.CollectionIndex when segment.IsValueTypedCollection
                => PathValueAccessors.GetImmutableArrayIndexer(
                    subjectType, metadata.PropertyInfo!, segment.CollectionElementType!),
            PathSegmentKind.CollectionIndex
                when metadata.PropertyInfo is not null && segment.CollectionInterfaceType is not null
                => PathValueAccessors.GetGenericListIndexer(
                    subjectType, metadata.PropertyInfo, segment.CollectionInterfaceType, segment.CollectionElementType!),
            PathSegmentKind.CollectionIndex => metadata.GetValue,
            PathSegmentKind.DictionaryKey => PathValueAccessors.GetDictionaryLookup(
                subjectType, metadata.PropertyInfo!, segment),
            _ => null
        };
    }

    /// <summary>
    /// Resolves the next subject through a previously built child accessor; the single place that
    /// switches on the segment kind for accessor invocation. Callers wrap the call in their own
    /// never-throw boundary (the walk's outer catch, or the chain build's local try/catch where a throw
    /// means "child unresolved, stop building the suffix").
    /// </summary>
    internal static IInterceptorSubject? ResolveChild(IInterceptorSubject current, Delegate? accessor, PathSegment segment)
    {
        switch (segment.Kind)
        {
            case PathSegmentKind.Property:
            {
                return (accessor as Func<IInterceptorSubject, object?>)?.Invoke(current) as IInterceptorSubject;
            }

            case PathSegmentKind.CollectionIndex when accessor is Func<IInterceptorSubject, int, IInterceptorSubject?> indexer:
            {
                return indexer(current, segment.CollectionIndex);
            }

            case PathSegmentKind.CollectionIndex:
            {
                var collection = (accessor as Func<IInterceptorSubject, object?>)?.Invoke(current);
                return collection is null ? null : PathValueAccessors.IndexReferenceCollection(collection, segment.CollectionIndex);
            }

            case PathSegmentKind.DictionaryKey:
            {
                return (accessor as Func<IInterceptorSubject, object, IInterceptorSubject?>)?.Invoke(current, segment.DictionaryKey!);
            }

            default:
            {
                return null;
            }
        }
    }
}
