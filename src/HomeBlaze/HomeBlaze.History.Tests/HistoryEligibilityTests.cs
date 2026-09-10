using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Tests;

public class HistoryEligibilityTests
{
    [Fact]
    public void WhenPropertyIsRecordableScalarState_ThenHasHistoryIsTrue()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.Temperature));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenPropertyIsNotState_ThenHasHistoryIsFalse()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.NotState));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenStatePropertyCanContainSubjects_ThenHasHistoryIsFalse()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.Child));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void WhenStatePropertyTypeIsNotRecordable_ThenHasHistoryIsFalse()
    {
        // Arrange
        var property = GetRegisteredProperty(nameof(EligibilityTestSubject.Marker));

        // Act
        var result = property.HasHistory();

        // Assert
        Assert.False(result);
    }

    private static RegisteredSubjectProperty GetRegisteredProperty(string propertyName)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var subject = new EligibilityTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        return registered.TryGetProperty(propertyName)!;
    }
}

[InterceptorSubject]
public partial class EligibilityTestSubject
{
    [State]
    public partial double Temperature { get; set; }

    public partial string? NotState { get; set; }

    [State]
    public partial EligibilityTestSubject? Child { get; set; }

    [State]
    public partial Guid Marker { get; set; }
}
