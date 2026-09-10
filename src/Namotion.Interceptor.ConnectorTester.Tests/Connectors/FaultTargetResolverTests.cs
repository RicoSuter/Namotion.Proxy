using Xunit;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Connectors;

public class FaultTargetResolverTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    private sealed class FakeConnector : ISubjectConnector, IFaultInjectable
    {
        public required IInterceptorSubject RootSubject { get; init; }
        public ConnectorDiagnostics Diagnostics { get; } = new(new ConnectorMetrics());
        public Task InjectFaultAsync(FaultType faultType, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void WhenConnectorMatchesParticipantRoot_ThenResolveReturnsIt()
    {
        // Arrange
        var rootA = new TestNode(CreateContext());
        var rootB = new TestNode(CreateContext());
        var participants = new Dictionary<string, TestNode> { ["server"] = rootA, ["client"] = rootB };
        var connectors = new ISubjectConnector[]
        {
            new FakeConnector { RootSubject = rootA },
            new FakeConnector { RootSubject = rootB }
        };
        var resolver = new FaultTargetResolver();
        resolver.Bind(participants, connectors);

        // Act
        var serverTarget = resolver.Resolve("server");
        var clientTarget = resolver.Resolve("client");

        // Assert
        Assert.NotNull(serverTarget);
        Assert.NotNull(clientTarget);
        Assert.NotSame(serverTarget, clientTarget);
    }

    [Fact]
    public void WhenNoMatchingConnector_ThenResolveReturnsNull()
    {
        // Arrange
        var root = new TestNode(CreateContext());
        var participants = new Dictionary<string, TestNode> { ["server"] = root };
        var resolver = new FaultTargetResolver();
        resolver.Bind(participants, connectors: []);

        // Act
        var target = resolver.Resolve("server");

        // Assert
        Assert.Null(target);
    }
}
