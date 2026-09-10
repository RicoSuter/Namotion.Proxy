using System;

namespace Namotion.Interceptor.Tracking.Paths;

internal enum PathSegmentKind
{
    Property,
    CollectionIndex,
    DictionaryKey
}

/// <summary>
/// One node of a decomposed path: a single subscribed property name plus an optional fixed
/// collection index or dictionary key evaluated once at subscribe time. Resolution is by name
/// against the runtime subject; the static-type fields drive accessor construction and validation only.
/// </summary>
internal sealed class PathSegment
{
    public required string PropertyName { get; init; }
    public required PathSegmentKind Kind { get; init; }
    public required Type PropertyStaticType { get; init; }
    public bool IsLeaf { get; init; }

    // Valid when Kind == CollectionIndex.
    public int CollectionIndex { get; init; }
    // True only when the collection segment's static type is ImmutableArray<T> (a value type that
    // would box through the object-returning metadata getter).
    public bool IsValueTypedCollection { get; init; }
    // The generic list interface used to invoke Count and the indexer directly. Null for the
    // value-typed ImmutableArray<T> path (dedicated accessor) and for containers without an
    // IList<>/IReadOnlyList<> interface, which the walk indexes leniently via IndexReferenceCollection.
    public Type? CollectionInterfaceType { get; init; }
    // The collection element type, resolved once at decompose time so the walk does not recompute it
    // per call (GetGenericArguments allocates a Type[]). Null when CollectionInterfaceType is null
    // and the segment is not value-typed.
    public Type? CollectionElementType { get; init; }

    // Valid when Kind == DictionaryKey.
    public object? DictionaryKey { get; init; }
    public Type? DictionaryInterfaceType { get; init; }
    public Type? DictionaryKeyType { get; init; }
    public Type? DictionaryValueType { get; init; }
}
