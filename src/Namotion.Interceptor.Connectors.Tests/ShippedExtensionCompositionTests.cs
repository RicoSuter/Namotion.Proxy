using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Guards the promise that the shipped configuration extensions compose. Several of them reach the
/// same singleton contracts through more than one path, and a second registration of one throws, so
/// a consumer cannot establish this by reading their own configuration.
/// </summary>
public class ShippedExtensionCompositionTests
{
    [Fact]
    public void WhenEveryShippedExtensionIsCombined_ThenNoSingletonContractIsClaimedTwice()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithSourceMonitoring(services)
            .WithHostedServices(services);

        Assert.NotNull(context);
    }

    [Fact]
    public void WhenTheSameExtensionIsAppliedTwice_ThenTheSecondApplicationIsAbsorbed()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithRegistry()
            .WithSourceMonitoring(services)
            .WithSourceMonitoring(services);

        Assert.NotNull(context);
    }
}
