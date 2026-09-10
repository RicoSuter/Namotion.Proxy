using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A dictionary entry is an occurrence like a collection slot is, so the same subject under two keys
/// is two edges. A key is a stable identity, so a reorder cannot invalidate one, but the key is the
/// occurrence's address rather than its identity: retention is decided by subject, and a rename
/// moves an occurrence instead of replacing it.
/// </summary>
public class DictionaryOccurrenceTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenOneSubjectIsStoredUnderTwoKeys_ThenItHasTwoOccurrences()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car, ["y"] = car };

        // Assert
        Assert.Equal(2, car.GetReferenceCount());
        var parents = ((IInterceptorSubject)car).GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, parent => Equals(parent.Index, "x"));
        Assert.Contains(parents, parent => Equals(parent.Index, "y"));
    }

    [Fact]
    public void WhenOneOfTwoKeysIsDropped_ThenTheSubjectKeepsTheSurvivingOccurrence()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car, ["y"] = car };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["y"] = car };

        // Assert
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        var parents = ((IInterceptorSubject)car).GetParents();
        Assert.Single(parents);
        Assert.Equal("y", parents[0].Index);
    }

    [Fact]
    public void WhenBothKeysAreDropped_ThenTheSubjectDetaches()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car, ["y"] = car };

        // Act
        garage.CarsByName = new Dictionary<string, Car>();

        // Assert
        Assert.Equal(0, car.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)car).TryGetContext());
        Assert.Empty(((IInterceptorSubject)car).GetParents());
    }

    [Fact]
    public void WhenTwoSubjectsSwapKeys_ThenBothOccurrencesAreRewritten()
    {
        // Arrange
        var context = CreateContext();
        var garage = new Garage(context) { Name = "G" };
        var first = new Car { Name = "A" };
        var second = new Car { Name = "B" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = first, ["y"] = second };

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = second, ["y"] = first };

        // Assert: keys identify the occurrences, so each subject moved to the other key rather than
        // keeping a stale one.
        Assert.Equal("y", ((IInterceptorSubject)first).GetParents()[0].Index);
        Assert.Equal("x", ((IInterceptorSubject)second).GetParents()[0].Index);
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
    }

    /// <summary>
    /// The rename is the shape that forces the retention rule: reconciliation runs its whole removal
    /// pass before its addition pass, so a subject matched by key would lose its only support and
    /// become claimable by another context in the gap, for a write that was about to re-commit it.
    /// Matching by subject instead keeps the edge and moves its key afterwards.
    /// </summary>
    [Fact]
    public void WhenTheOnlyKeyOfASubjectIsRenamed_ThenTheOccurrenceMovesWithoutLeavingTheGraph()
    {
        // Arrange
        var context = CreateContext();
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var garage = new Garage(context) { Name = "G" };
        var car = new Car { Name = "A" };
        garage.CarsByName = new Dictionary<string, Car> { ["x"] = car };

        var attached = new List<IInterceptorSubject>();
        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);
        lifecycleInterceptor.SubjectDetaching += change => detached.Add(change.Subject);

        // Act
        garage.CarsByName = new Dictionary<string, Car> { ["y"] = car };

        // Assert: the subject the new value still holds never leaves the graph, so nothing below it
        // is torn down and rebuilt either, and a Registry projection sees the key move rather than an
        // eviction followed by a fresh registration of the whole subtree. Master behaves the same way
        // on this shape, except that it leaves the published index on the old key.
        Assert.Empty(detached);
        Assert.Empty(attached);
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        Assert.Equal("y", ((IInterceptorSubject)car).GetParents()[0].Index);
    }

    [Fact]
    public void WhenADictionaryIsAssignedToAnObjectProperty_ThenItsValuesAreKeyedOccurrences()
    {
        // Arrange
        var context = CreateContext();
        var holder = new BroadPropertyHolder(context);
        var car = new Car { Name = "A" };

        // Act
        holder.Payload = new ReadOnlyCarDictionary(new Dictionary<string, Car> { ["x"] = car });

        // Assert
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Equal("x", ((IInterceptorSubject)car).GetParents()[0].Index);
    }

    [Fact]
    public void WhenADictionaryOnAnObjectPropertyDropsOneOfTwoKeys_ThenTheSurvivingOccurrenceIsKept()
    {
        // Arrange
        var context = CreateContext();
        var holder = new BroadPropertyHolder(context);
        var car = new Car { Name = "A" };
        holder.Payload = new ReadOnlyCarDictionary(new Dictionary<string, Car> { ["x"] = car, ["y"] = car });

        // Act
        holder.Payload = new ReadOnlyCarDictionary(new Dictionary<string, Car> { ["y"] = car });

        // Assert
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Equal("y", ((IInterceptorSubject)car).GetParents()[0].Index);
    }

    [Fact]
    public void WhenADictionaryOnAnObjectPropertyIsReplacedByAList_ThenTheSubjectMovesToItsOrdinal()
    {
        // Arrange
        var context = CreateContext();
        var holder = new BroadPropertyHolder(context);
        var car = new Car { Name = "A" };
        holder.Payload = new ReadOnlyCarDictionary(new Dictionary<string, Car> { ["x"] = car });

        // Act: the same property changes shape, so no occurrence identity survives.
        holder.Payload = new List<Car> { car };

        // Assert
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        Assert.Equal(0, ((IInterceptorSubject)car).GetParents()[0].Index);
    }

    [Fact]
    public void WhenAListOnAnObjectPropertyIsReplacedByADictionary_ThenTheSubjectMovesToItsKey()
    {
        // Arrange
        var context = CreateContext();
        var holder = new BroadPropertyHolder(context);
        var car = new Car { Name = "A" };
        holder.Payload = new List<Car> { car };

        // Act
        holder.Payload = new ReadOnlyCarDictionary(new Dictionary<string, Car> { ["x"] = car });

        // Assert
        Assert.Equal(1, car.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)car).TryGetContext());
        Assert.Equal("x", ((IInterceptorSubject)car).GetParents()[0].Index);
    }

    [Fact]
    public void WhenADictionaryOnAnObjectPropertyIsReplacedByAnArray_ThenTheOldSubjectDetaches()
    {
        // Arrange
        var context = CreateContext();
        var holder = new BroadPropertyHolder(context);
        var first = new Car { Name = "A" };
        var second = new Car { Name = "B" };
        holder.Payload = new ReadOnlyCarDictionary(new Dictionary<string, Car> { ["x"] = first });

        // Act
        holder.Payload = new[] { second };

        // Assert
        Assert.Equal(0, first.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)first).TryGetContext());
        Assert.Equal(1, second.GetReferenceCount());
        Assert.Equal(0, ((IInterceptorSubject)second).GetParents()[0].Index);
    }
}

[InterceptorSubject]
public partial class BroadPropertyHolder
{
    public partial object? Payload { get; set; }
}

/// <summary>
/// A dictionary of subjects that implements neither <see cref="IDictionary"/> nor <see cref="ICollection"/>,
/// so only its own runtime type reveals that its entries are keyed.
/// </summary>
public sealed class ReadOnlyCarDictionary(Dictionary<string, Car> entries) : IReadOnlyDictionary<string, Car>
{
    public IEnumerator<KeyValuePair<string, Car>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Count => entries.Count;

    public bool ContainsKey(string key) => entries.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out Car value) => entries.TryGetValue(key, out value);

    public Car this[string key] => entries[key];

    public IEnumerable<string> Keys => entries.Keys;

    public IEnumerable<Car> Values => entries.Values;
}
