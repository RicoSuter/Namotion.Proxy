using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mqtt.Tests;

/// <summary>
/// The server relays a client's message rather than letting the broker distribute it, so an applied
/// value has reached every subscriber by the time the subject sees it and must supersede an older local
/// commit. Choosing the other rule is silent: the broker keeps serving a value the model has moved past,
/// and no transport test notices. Pins the wiring; the rule's behaviour is covered by
/// <c>ChangeQueueProcessorTests</c> in the connectors suite.
/// </summary>
public class MqttServerDeliveryRuleTests
{
    [Fact]
    public async Task WhenTheServerCreatesItsProcessor_ThenItSelectsTheServerRule()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var subject = new DeliveryRuleTestRoot(context);
        await using var server = new MqttSubjectServer(subject, new MqttServerConfiguration(), NullLogger<MqttSubjectServer>.Instance);

        // Act
        using var processor = server.CreateChangeQueueProcessor();

        // Assert
        Assert.Equal(ChangeDeliveryRule.SourceValuesAreSettled, processor.DeliveryRule);
    }

}

[InterceptorSubject]
public partial class DeliveryRuleTestRoot
{
    public partial string? Name { get; set; }
}
