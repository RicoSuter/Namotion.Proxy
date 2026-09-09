using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Covers structural values whose declared property type is too broad to say whether they are
/// keyed or ordinal, so the shape has to come from the value itself, plus the precisely declared
/// dictionary that enumerates as its values and therefore also depends on a total keyed arm.
/// </summary>
public class BroadlyDeclaredStructuralPropertyTests
{
    private static (BroadlyDeclaredHolder Holder, TestLifecycleHandler Handler) CreateHolder()
    {
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithParents()
            .WithService(() => handler)
            .WithContextInheritance();

        return (new BroadlyDeclaredHolder(context), handler);
    }

    [Fact]
    public void WhenAReadOnlyDictionaryIsHeldBehindABroadProperty_ThenItsChildrenAreAttachedUnderTheirKey()
    {
        // Arrange
        var (holder, handler) = CreateHolder();
        var car = new Car { Name = "Car1" };

        // Act
        holder.BroadCars = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car> { ["first"] = car });

        // Assert
        Assert.Contains(handler.GetEvents(), e => e.Contains("BroadCars[first] -> Car1") && e.Contains("attached"));
        Assert.Equal("first", Assert.Single(car.GetParents()).Index);
    }

    [Fact]
    public void WhenABroadPropertyHoldingAReadOnlyDictionaryIsCleared_ThenItsChildrenAreDetached()
    {
        // Arrange
        var (holder, handler) = CreateHolder();
        var car = new Car { Name = "Car1" };
        holder.BroadCars = new ReadOnlyDictionaryWrapper<string, Car>(new Dictionary<string, Car> { ["first"] = car });
        handler.Clear();

        // Act
        holder.BroadCars = null;

        // Assert
        Assert.Contains(handler.GetEvents(), e => e.Contains("BroadCars[first] -> Car1") && e.Contains("detached"));
        Assert.Empty(car.GetParents());
        Assert.Empty(holder
            .TryGetRegisteredSubject()!
            .TryGetProperty(nameof(BroadlyDeclaredHolder.BroadCars))!
            .Children);
    }

    [Fact]
    public void WhenAReadOnlyCollectionIsHeldBehindABroadProperty_ThenItsChildrenAreAttachedByPosition()
    {
        // Arrange: the value carries no key value pairs, so the same broad declaration must keep
        // producing positional indices.
        var (holder, handler) = CreateHolder();
        var car = new Car { Name = "Car1" };

        // Act
        holder.BroadCars = new CarBag(car);

        // Assert
        Assert.Contains(handler.GetEvents(), e => e.Contains("BroadCars[0] -> Car1") && e.Contains("attached"));
        Assert.Equal(0, Assert.Single(car.GetParents()).Index);
    }

    [Fact]
    public void WhenADictionaryEnumeratesAsValuesInsteadOfPairs_ThenItsChildrenAreStillAttached()
    {
        // Arrange: classifying the value as keyed is only half the answer, the keyed arm also has
        // to handle a dictionary type whose enumerator yields values rather than pairs.
        var (holder, handler) = CreateHolder();
        var car = new Car { Name = "Car1" };

        // Act
        holder.BroadCars = new ValueEnumeratingCarDictionary(new Dictionary<string, Car> { ["first"] = car });

        // Assert
        Assert.Contains(handler.GetEvents(), e => e.Contains("BroadCars[0] -> Car1") && e.Contains("attached"));
        Assert.Equal(0, Assert.Single(car.GetParents()).Index);
    }

    [Fact]
    public void WhenAPreciselyDeclaredDictionaryEnumeratesAsValuesInsteadOfPairs_ThenItsChildrenAreAttachedByPosition()
    {
        // Arrange: the declaration already says keyed, so only the totality of the keyed arm keeps
        // this child from being dropped, and it can only place it by position because the
        // enumerator yields values that carry no key.
        var (holder, handler) = CreateHolder();
        var car = new Car { Name = "Car1" };

        // Act
        holder.PreciseCars = new ValueEnumeratingCarDictionary(new Dictionary<string, Car> { ["first"] = car });

        // Assert
        Assert.Contains(handler.GetEvents(), e => e.Contains("PreciseCars[0] -> Car1") && e.Contains("attached"));
        Assert.Equal(0, Assert.Single(car.GetParents()).Index);

        var property = holder
            .TryGetRegisteredSubject()!
            .TryGetProperty(nameof(BroadlyDeclaredHolder.PreciseCars))!;

        Assert.Equal(0, Assert.Single(property.Children).Index);

        // The declared type still reports keyed, so key based path resolution cannot reach a child
        // recorded at a position. Attaching it positionally is what the totality arm buys.
        Assert.True(property.IsSubjectDictionary);
    }

    [Fact]
    public void WhenAnEnumerableOfPairsIsNotADictionary_ThenItsPairsAreNotUnwrapped()
    {
        // Arrange: the keyed arm is chosen by the value's own type, never by the shape of the items
        // it happens to yield. Keeping a non-dictionary sequence ordinal is what makes the
        // classifier and this handler answer the same question.
        var (holder, handler) = CreateHolder();
        var car = new Car { Name = "Car1" };

        // Act
        holder.BroadCars = new CarPairSequence(new KeyValuePair<string, Car>("first", car));

        // Assert
        Assert.DoesNotContain(handler.GetEvents(), e => e.Contains("Car1") && e.Contains("attached"));
        Assert.Empty(car.GetParents());
    }
}
