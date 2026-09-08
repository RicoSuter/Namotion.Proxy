using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Unit-level equivalent of the server echo-suppression path: an inbound update applied for a
/// connection publishes a change stamped <see cref="ChangeOrigin.FromSource"/> with that connection,
/// so the connection's own outbound queue skips it (dequeue-time <c>ReferenceEquals</c> against the
/// source) while every other connection's outbound queue delivers it. Exercises the applier and
/// <see cref="ChangeQueueProcessor"/> directly, without WebSocket transport.
/// </summary>
public class WebSocketEchoSuppressionTests
{
    [Fact]
    public async Task WhenInboundUpdateAppliedForConnection_ThenOwnOutboundSkipsButOthersReceive()
    {
        // Arrange - two client connections sharing one subject graph
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new TestRoot(context) { Name = "Initial" };

        var connection = new object();      // stands in for the originating WebSocketClientConnection
        var otherConnection = new object(); // another client's outbound path

        var ownWrites = new ConcurrentQueue<SubjectPropertyChange>();
        var otherWrites = new ConcurrentQueue<SubjectPropertyChange>();

        using var ownProcessor = new ChangeQueueProcessor(
            source: connection,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                    ownWrites.Enqueue(change);
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(20),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesAreSettled);

        using var otherProcessor = new ChangeQueueProcessor(
            source: otherConnection,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                    otherWrites.Enqueue(change);
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(20),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesAreSettled);

        using var cancellation = new CancellationTokenSource();
        var ownProcessing = ownProcessor.ProcessAsync(cancellation.Token);
        var otherProcessing = otherProcessor.ProcessAsync(cancellation.Token);

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Echoed"
                    }
                }
            }
        };

        // Act - apply the inbound update as if it arrived on `connection`, plus a local write
        subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(connection));
        subject.Number = 42m; // local change, no source

        await AsyncTestHelpers.WaitUntilAsync(
            () => otherWrites.Any(c => c.Property.Name == nameof(TestRoot.Name)) &&
                  ownWrites.Any(c => c.Property.Name == nameof(TestRoot.Number)),
            message: "The other connection should receive the inbound change and the own connection the local change");

        // Assert - the own connection skips its own-source change but still delivers the local change
        Assert.DoesNotContain(ownWrites, c => c.Property.Name == nameof(TestRoot.Name));
        Assert.Contains(ownWrites, c => c.Property.Name == nameof(TestRoot.Number));

        // The other connection receives the inbound change, and it carries FromSource(connection)
        var deliveredName = Assert.Single(otherWrites.Where(c => c.Property.Name == nameof(TestRoot.Name)).ToArray());
        Assert.Equal(ChangeOriginKind.FromSource, deliveredName.Origin.Kind);
        Assert.Same(connection, deliveredName.Origin.Source);

        // Cleanup
        await cancellation.CancelAsync();
        await ownProcessing;
        await otherProcessing;
    }

    /// <summary>
    /// The handler serves state it hosts, so a client's write is already at the destination when it is
    /// applied and must supersede an older local commit. Picking the other rule is silent: the server
    /// keeps serving a value the model has moved past, and nothing in the transport tests notices.
    /// Pins the wiring rather than the rule itself, which
    /// <c>ChangeQueueProcessorTests.WhenTheSourceAlreadyHoldsWhatItPushed_...</c> covers.
    /// </summary>
    [Fact]
    public void WhenTheHandlerCreatesItsProcessor_ThenItSelectsTheServerRule()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new TestRoot(context) { Name = "Initial" };
        var handler = new WebSocketSubjectHandler(subject, new WebSocketServerConfiguration(), NullLogger.Instance);

        // Act
        using var processor = handler.CreateChangeQueueProcessor(NullLogger.Instance);

        // Assert
        Assert.Equal(ChangeDeliveryRule.SourceValuesAreSettled, processor.DeliveryRule);
    }
}
