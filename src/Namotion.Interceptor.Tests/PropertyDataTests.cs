namespace Namotion.Interceptor.Tests;

public class PropertyDataTests
{
    [Fact]
    public void WhenKeyIsAbsent_ThenTryAddPropertyDataStoresValueAndReturnsTrue()
    {
        // Arrange
        var car = new Car();
        var property = new PropertyReference(car, nameof(Car.Speed));

        // Act
        var added = property.TryAddPropertyData("test.key", 1);

        // Assert
        Assert.True(added);
        Assert.True(property.TryGetPropertyData("test.key", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void WhenKeyIsPresent_ThenTryAddPropertyDataLeavesValueAndReturnsFalse()
    {
        // Arrange
        var car = new Car();
        var property = new PropertyReference(car, nameof(Car.Speed));
        property.TryAddPropertyData("test.key", 1);

        // Act
        var added = property.TryAddPropertyData("test.key", 2);

        // Assert
        Assert.False(added);
        Assert.True(property.TryGetPropertyData("test.key", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void WhenSubjectKeyIsAbsent_ThenTryAddDataStoresValueAndReturnsTrue()
    {
        // Arrange
        var car = new Car();

        // Act
        var added = car.TryAddData("test.key", 1);

        // Assert
        Assert.True(added);
        Assert.True(car.TryGetData("test.key", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void WhenSubjectKeyIsPresent_ThenTryAddDataLeavesValueAndReturnsFalse()
    {
        // Arrange
        // The add-if-absent semantics are what make this usable as a one-shot latch: it must report
        // true exactly once per subject and key, and never overwrite.
        var car = new Car();
        car.TryAddData("test.key", 1);

        // Act
        var added = car.TryAddData("test.key", 2);

        // Assert
        Assert.False(added);
        Assert.True(car.TryGetData("test.key", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void WhenSetDataOverwritesAnExistingKey_ThenTryAddDataStillReportsItPresent()
    {
        // Arrange
        // TryAddData and SetData share one slot, so a latch taken by one is visible to the other.
        var car = new Car();
        car.SetData("test.key", 1);

        // Act
        var added = car.TryAddData("test.key", 2);

        // Assert
        Assert.False(added);
        Assert.True(car.TryGetData("test.key", out var value));
        Assert.Equal(1, value);
    }
}
