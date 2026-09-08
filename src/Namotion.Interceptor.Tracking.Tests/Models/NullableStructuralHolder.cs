using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Model with structural properties typed <c>Nullable&lt;T&gt;</c>, where <c>T</c> is a struct that
/// carries subjects. The nullable form distinguishes "not set" from "empty", which a bare
/// <see cref="ImmutableArray{T}"/> cannot express.
/// </summary>
[InterceptorSubject]
public partial class NullableStructuralHolder
{
    /// <summary>Boxes to an <see cref="ICollection"/>, the collection dispatch arm.</summary>
    public partial ImmutableArray<Car>? ImmutableCars { get; set; }

    /// <summary>Boxes to a bare <see cref="IEnumerable"/>, the read-only collection dispatch arm.</summary>
    public partial CarBag? BagCars { get; set; }

    /// <summary>
    /// Boxes to a bare <see cref="IEnumerable"/> of key value pairs, so the dispatch arm is chosen
    /// from the declared property type rather than from the value.
    /// </summary>
    public partial CarMap? MappedCars { get; set; }
}

/// <summary>Read-only struct collection that implements neither <see cref="ICollection"/> nor <see cref="IDictionary"/>.</summary>
public readonly struct CarBag(params Car[] cars) : IEnumerable<Car>
{
    private readonly Car[] _cars = cars;

    public IEnumerator<Car> GetEnumerator() => ((IEnumerable<Car>)(_cars ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Read-only struct dictionary that implements neither <see cref="ICollection"/> nor <see cref="IDictionary"/>.</summary>
public readonly struct CarMap(Dictionary<string, Car> cars) : IReadOnlyDictionary<string, Car>
{
    private readonly Dictionary<string, Car> _cars = cars;

    private Dictionary<string, Car> Cars => _cars ?? [];

    public Car this[string key] => Cars[key];

    public IEnumerable<string> Keys => Cars.Keys;

    public IEnumerable<Car> Values => Cars.Values;

    public int Count => Cars.Count;

    public bool ContainsKey(string key) => Cars.ContainsKey(key);

    public bool TryGetValue(string key, out Car value) => Cars.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, Car>> GetEnumerator() => Cars.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
