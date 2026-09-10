using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>One observed state of a watched path: either resolved (every segment reachable, the leaf property exists) with a value that may itself be null, or unresolved.</summary>
public readonly struct SubjectPathValue<TValue>
{
    private readonly TValue? _value;

    private SubjectPathValue(bool isResolved, TValue? value)
    {
        IsResolved = isResolved;
        _value = value;
    }

    /// <summary>Every segment reachable and indices in range, so the leaf property exists; its value may still be null.</summary>
    public bool IsResolved { get; }

    /// <summary>The leaf's value; <c>default</c> when not resolved.</summary>
    public TValue? Value => _value;

    /// <summary>The unresolved state (also <c>default</c>).</summary>
    public static SubjectPathValue<TValue> Unresolved => default;

    /// <summary>A resolved state carrying the leaf value (which may be null).</summary>
    public static SubjectPathValue<TValue> Resolved(TValue? value) => new(true, value);

    /// <summary>False when unresolved; true with a possibly-null value for a resolved leaf.</summary>
    public bool TryGetValue([MaybeNull] out TValue value)
    {
        value = _value;
        return IsResolved;
    }

    /// <summary><c>default(TValue)</c> when unresolved; the leaf value (may be null) when resolved.</summary>
    public TValue? GetValueOrDefault() => IsResolved ? _value : default;

    /// <summary><paramref name="fallback"/> when unresolved OR the resolved value is null; the resolved non-null value otherwise.</summary>
    public TValue GetValueOrDefault(TValue fallback) => IsResolved && _value is not null ? _value : fallback;

    /// <summary>Suppression equivalence: same resolvedness and, when both resolved, values equal under <see cref="EqualityComparer{TValue}.Default"/>.</summary>
    internal static bool AreEquivalent(in SubjectPathValue<TValue> a, in SubjectPathValue<TValue> b)
    {
        if (a.IsResolved != b.IsResolved)
        {
            return false;
        }

        return !a.IsResolved || EqualityComparer<TValue>.Default.Equals(a._value, b._value);
    }
}
