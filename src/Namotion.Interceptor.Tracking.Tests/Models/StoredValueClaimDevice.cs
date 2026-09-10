using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A subject with no properties that counts how often the write protocol reaches for its executor.
/// Every pass over a value asks each subject it contains for its attachment, so the count is
/// proportional to the number of passes.
/// </summary>
internal sealed class ExecutorCountingSubject : IInterceptorSubject
{
    private IInterceptorExecutor? _executor;

    public int ExecutorAccessCount;

    public IInterceptorExecutor Executor
    {
        get
        {
            ExecutorAccessCount++;
            return InterceptorExecutor.GetOrCreate(ref _executor, this);
        }
    }

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } =
        FrozenDictionary<string, SubjectPropertyMetadata>.Empty;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");
}

/// <summary>
/// A value-typed collection whose equality compares a version stamp and not its contents, which is
/// what a hand-rolled immutable wrapper usually does. Two values can be equal and hold entirely
/// different subjects, so this equality must never be allowed to decide whether the stored value
/// still needs claiming.
/// </summary>
internal readonly struct StampedChildren(int stamp, List<Person>? items) : IEnumerable<Person>, IEquatable<StampedChildren>
{
    private readonly int _stamp = stamp;
    private readonly List<Person>? _items = items;

    public bool Equals(StampedChildren other) => _stamp == other._stamp;

    public override bool Equals(object? obj) => obj is StampedChildren other && Equals(other);

    public override int GetHashCode() => _stamp;

    public IEnumerator<Person> GetEnumerator() => (_items ?? []).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Carries the shapes the stored-value claim has to tell apart: an immutable array, whose equality
/// is a comparison of the underlying array reference, the same shape behind a reference-typed
/// declaration, and a value type whose equality answers about a stamp instead of about storage.
/// </summary>
internal sealed class StoredValueClaimDevice : IInterceptorSubject
{
    private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(ImmutableChildren)] = new(
                nameof(ImmutableChildren),
                typeof(ImmutableArray<ExecutorCountingSubject>),
                [],
                static subject => ((StoredValueClaimDevice)subject)._immutableChildren,
                static (subject, value) => ((StoredValueClaimDevice)subject).ImmutableChildren = (ImmutableArray<ExecutorCountingSubject>)value!,
                isIntercepted: true,
                isDynamic: false),
            [nameof(ListChildren)] = new(
                nameof(ListChildren),
                typeof(IReadOnlyList<ExecutorCountingSubject>),
                [],
                static subject => ((StoredValueClaimDevice)subject)._listChildren,
                static (subject, value) => ((StoredValueClaimDevice)subject).ListChildren = (IReadOnlyList<ExecutorCountingSubject>?)value,
                isIntercepted: true,
                isDynamic: false),
            [nameof(Stamped)] = new(
                nameof(Stamped),
                typeof(StampedChildren),
                [],
                static subject => ((StoredValueClaimDevice)subject)._stamped,
                static (subject, value) => ((StoredValueClaimDevice)subject).Stamped = (StampedChildren)value!,
                isIntercepted: true,
                isDynamic: false)
        }.ToFrozenDictionary();

    private IInterceptorExecutor? _executor;
    private ImmutableArray<ExecutorCountingSubject> _immutableChildren = [];
    private IReadOnlyList<ExecutorCountingSubject>? _listChildren;
    private StampedChildren _stamped;

    /// <summary>Armed before a write to make the terminal store a value the caller never proposed.</summary>
    public StampedChildren? StampedSubstitute = null;

    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
        throw new NotSupportedException("The hand-written subject declares all its properties statically.");

    public ImmutableArray<ExecutorCountingSubject> ImmutableChildren
    {
        get => Executor.GetPropertyValue(nameof(ImmutableChildren), static subject => ((StoredValueClaimDevice)subject)._immutableChildren);
        set => Executor.SetPropertyValue(nameof(ImmutableChildren), value, _immutableChildren,
            static (subject, newValue) => ((StoredValueClaimDevice)subject)._immutableChildren = newValue);
    }

    public IReadOnlyList<ExecutorCountingSubject>? ListChildren
    {
        get => Executor.GetPropertyValue(nameof(ListChildren), static subject => ((StoredValueClaimDevice)subject)._listChildren);
        set => Executor.SetPropertyValue(nameof(ListChildren), value, _listChildren,
            static (subject, newValue) => ((StoredValueClaimDevice)subject)._listChildren = newValue);
    }

    public StampedChildren Stamped
    {
        get => Executor.GetPropertyValue(nameof(Stamped), static subject => ((StoredValueClaimDevice)subject)._stamped);
        set => Executor.SetPropertyValue(nameof(Stamped), value, _stamped,
            static (subject, newValue) =>
            {
                var device = (StoredValueClaimDevice)subject;
                device._stamped = device.StampedSubstitute ?? newValue;
            });
    }
}
