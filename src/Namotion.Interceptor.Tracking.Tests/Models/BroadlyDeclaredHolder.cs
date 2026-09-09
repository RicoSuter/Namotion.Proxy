using System.Collections;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Holds a structural value behind a declaration too broad to say whether it is keyed or ordinal,
/// so the structural scan has to dispatch from the value instead.
/// </summary>
[InterceptorSubject]
public partial class BroadlyDeclaredHolder
{
    public partial object? BroadCars { get; set; }

    public partial IReadOnlyDictionary<string, Car>? PreciseCars { get; set; }
}

/// <summary>
/// A dictionary that enumerates as its values rather than as key value pairs, so the keyed dispatch
/// arm has to cope with items that are subjects instead of pairs.
/// </summary>
public sealed class ValueEnumeratingCarDictionary(Dictionary<string, Car> cars)
    : IReadOnlyDictionary<string, Car>, IEnumerable<Car>
{
    public Car this[string key] => cars[key];

    public IEnumerable<string> Keys => cars.Keys;

    public IEnumerable<Car> Values => cars.Values;

    public int Count => cars.Count;

    public bool ContainsKey(string key) => cars.ContainsKey(key);

    public bool TryGetValue(string key, out Car value) => cars.TryGetValue(key, out value!);

    public IEnumerator<Car> GetEnumerator() => cars.Values.GetEnumerator();

    IEnumerator<KeyValuePair<string, Car>> IEnumerable<KeyValuePair<string, Car>>.GetEnumerator() => cars.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A sequence of pairs that is not a dictionary: it yields KeyValuePair items but implements no
/// dictionary interface, so classification has to keep it ordinal.
/// </summary>
public sealed class CarPairSequence(params KeyValuePair<string, Car>[] pairs) : IEnumerable<KeyValuePair<string, Car>>
{
    public IEnumerator<KeyValuePair<string, Car>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, Car>>)pairs).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
