using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Mqtt.Tests.Server;

/// <summary>
/// The broker owns its own restart loop, so nothing outside it can tell whether it is listening. These
/// pin the transitions the loop is responsible for, and that a restart can register its own outbound
/// change queue: the metrics permit one live registration at a time.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class MqttServerLivenessTests
{
    [InterceptorSubject]
    public partial class LivenessTestRoot
    {
        [Path("mqtt", "Name")]
        public partial string Name { get; set; }

        public LivenessTestRoot()
        {
            Name = string.Empty;
        }
    }

    [Fact]
    public async Task WhenTheBrokerIsListening_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        await using var server = CreateServer();

        // Act
        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Diagnostics.IsOperational == true,
            message: "The broker should report operational once it is listening.");

        // Assert
        Assert.NotNull(server.Diagnostics.OperationalChangeTime);
        Assert.NotNull(server.Diagnostics.StartTime);
        Assert.Null(server.Diagnostics.LastError);
        Assert.Equal(0, server.Diagnostics.ConnectedClientCount);

        // Act
        await server.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheBrokerIsListening_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange: a buffer time that outlasts the test, so a captured change stays in the processor's
        // queue instead of being flushed away before the depth can be read.
        await using var server = CreateServer(bufferTime: TimeSpan.FromMinutes(5));
        var root = (LivenessTestRoot)server.RootSubject;

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true,
                message: "The broker should report operational once it is listening.");

            // Act
            // Re-written on each poll because the processor only captures changes once it is running.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    root.Name = "v" + probeValue++;
                    return server.Diagnostics.OutboundChanges.Depth > 0;
                },
                message: "The outbound change queue never reported a depth, so it was never registered.");

            // Assert
            Assert.Null(server.Diagnostics.OutboundChanges.Capacity);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheBrokerIsForceKilled_ThenItBecomesOperationalAgain()
    {
        // Arrange
        await using var server = CreateServer();

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true,
                message: "The broker should report operational once it is listening.");

            var firstOperationalTime = server.Diagnostics.OperationalChangeTime;

            // Act
            await ((IFaultInjectable)server).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true &&
                      server.Diagnostics.OperationalChangeTime != firstOperationalTime,
                message: "The broker should report operational again after restarting.");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheBrokerCannotBind_ThenTheFailureIsReported()
    {
        // Arrange: the port is already taken, so the broker fails inside the loop, which swallows the
        // exception rather than letting the base class see it.
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await using var server = CreateServer(port: occupiedPort);

        await server.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.LastError is not null,
                message: "A broker that cannot bind should report the failure.");

            // Assert
            Assert.False(server.Diagnostics.IsOperational);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenStoppedAndStartedAgain_ThenEachRunReleasesItsBrokerLifecycleAndInitialStateWork()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var server = CreateServer(
            port: brokerPort,
            initialStateDelay: TimeSpan.FromMinutes(5),
            withLifecycle: true);

        var lifecycleInterceptor = server.RootSubject.GetContext().TryGetLifecycleInterceptor()!;
        var baselineHandlerCount = GetSubjectDetachingHandlerCount(lifecycleInterceptor);

        using var client = new MqttClientFactory().CreateMqttClient();
        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true,
                message: "The first broker run should become operational.");

            var firstBroker = GetCurrentBroker(server);
            Assert.Equal(baselineHandlerCount + 1, GetSubjectDetachingHandlerCount(lifecycleInterceptor));

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort)
                .Build();
            await client.ConnectAsync(options, CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => GetInitialStateTasks(server).Length == 1,
                message: "The first run should track its delayed initial-state publication.");
            var firstRunInitialStateTask = GetInitialStateTasks(server).Single();

            // Act
            await server.StopAsync(CancellationToken.None);

            // Assert
            Assert.True(IsDisposed(firstBroker));
            Assert.True(firstRunInitialStateTask.IsCompleted);
            Assert.Equal(baselineHandlerCount, GetSubjectDetachingHandlerCount(lifecycleInterceptor));

            // Act
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.IsOperational == true,
                message: "The second broker run should become operational.");

            // Assert
            Assert.NotSame(firstBroker, GetCurrentBroker(server));
            Assert.Equal(baselineHandlerCount + 1, GetSubjectDetachingHandlerCount(lifecycleInterceptor));
            Assert.True(firstRunInitialStateTask.IsCompleted);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenConnectedCallbackOutlivesRun_ThenItObservesTheCapturedCancellation()
    {
        // Arrange
        await using var server = CreateServer();
        await server.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
        var staleHandler = GetInstalledHandler<ClientConnectedEventArgs>(
            GetCurrentBroker(server), "ClientConnectedEvent");
        await server.StopAsync(CancellationToken.None);

        var args =
            new ClientConnectedEventArgs(
                new MqttConnectPacket { ClientId = "stale-client" },
                MqttProtocolVersion.V500,
                new IPEndPoint(IPAddress.Loopback, 1),
                new Hashtable());

        // Act
        await staleHandler(args);

        // Assert
        Assert.Equal(0, server.Diagnostics.ConnectedClientCount);
    }

    [Fact]
    public async Task WhenDisconnectedCallbackOutlivesRun_ThenItCannotChangeTheNextRunCount()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var server = CreateServer(port: brokerPort);
        using var client = new MqttClientFactory().CreateMqttClient();

        await server.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
            var staleHandler = GetInstalledHandler<ClientDisconnectedEventArgs>(
                GetCurrentBroker(server), "ClientDisconnectedEvent");

            await server.StopAsync(CancellationToken.None);
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort)
                .Build();
            await client.ConnectAsync(options, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Diagnostics.ConnectedClientCount == 1);

            var args = new ClientDisconnectedEventArgs(
                new MqttConnectPacket { ClientId = "stale-client" },
                new MqttDisconnectPacket(),
                MqttClientDisconnectType.Clean,
                new IPEndPoint(IPAddress.Loopback, 1),
                new Hashtable());

            // Act
            await staleHandler(args);

            // Assert
            Assert.Equal(1, server.Diagnostics.ConnectedClientCount);
        }
        finally
        {
            await DisconnectForTeardownAsync(client);

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenPublishMappingOutlivesRun_ThenOnlyTheReplacementRunCanMutateAndRelay()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        using var mapper = new GatedMqttMapper();
        await using var server = CreateServer(
            port: brokerPort,
            initialStateDelay: TimeSpan.Zero,
            mapper: mapper);
        var root = (LivenessTestRoot)server.RootSubject;
        using var publishCancellation = new CancellationTokenSource();
        using var subscriber = new MqttClientFactory().CreateMqttClient();
        var relayedValues = new ConcurrentQueue<string>();
        var newValueRelayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinelRelayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            var value = (string)new JsonMqttValueConverter().Deserialize(
                args.ApplicationMessage.Payload,
                typeof(string))!;
            relayedValues.Enqueue(value);
            if (value == "New")
            {
                newValueRelayed.TrySetResult();
            }
            else if (value == "Sentinel")
            {
                sentinelRelayed.TrySetResult();
            }

            return Task.CompletedTask;
        };

        await server.StartAsync(CancellationToken.None);
        Task? staleCallback = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
            var staleHandler = GetInstalledHandler<InterceptingPublishEventArgs>(
                GetCurrentBroker(server), "InterceptingPublishEvent");
            var staleArgs = CreatePublishEventArgs("Old", publishCancellation.Token);
            staleCallback = Task.Run(() => staleHandler(staleArgs));
            await mapper.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            await server.StopAsync(CancellationToken.None);
            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);

            var subscriberOptions = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort)
                .Build();
            await subscriber.ConnectAsync(subscriberOptions, CancellationToken.None);
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter.WithTopic("Name"))
                .Build();
            await subscriber.SubscribeAsync(subscribeOptions, CancellationToken.None);

            var currentHandler = GetInstalledHandler<InterceptingPublishEventArgs>(
                GetCurrentBroker(server), "InterceptingPublishEvent");
            var currentArgs = CreatePublishEventArgs("New");
            await currentHandler(currentArgs);
            await newValueRelayed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            mapper.Release();
            await staleCallback.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("New", root.Name);
            root.Name = "Sentinel";
            await sentinelRelayed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal(publishCancellation.Token, mapper.ObservedCancellationToken);
            Assert.False(currentArgs.ProcessPublish);
            Assert.Equal("Sentinel", root.Name);
            Assert.DoesNotContain("Old", relayedValues);
        }
        finally
        {
            mapper.Release();
            if (staleCallback is not null)
            {
                await staleCallback.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await DisconnectForTeardownAsync(subscriber);

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenMapperPausedPublishResumesDuringShutdown_ThenRetiringBrokerDoesNotRelayIt()
    {
        // Arrange
        using var mapper = new GatedMqttMapper();

        // Act & Assert
        await AssertRetiringBrokerDoesNotRelayMapperPausedPublishAsync(mapper);
    }

    [Fact]
    public async Task WhenUnmappedPublishMappingOutlivesShutdown_ThenRetiringBrokerDoesNotRelayIt()
    {
        // Arrange
        using var mapper = new GatedMqttMapper(returnNullAfterRelease: true);

        // Act & Assert
        await AssertRetiringBrokerDoesNotRelayMapperPausedPublishAsync(mapper);
    }

    [Fact]
    public async Task WhenPublishMappingThrowsAfterShutdown_ThenRetiringBrokerDoesNotRelayIt()
    {
        // Arrange
        using var mapper = new GatedMqttMapper(throwAfterRelease: true);

        // Act & Assert
        await AssertRetiringBrokerDoesNotRelayMapperPausedPublishAsync(mapper);
    }

    private static async Task AssertRetiringBrokerDoesNotRelayMapperPausedPublishAsync(GatedMqttMapper mapper)
    {
        var brokerPort = GetFreeTcpPort();
        using var commitGate = new GatedWriteInterceptor("Drain");
        await using var server = CreateServer(
            port: brokerPort,
            initialStateDelay: TimeSpan.Zero,
            mapper: mapper,
            writeInterceptor: commitGate);
        using var subscriber = new MqttClientFactory().CreateMqttClient();
        using var publisher = new MqttClientFactory().CreateMqttClient();
        var converter = new JsonMqttValueConverter();
        var relayedValues = new ConcurrentQueue<string>();
        var sentinelRelayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            var value = (string)converter.Deserialize(args.ApplicationMessage.Payload, typeof(string))!;
            relayedValues.Enqueue(value);
            if (value == "Sentinel")
            {
                sentinelRelayed.TrySetResult();
            }

            return Task.CompletedTask;
        };

        await server.StartAsync(CancellationToken.None);
        Task? stalePublishTask = null;
        Task? admittedCallback = null;
        Task? stopTask = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
            var broker = GetCurrentBroker(server);
            var subscriberOptions = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort)
                .WithClientId("relay-subscriber")
                .Build();
            await subscriber.ConnectAsync(subscriberOptions, CancellationToken.None);
            await subscriber.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(filter => filter
                        .WithTopic("Name")
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                    .Build(),
                CancellationToken.None);
            var publisherOptions = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort)
                .WithClientId("relay-publisher")
                .Build();
            await publisher.ConnectAsync(publisherOptions, CancellationToken.None);

            stalePublishTask = publisher.PublishAsync(
                CreateApplicationMessage("Old", converter),
                CancellationToken.None);
            await mapper.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            var publishHandler = GetInstalledHandler<InterceptingPublishEventArgs>(
                broker, "InterceptingPublishEvent");
            admittedCallback = Task.Run(() => publishHandler(CreatePublishEventArgs("Drain", converter: converter)));
            await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            stopTask = server.StopAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetInstalledHandlers<InterceptingPublishEventArgs>(broker, "InterceptingPublishEvent").Count == 0,
                message: "Shutdown should unregister the publish callback before draining admitted commits.");
            Assert.True(broker.IsStarted);
            var lateArgs = CreatePublishEventArgs("Late", converter: converter);
            await publishHandler(lateArgs);

            // Act
            mapper.Release();
            await stalePublishTask.WaitAsync(TimeSpan.FromSeconds(10));
            await broker.InjectApplicationMessage(
                new InjectedMqttApplicationMessage(CreateApplicationMessage("Sentinel", converter))
                {
                    SenderClientId = "sentinel-client"
                },
                CancellationToken.None);
            await sentinelRelayed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.DoesNotContain("Old", relayedValues);
            Assert.False(lateArgs.ProcessPublish);
        }
        finally
        {
            mapper.Release();

            // Drain the publish before releasing the commit gate. The gate is what holds the shutdown
            // open, so releasing it first lets the broker send DISCONNECT and the QoS 1 publish the
            // body left in flight never gets its PUBACK. The transport error that then surfaces here
            // would replace the failure the body is already reporting.
            if (stalePublishTask is not null)
            {
                await stalePublishTask.WaitAsync(TimeSpan.FromSeconds(10));
            }

            commitGate.Release();

            if (admittedCallback is not null)
            {
                await admittedCallback.WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await DisconnectForTeardownAsync(publisher);

            await DisconnectForTeardownAsync(subscriber);

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenShutdownRetiresPublishPausedInConversion_ThenLateCommitIsRejected()
    {
        // Arrange
        using var converter = new GatedMqttValueConverter("Old");
        await using var server = CreateServer(valueConverter: converter);
        var root = (LivenessTestRoot)server.RootSubject;
        root.Name = "Initial";

        await server.StartAsync(CancellationToken.None);
        Task? staleCallback = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
            var broker = GetCurrentBroker(server);
            var staleHandler = GetInstalledHandler<InterceptingPublishEventArgs>(
                broker, "InterceptingPublishEvent");
            staleCallback = Task.Run(() => staleHandler(CreatePublishEventArgs("Old", converter: converter)));
            await converter.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            var stopTask = server.StopAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetInstalledHandlers<InterceptingPublishEventArgs>(broker, "InterceptingPublishEvent").Count == 0,
                message: "Shutdown should unregister the old publish callback before retiring its commits.");
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            converter.Release();
            await staleCallback.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("Initial", root.Name);
        }
        finally
        {
            converter.Release();
            if (staleCallback is not null)
            {
                await staleCallback.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenShutdownRetiresAnAdmittedPublishCommit_ThenStopDrainsItBeforeReplacementRun()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        using var commitGate = new GatedWriteInterceptor("Old");
        await using var server = CreateServer(port: brokerPort, writeInterceptor: commitGate);
        var root = (LivenessTestRoot)server.RootSubject;

        await server.StartAsync(CancellationToken.None);
        Task? admittedCallback = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
            var broker = GetCurrentBroker(server);
            var handler = GetInstalledHandler<InterceptingPublishEventArgs>(
                broker, "InterceptingPublishEvent");
            admittedCallback = Task.Run(() => handler(CreatePublishEventArgs("Old")));
            await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            var stopTask = server.StopAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetInstalledHandlers<InterceptingPublishEventArgs>(broker, "InterceptingPublishEvent").Count == 0,
                message: "Shutdown should unregister the old publish callback before retiring its commits.");

            // Assert
            Assert.False(stopTask.IsCompleted);
            Assert.True(broker.IsStarted);

            // Act
            commitGate.Release();
            await admittedCallback.WaitAsync(TimeSpan.FromSeconds(10));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Old", root.Name);

            await server.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => server.Diagnostics.IsOperational == true);
            var replacementHandler = GetInstalledHandler<InterceptingPublishEventArgs>(
                GetCurrentBroker(server), "InterceptingPublishEvent");
            await replacementHandler(CreatePublishEventArgs("New"));

            // Assert
            Assert.Equal("New", root.Name);
        }
        finally
        {
            commitGate.Release();
            if (admittedCallback is not null)
            {
                await admittedCallback.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await server.StopAsync(CancellationToken.None);
        }
    }

    private static MqttSubjectServer CreateServer(
        TimeSpan? bufferTime = null,
        int? port = null,
        TimeSpan? initialStateDelay = null,
        bool withLifecycle = false,
        IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>? mapper = null,
        IMqttValueConverter? valueConverter = null,
        IWriteInterceptor? writeInterceptor = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        if (writeInterceptor is not null)
        {
            context.WithService<IWriteInterceptor>(() => writeInterceptor, _ => false);
        }

        if (withLifecycle)
        {
            context.WithLifecycle();
        }

        var configuration = new MqttServerConfiguration
        {
            BrokerHost = "127.0.0.1",
            BrokerPort = port ?? GetFreeTcpPort(),
            Mapper = mapper ?? new MqttCompositeMapper(
                new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
                new MqttAttributeMapper("mqtt")),
            ValueConverter = valueConverter ?? new JsonMqttValueConverter(),
            BufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8),
            InitialStateDelay = initialStateDelay ?? TimeSpan.FromMilliseconds(500)
        };

        return new MqttSubjectServer(
            new LivenessTestRoot(context), configuration, NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttServer GetCurrentBroker(MqttSubjectServer server) =>
        (MqttServer)(typeof(MqttSubjectServer)
            .GetField("_mqttServer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)
            ?? throw new InvalidOperationException("The server has no active MQTT broker."));

    private static Func<TEventArgs, Task> GetInstalledHandler<TEventArgs>(
        MqttServer broker,
        string eventPropertyName)
        where TEventArgs : EventArgs
    {
        return Assert.Single(GetInstalledHandlers<TEventArgs>(broker, eventPropertyName));
    }

    private static IReadOnlyList<Func<TEventArgs, Task>> GetInstalledHandlers<TEventArgs>(
        MqttServer broker,
        string eventPropertyName)
        where TEventArgs : EventArgs
    {
        var eventContainer = typeof(MqttServer)
            .GetField("_eventContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(broker)!;
        var asyncEvent = eventContainer.GetType()
            .GetProperty(eventPropertyName, BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(eventContainer)!;
        var handlers = (IEnumerable)asyncEvent.GetType()
            .GetField("_handlersForInvoke", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(asyncEvent)!;

        var result = new List<Func<TEventArgs, Task>>();
        foreach (var invocator in handlers)
        {
            var handler = (Func<TEventArgs, Task>?)invocator.GetType()
                .GetField("_asyncHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(invocator);
            if (handler is not null)
            {
                result.Add(handler);
            }
        }

        return result;
    }

    private static InterceptingPublishEventArgs CreatePublishEventArgs(
        string value,
        CancellationToken cancellationToken = default,
        IMqttValueConverter? converter = null)
    {
        converter ??= new JsonMqttValueConverter();
        return new InterceptingPublishEventArgs(
            CreateApplicationMessage(value, converter),
            "test-client",
            "test-user",
            new Hashtable(),
            cancellationToken);
    }

    private static MqttApplicationMessage CreateApplicationMessage(
        string value,
        IMqttValueConverter converter) =>
        new()
        {
            Topic = "Name",
            PayloadSegment = new ArraySegment<byte>(converter.Serialize(value, typeof(string))),
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
        };

    private static Task[] GetInitialStateTasks(MqttSubjectServer server) =>
        server.GetRunningInitialStateTasksSnapshot();

    private static int GetSubjectDetachingHandlerCount(LifecycleInterceptor lifecycleInterceptor)
    {
        // Two hops: the interceptor's event forwards its add and remove to an internal notifier,
        // so the subscriber list lives there rather than on a backing field of the interceptor.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var notifier = typeof(LifecycleInterceptor).GetField("_notifier", flags)?.GetValue(lifecycleInterceptor);
        var handler = (Delegate?)notifier?.GetType().GetField("SubjectDetaching", flags)?.GetValue(notifier);
        return handler?.GetInvocationList().Length ?? 0;
    }

    private static bool IsDisposed(MqttServer server)
    {
        for (var type = server.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(
                "IsDisposed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return (bool)property.GetValue(server)!;
            }
        }

        throw new InvalidOperationException("The MQTT broker does not expose its disposal state.");
    }

    /// <summary>
    /// Disconnects a client during test teardown. These tests deliberately retire the broker while
    /// clients are still attached, so by the time teardown runs the socket may already be gone and the
    /// DISCONNECT packet fails to send. IsConnected cannot guard against it: it is a snapshot that can
    /// go stale between the check and the send. Teardown failures must not decide the test, so the
    /// transport-level failure is swallowed here while every assertion above stays untouched.
    /// </summary>
    private static async Task DisconnectForTeardownAsync(IMqttClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            await client.DisconnectAsync();
        }
        catch (MqttCommunicationException)
        {
            // The broker is already gone, which is what the test asserted in the first place.
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class GatedMqttMapper(
        bool returnNullAfterRelease = false,
        bool throwAfterRelease = false)
        : IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey>, IDisposable
    {
        private readonly MqttCompositeMapper _inner = new(
            new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
            new MqttAttributeMapper("mqtt"));
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Entered => _entered.Task;

        public CancellationToken ObservedCancellationToken { get; private set; }

        public bool TryGetMapping(
            RegisteredSubjectProperty property,
            IInterceptorSubject rootSubject,
            [NotNullWhen(true)]
            out MqttPropertyMapping? mapping) =>
            _inner.TryGetMapping(property, rootSubject, out mapping);

        public async ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
            MqttLookupKey key,
            RegisteredSubject subject,
            CancellationToken cancellationToken)
        {
            var isBlockedInvocation = Interlocked.Exchange(ref _blocked, 1) == 0;
            if (isBlockedInvocation)
            {
                ObservedCancellationToken = cancellationToken;
                _entered.TrySetResult();
                _release.Wait();
            }

            if (throwAfterRelease && isBlockedInvocation)
            {
                throw new InvalidOperationException("Mapping failed after release.");
            }

            return returnNullAfterRelease && isBlockedInvocation
                ? null
                : await _inner.TryGetPropertyAsync(key, subject, cancellationToken).ConfigureAwait(false);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class GatedMqttValueConverter(string blockedValue) : IMqttValueConverter, IDisposable
    {
        private readonly JsonMqttValueConverter _inner = new();
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Entered => _entered.Task;

        public byte[] Serialize(object? value, Type type) => _inner.Serialize(value, type);

        public object? Deserialize(ReadOnlySequence<byte> payload, Type type)
        {
            var value = _inner.Deserialize(payload, type);
            if (Equals(value, blockedValue) && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
            }

            return value;
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class GatedWriteInterceptor(string blockedValue) : IWriteInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Entered => _entered.Task;

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (Equals(context.NewValue, blockedValue) && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
            }

            next(ref context);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
