using System.Buffers;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Packets;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Mqtt.Client;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Mqtt.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Mqtt.Tests.Client;

/// <summary>
/// The client reports liveness from three places the broker drives: the initial connect, the
/// broker's disconnect event, and the connection monitor's own reconnect. None of them is observable
/// without a real broker.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MqttNetworkIntegrationCollection.Name)]
public partial class MqttClientLivenessTests
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
    public async Task WhenTheClientConnects_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort);

        // Act
        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);

        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational == true,
            message: "The client should report operational once it has connected to the broker.");

        // Assert
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
        Assert.NotNull(source.Diagnostics.StartTime);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(source.Diagnostics.IsOperational);

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenTheConnectionDrops_ThenLivenessFallsAndRisesAgainOnReconnect()
    {
        // Arrange - a reconnect delay far longer than the poll interval below, so the client cannot
        // be back before the drop has been observed.
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort, reconnectDelay: TimeSpan.FromSeconds(2));

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational once it has connected to the broker.");

            var connectedAt = source.Diagnostics.OperationalChangeTime;

            // Act - Disconnect is the soft fault: it breaks the broker connection without stopping the
            // connector, so the connection monitor reconnects to the still-running broker.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == false,
                message: "A disconnected client should stop reporting that it is serving.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational again once the monitor has reconnected.");

            // The rise is a second transition rather than the first one never having been dropped.
            Assert.True(source.Diagnostics.OperationalChangeTime > connectedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheClientIsForceKilled_ThenLivenessCyclesAndTheTransportIsReplaced()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort, reconnectDelay: TimeSpan.FromSeconds(1));

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational once it has connected to the broker.");

            var firstClient = GetCurrentClient(source);

            // Act
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == false,
                timeout: TimeSpan.FromSeconds(3),
                message: "A force-killed client should report the transport outage.");
            var downAt = source.Diagnostics.OperationalChangeTime;

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "A force-killed client should become operational on a replacement transport.");

            Assert.NotSame(firstClient, GetCurrentClient(source));
            Assert.False(firstClient.IsConnected);
            Assert.True(source.Diagnostics.OperationalChangeTime > downAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenDisconnectCallbackArrivesAfterReconnect_ThenHealthyConnectionRemainsOperational()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(brokerPort, reconnectDelay: TimeSpan.FromMilliseconds(200));
        using var stateRecorder = SourceStateRecorder.SubscribeTo(source);

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true,
                message: "The client should report operational once it has connected to the broker.");
            await stateRecorder.WaitForStatesAsync(
                TimeSpan.FromSeconds(30),
                "The initial subscription should complete.",
                SourceState.Synchronized);

            var client = GetCurrentClient(source);
            var monitor = GetConnectionMonitor(source);
            var delayedDisconnectedHandler = GetDisconnectedHandler(source);
            var disconnectedArgs = new TaskCompletionSource<MqttClientDisconnectedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task CaptureDisconnectedArgsAsync(MqttClientDisconnectedEventArgs args)
            {
                disconnectedArgs.TrySetResult(args);
                return Task.CompletedTask;
            }

            // Hold the raw MQTTnet callback while the monitor handles the confirmed transport loss.
            client.DisconnectedAsync -= delayedDisconnectedHandler;
            client.DisconnectedAsync += CaptureDisconnectedArgsAsync;
            try
            {
                await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
                var delayedArgs = await disconnectedArgs.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // The monitor detects the loss independently, buffers, and reconnects before
                // MQTTnet is allowed to deliver its delayed raw callback.
                monitor.SignalReconnectNeeded();

                await AsyncTestHelpers.WaitUntilAsync(
                    () => source.Diagnostics.IsOperational == false,
                    message: "The confirmed disconnect should mark the client non-operational.");
                await stateRecorder.WaitForStatesAsync(
                    TimeSpan.FromSeconds(15),
                    "The confirmed disconnect should start buffering.",
                    SourceState.Synchronized,
                    SourceState.Synchronizing);

                await AsyncTestHelpers.WaitUntilAsync(
                    () => source.Diagnostics.IsOperational == true,
                    message: "The client should report operational again once the monitor has reconnected.");
                await stateRecorder.WaitForStatesAsync(
                    TimeSpan.FromSeconds(30),
                    "The client should finish synchronizing after reconnecting.",
                    SourceState.Synchronized,
                    SourceState.Synchronizing,
                    SourceState.Synchronized);

                // Act
                await delayedDisconnectedHandler(delayedArgs);

                // Assert
                Assert.True(source.Diagnostics.IsOperational);
                Assert.True(client.IsConnected);
            }
            finally
            {
                client.DisconnectedAsync -= CaptureDisconnectedArgsAsync;
                client.DisconnectedAsync += delayedDisconnectedHandler;
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOldTransportMessageFinishesConversionAfterReplacement_ThenItCannotOverwriteRecoveredValue()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        using var converter = new GatedMqttValueConverter("Old");
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(
            brokerPort,
            reconnectDelay: TimeSpan.FromSeconds(1),
            valueConverter: converter);
        var brokerRoot = (LivenessTestRoot)broker.RootSubject;
        var clientRoot = (LivenessTestRoot)source.RootSubject;

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);

        Task? oldCallback = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial",
                message: "The initial transport should receive the broker's retained value.");

            var oldClient = GetCurrentClient(source);
            var oldMessageHandler = Assert.Single(GetApplicationMessageHandlers(oldClient));
            oldCallback = Task.Run(() => oldMessageHandler(CreateMessageReceivedEventArgs("Old", converter)));
            await converter.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            brokerRoot.Name = "Recovered";
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.Name == "Recovered",
                message: "The live transport should receive the newer retained value.");

            var property = clientRoot.GetPropertyReference(nameof(LivenessTestRoot.Name));
            property.SetValueFromSource(
                source,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "Awaiting recovery");

            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true &&
                    !ReferenceEquals(oldClient, GetCurrentClient(source)) &&
                    clientRoot.Name == "Recovered",
                message: "The replacement transport should restore the newer retained value.");

            // Act
            converter.Release();
            await oldCallback.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Empty(GetApplicationMessageHandlers(oldClient));
            Assert.Equal("Recovered", clientRoot.Name);
        }
        finally
        {
            converter.Release();
            if (oldCallback is not null)
            {
                await oldCallback.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenOldTransportCommitIsAdmittedBeforeReplacement_ThenRecoveryCommitsLast()
    {
        // Arrange
        var brokerPort = GetFreeTcpPort();
        using var commitGate = new GatedWriteInterceptor("Old");
        await using var broker = CreateBroker(brokerPort);
        await using var source = CreateClientSource(
            brokerPort,
            reconnectDelay: TimeSpan.FromSeconds(1),
            writeInterceptor: commitGate);
        var brokerRoot = (LivenessTestRoot)broker.RootSubject;
        var clientRoot = (LivenessTestRoot)source.RootSubject;

        await broker.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);

        Task? oldCallback = null;
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Initial",
                message: "The initial transport should receive the broker's retained value.");

            brokerRoot.Name = "Recovered";
            await AsyncTestHelpers.WaitUntilAsync(
                () => clientRoot.Name == "Recovered",
                message: "The live transport should receive the newer retained value.");

            var property = clientRoot.GetPropertyReference(nameof(LivenessTestRoot.Name));
            property.SetValueFromSource(
                source,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "Awaiting recovery");
            Assert.Equal("Awaiting recovery", clientRoot.Name);

            var oldClient = GetCurrentClient(source);
            var oldMessageHandler = Assert.Single(GetApplicationMessageHandlers(oldClient));
            oldCallback = Task.Run(() => oldMessageHandler(CreateMessageReceivedEventArgs("Old")));
            await commitGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            // Samples, at the instant the teardown's drain releases it, whether the commit the drain
            // was supposed to wait for had already been applied. A teardown that skipped the drain
            // reaches this while the gate still holds the commit, so it samples false.
            var drainWaitedForCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.AfterTransportCommitDrain = () => drainWaitedForCommit.TrySetResult(commitGate.BlockedWriteCompleted);

            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

            // The ownership field is cleared right when the teardown retires the lease, so this is the
            // point from which the drain either holds the teardown back or does not.
            await AsyncTestHelpers.WaitUntilAsync(
                () => GetTransportOwnership(source) is null,
                message: "The kill teardown should retire the transport's commit lease.");
            Assert.False(source.Diagnostics.IsOperational);
            Assert.Equal("Awaiting recovery", clientRoot.Name);

            // Act
            commitGate.Release();
            await oldCallback.WaitAsync(TimeSpan.FromSeconds(10));
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true && clientRoot.Name == "Recovered",
                message: "Recovery should follow an old commit that retirement already admitted.");

            // Assert
            Assert.True(
                await drainWaitedForCommit.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                "The kill teardown should stay in the drain until the admitted commit has been applied.");
            Assert.Empty(GetApplicationMessageHandlers(oldClient));
            Assert.Equal("Recovered", clientRoot.Name);
        }
        finally
        {
            commitGate.Release();
            if (oldCallback is not null)
            {
                await oldCallback.WaitAsync(TimeSpan.FromSeconds(10));
            }

            source.AfterTransportCommitDrain = null;
            await source.StopAsync(CancellationToken.None);
            await broker.StopAsync(CancellationToken.None);
        }
    }

    private static MqttSubjectServer CreateBroker(int brokerPort)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new MqttSubjectServer(
            new LivenessTestRoot(context) { Name = "Initial" },
            new MqttServerConfiguration { BrokerPort = brokerPort, Mapper = CreateMapper() },
            NullLogger<MqttSubjectServer>.Instance);
    }

    private static MqttSubjectClientSource CreateClientSource(
        int brokerPort,
        TimeSpan? reconnectDelay = null,
        IMqttValueConverter? valueConverter = null,
        IWriteInterceptor? writeInterceptor = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceTransactions()
            .WithSourceMonitoring();

        if (writeInterceptor is not null)
        {
            context.WithService<IWriteInterceptor>(() => writeInterceptor, _ => false);
        }

        var source = new MqttSubjectClientSource(
            new LivenessTestRoot(context),
            new MqttClientConfiguration
            {
                // The broker binds IPv4 only, so dialling it by name would let the client spend its
                // connect timeout on the IPv6 loopback first.
                BrokerHost = "127.0.0.1",
                BrokerPort = brokerPort,
                Mapper = CreateMapper(),
                ValueConverter = valueConverter ?? new JsonMqttValueConverter(),
                ReconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1),
                MaximumReconnectDelay = TimeSpan.FromSeconds(4),
                HealthCheckInterval = TimeSpan.FromSeconds(1)
            },
            NullLogger<MqttSubjectClientSource>.Instance);

        return source;
    }

    private static MqttCompositeMapper CreateMapper() => new(
        new MqttPathProviderMapper(new AttributeBasedPathProvider("mqtt", '/')),
        new MqttAttributeMapper("mqtt"));

    private static object? GetTransportOwnership(MqttSubjectClientSource source) =>
        typeof(MqttSubjectClientSource)
            .GetField("_transportOwnership", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source);

    private static IMqttClient GetCurrentClient(MqttSubjectClientSource source)
    {
        return TryGetCurrentClient(source) ??
            throw new InvalidOperationException("The source has no active MQTT client.");
    }

    private static IMqttClient? TryGetCurrentClient(MqttSubjectClientSource source)
    {
        var client = typeof(MqttSubjectClientSource)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(source);

        return client as IMqttClient;
    }

    private static MqttConnectionMonitor GetConnectionMonitor(MqttSubjectClientSource source)
    {
        var monitor = typeof(MqttSubjectClientSource)
            .GetField("_connectionMonitor", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(source);

        return monitor as MqttConnectionMonitor ??
            throw new InvalidOperationException("The source has no active MQTT connection monitor.");
    }

    private static Func<MqttClientDisconnectedEventArgs, Task> GetDisconnectedHandler(
        MqttSubjectClientSource source)
    {
        var method = typeof(MqttSubjectClientSource)
            .GetMethod("OnDisconnectedAsync", BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The source has no disconnected handler.");

        return method.CreateDelegate<Func<MqttClientDisconnectedEventArgs, Task>>(source);
    }

    private static IReadOnlyList<Func<MqttApplicationMessageReceivedEventArgs, Task>> GetApplicationMessageHandlers(
        IMqttClient client)
    {
        var events = client.GetType()
            .GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(client) ?? throw new InvalidOperationException("The MQTT client has no event collection.");
        var applicationMessageEvent = events.GetType()
            .GetProperty("ApplicationMessageReceivedEvent", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(events) ?? throw new InvalidOperationException("The MQTT client has no application-message event.");
        var handlers = applicationMessageEvent.GetType()
            .GetField("_handlersForInvoke", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(applicationMessageEvent) as IEnumerable ??
            throw new InvalidOperationException("The MQTT application-message event has no handler snapshot.");

        var result = new List<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
        foreach (var handler in handlers)
        {
            if (handler?.GetType()
                .GetField("_asyncHandler", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(handler) is Func<MqttApplicationMessageReceivedEventArgs, Task> asyncHandler)
            {
                result.Add(asyncHandler);
            }
        }

        return result;
    }

    private static MqttApplicationMessageReceivedEventArgs CreateMessageReceivedEventArgs(
        string value,
        IMqttValueConverter? converter = null)
    {
        converter ??= new JsonMqttValueConverter();
        var message = new MqttApplicationMessage
        {
            Topic = "Name",
            PayloadSegment = new ArraySegment<byte>(converter.Serialize(value, typeof(string)))
        };

        return new MqttApplicationMessageReceivedEventArgs(
            "old-client",
            message,
            new MqttPublishPacket(),
            static (_, _) => Task.CompletedTask);
    }

    private sealed class GatedMqttValueConverter(string blockedValue) : IMqttValueConverter, IDisposable
    {
        private readonly JsonMqttValueConverter _inner = new();
        private readonly ManualResetEventSlim _release = new(false);
        private int _blocked;

        public Task Entered => _entered.Task;

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    // Instance-scoped test seam. It blocks only after the source has admitted the old commit, and
    // it runs outside the commit lease's internal lock.
    private sealed class GatedWriteInterceptor(string blockedValue) : IWriteInterceptor, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _blocked;
        private int _blockedWriteCompleted;

        public Task Entered => _entered.Task;

        /// <summary>
        /// Set once the blocked write has actually been applied, which happens before the commit that
        /// carries it is released, so a drain that observes this as <c>false</c> did not wait for it.
        /// </summary>
        public bool BlockedWriteCompleted => Volatile.Read(ref _blockedWriteCompleted) == 1;

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (Equals(context.NewValue, blockedValue) && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _entered.TrySetResult();
                _release.Wait();
                next(ref context);
                Volatile.Write(ref _blockedWriteCompleted, 1);
                return;
            }

            next(ref context);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
