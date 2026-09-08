using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class CurrentValueTests
{
    public CurrentValueTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenNothingWrittenSinceTheChange_ThenGetCurrentValueEqualsGetNewValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) => captured = change);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal(captured.GetNewValue<string>(), captured.GetCurrentValue<string>());
        Assert.Equal("Rico", captured.GetCurrentValue<string>());
    }

    [Fact]
    public void WhenPropertyWrittenAgainAfterTheChange_ThenGetCurrentValueReflectsTheLaterWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                if (captured.Property.Subject is null) captured = change;
            });
        person.FirstName = "Rico";

        // Act
        person.FirstName = "Suter";

        // Assert
        Assert.Equal("Rico", captured.GetNewValue<string>());
        Assert.Equal("Suter", captured.GetCurrentValue<string>());
    }

    [Fact]
    public void WhenCurrentValueIsNullAndTValueIsNullable_ThenGetCurrentValueReturnsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                if (captured.Property.Subject is null) captured = change;
            });
        person.FirstName = "Rico";

        // Act
        person.FirstName = null;

        // Assert
        Assert.Null(captured.GetCurrentValue<string>());
    }

    [Fact]
    public void WhenCurrentValueIsNullAndTValueIsNonNullableValueType_ThenGetCurrentValueThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                if (captured.Property.Subject is null) captured = change;
            });
        person.FirstName = "Rico";
        person.FirstName = null;

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => captured.GetCurrentValue<int>());
    }

    [Fact]
    public void WhenCurrentValueTypeMismatchesTValue_ThenGetCurrentValueThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
            {
                if (captured.Property.Subject is null) captured = change;
            });
        person.FirstName = "Rico";

        // Act
        person.FirstName = "Suter";

        // Assert
        Assert.Throws<InvalidCastException>(() => captured.GetCurrentValue<int>());
    }
}
