using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HomeBlaze.OpcUa.Tests;

public class OpcUaWrapperConstructionTests
{
    [Fact]
    public async Task WhenAWrapperIsBuiltByTheApplicationsOwnFactory_ThenItsOptionalArgumentsAreFilledIn()
    {
        // Arrange
        await using var testHost = await OpcUaTestHost.StartAsync();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(testHost.RootManager)
            .AddSingleton(testHost.PathResolver)
            .BuildServiceProvider();

        var factory = new SubjectFactory(services);

        // Act
        var client = factory.CreateSubject<OpcUaClient>();
        var server = factory.CreateSubject<OpcUaServer>();

        // Assert
        // Both wrappers take the diagnostics poll interval as a constructor argument the application
        // never passes, and ActivatorUtilities fills a parameter it can find no service for only when
        // that parameter has a default. Reaching a constructed wrapper at all is what this pins.
        Assert.Equal(ServiceStatus.Stopped, client.Status);
        Assert.Equal(ServiceStatus.Stopped, server.Status);
    }
}
