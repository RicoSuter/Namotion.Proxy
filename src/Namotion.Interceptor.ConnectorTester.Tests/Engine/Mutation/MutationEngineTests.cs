using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class MutationEngineTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    [Fact]
    public async Task WhenStructuralMutationRateIsZero_ThenOnlyValueMutationsRun()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 100,
            StructuralMutationRate = 0
        };
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance);

        // Act
        await engine.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => engine.ValueMutationCount > 0,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(20));
        await engine.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(engine.ValueMutationCount > 0);
        Assert.Equal(0, engine.StructuralMutationCount);
    }

    [Fact]
    public void WhenResetCountersCalled_ThenBothCountersZero()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration { Name = "test", ValueMutationRate = 50 };
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance);

        // Act
        engine.ResetCounters();

        // Assert
        Assert.Equal(0, engine.ValueMutationCount);
        Assert.Equal(0, engine.StructuralMutationCount);
    }
}
