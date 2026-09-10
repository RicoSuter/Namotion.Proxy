using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;
using Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;
using Namotion.Interceptor.ConnectorTester.Connectors.WebSocket;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Connectors;

public class ConnectorFactoryRegistrationTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    [Theory]
    [MemberData(nameof(AllFactories))]
    public void WhenRegisterServerCalled_ThenAtLeastOneSubjectConnectorRegistered(IConnectorFactory connectorFactory)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var root = new TestNode(CreateContext());

        // Act
        connectorFactory.RegisterServer(services, root, connectorFactory.DefaultPort);
        var provider = services.BuildServiceProvider();
        var connectors = provider.GetServices<IHostedService>().OfType<ISubjectConnector>().ToList();

        // Assert
        Assert.NotEmpty(connectors);
    }

    [Theory]
    [MemberData(nameof(AllFactories))]
    public void WhenRegisterClientCalled_ThenAtLeastOneSubjectConnectorRegistered(IConnectorFactory connectorFactory)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var root = new TestNode(CreateContext());
        var participantConfiguration = new ParticipantConfiguration { Name = "client-test" };

        // Act
        connectorFactory.RegisterClient(services, root, participantConfiguration, connectorFactory.DefaultPort);
        var provider = services.BuildServiceProvider();
        var connectors = provider.GetServices<IHostedService>().OfType<ISubjectConnector>().ToList();

        // Assert
        Assert.NotEmpty(connectors);
    }

    public static IEnumerable<object[]> AllFactories()
    {
        yield return [new OpcUaConnectorFactory()];
        yield return [new MqttConnectorFactory()];
        yield return [new WebSocketConnectorFactory()];
    }
}
