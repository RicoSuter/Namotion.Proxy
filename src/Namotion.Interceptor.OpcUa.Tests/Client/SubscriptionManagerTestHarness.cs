using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Shared test harness for SubscriptionManager unit tests.
/// Wires up a DynamicSubject, OpcUaSubjectClientSource, ReadAfterWriteManager and SubscriptionManager
/// without a live OPC UA session. The SubjectPropertyWriter is put into the
/// applying (non-buffering) state by calling StartBuffering + LoadInitialStateAndResumeAsync
/// against the real source, whose initial state load returns null while the subject has no mapped properties.
/// The configuration requests sampling interval zero, so a monitored item whose server-revised
/// interval is positive passes the read-after-write filter.
/// </summary>
internal sealed class SubscriptionManagerTestHarness
{
    private readonly IInterceptorSubject _subject;

    public SubscriptionManager Manager { get; }

    public ReadAfterWriteManager ReadAfterWriteManager { get; }

    /// <summary>
    /// Null unless built with polling fallback. Never started: escalation only needs a target.
    /// </summary>
    public PollingManager? PollingManager { get; }

    private SubscriptionManagerTestHarness(
        IInterceptorSubject subject,
        SubscriptionManager manager,
        ReadAfterWriteManager readAfterWriteManager,
        PollingManager? pollingManager)
    {
        _subject = subject;
        Manager = manager;
        ReadAfterWriteManager = readAfterWriteManager;
        PollingManager = pollingManager;
    }

    public static SubscriptionManagerTestHarness Create(bool withPollingFallback = false)
    {
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var subject = new DynamicSubject(context);

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false),
            DefaultSamplingInterval = 0
        };

        var source = new OpcUaSubjectClientSource(subject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        // The SubjectPropertyWriter buffers updates until LoadInitialStateAndResumeAsync is called
        // (its _updates field starts as a non-null list at construction). We need it in the
        // applying state (i.e., _updates == null) so that a delivered notification actually
        // updates subjects in tests. The subject has no mapped properties yet, so the source's
        // initial state load returns null without needing a live session.
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        propertyWriter.StartBuffering();
        propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None).GetAwaiter().GetResult();

        var readAfterWriteManager = new ReadAfterWriteManager(
            sessionProvider: static () => null,
            source,
            configuration,
            source.ReadAfterWriteMetrics,
            source.ReportBackgroundError,
            NullLogger.Instance);

        var pollingManager = withPollingFallback
            ? new PollingManager(
                source,
                sessionProvider: static () => null,
                propertyWriter,
                configuration,
                source.PollingMetrics,
                source.ReportBackgroundError,
                NullLogger.Instance)
            : null;

        var manager = new SubscriptionManager(
            source,
            propertyWriter,
            pollingManager,
            readAfterWriteManager,
            configuration,
            source.ReportBackgroundError,
            NullLogger<OpcUaSubjectClientSource>.Instance,
            // There is no session to apply against; the tests only care what the manager does around the call.
            applyChangesAsync: static (_, _) => Task.CompletedTask);

        return new SubscriptionManagerTestHarness(subject, manager, readAfterWriteManager, pollingManager);
    }

    /// <summary>
    /// Adds a dynamic double property to the root subject and tracks a created monitored item for
    /// it under the given client handle. The returned item's handle is the property.
    /// </summary>
    public MonitoredItem RegisterMonitoredItem(uint clientHandle, string propertyName, double revisedSamplingIntervalMs = 0)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject has no registered subject. Ensure context has WithRegistry().");

        return TrackMonitoredItem(clientHandle, AddDoubleProperty(registeredSubject, propertyName), revisedSamplingIntervalMs);
    }

    /// <summary>
    /// Adds a dynamic double property to a new child subject that stays attached to the graph, so
    /// <c>TryGetRegisteredSubject()</c> keeps returning non-null. The property is deliberately not
    /// tracked by the manager, which lets a test drive a detach callback before the items exist
    /// and then add them, the ordering a detach mid-setup produces.
    /// </summary>
    public RegisteredSubjectProperty CreateAttachedChildSubjectProperty(string propertyName)
    {
        var childSubject = new DynamicSubject(_subject.Context);

        var registeredChild = childSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Child subject has no registered subject.");

        return AddDoubleProperty(registeredChild, propertyName);
    }

    /// <summary>
    /// Tracks a monitored item for a new child subject, then immediately detaches that child
    /// subject from the registry so that <c>TryGetRegisteredSubject()</c> returns null.
    /// The returned item's handle is the property whose subject is now detached.
    /// </summary>
    public MonitoredItem RegisterMonitoredItemThenDetachSubject(uint clientHandle, string propertyName, double revisedSamplingIntervalMs = 0)
    {
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var childSubject = new DynamicSubject(context);

        var registeredChild = childSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Child subject has no registered subject.");

        var item = TrackMonitoredItem(clientHandle, AddDoubleProperty(registeredChild, propertyName), revisedSamplingIntervalMs);

        // Detach the child subject from its context so TryGetRegisteredSubject() returns null.
        context.TryGetLifecycleInterceptor()!.DetachSubjectFromContext(childSubject);

        return item;
    }

    /// <summary>
    /// Tracks a created monitored item for the property under the given client handle, the way
    /// <c>CreateBatchedSubscriptionsAsync</c> does for every item it adds to a subscription.
    /// </summary>
    public MonitoredItem TrackMonitoredItem(uint clientHandle, RegisteredSubjectProperty property, double revisedSamplingIntervalMs = 0)
    {
        var item = CreatedMonitoredItem.Create(clientHandle, new NodeId(clientHandle, 2), revisedSamplingIntervalMs, property);
        Manager.TrackMonitoredItem(item);
        return item;
    }

    /// <summary>
    /// Reads the current value of a dynamic property by name.
    /// </summary>
    public object? GetValue(string name)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject has no registered subject.");

        return registeredSubject.TryGetProperty(name)?.GetValue();
    }

    public static DataChangeNotification CreateNotification(uint clientHandle, object value)
    {
        return new DataChangeNotification
        {
            MonitoredItems =
            [
                new MonitoredItemNotification
                {
                    ClientHandle = clientHandle,
                    Value = new DataValue(new Variant(value), StatusCodes.Good, DateTime.UtcNow)
                }
            ],
            DiagnosticInfos = []
        };
    }

    private static RegisteredSubjectProperty AddDoubleProperty(RegisteredSubject registeredSubject, string propertyName)
    {
        double storedValue = 0d;
        return registeredSubject.AddProperty<double>(
            propertyName,
            getValue: _ => storedValue,
            setValue: (_, value) => storedValue = value is double d ? d : 0d);
    }
}

/// <summary>
/// A MonitoredItem subclass that calls <c>SetCreateResult</c> in its constructor so that
/// <c>Status.Created</c> is true and <c>ClientHandle</c> matches the supplied value.
/// Used only in tests to build snapshot items without a live OPC UA session.
/// </summary>
internal sealed class CreatedMonitoredItem : MonitoredItem
{
    private CreatedMonitoredItem(uint clientHandle, NodeId nodeId, double revisedIntervalMs, object handle)
        : base(clientHandle, NullTelemetryContext.Instance)
    {
        Handle = handle;
        StartNodeId = nodeId;

        var request = new MonitoredItemCreateRequest
        {
            ItemToMonitor = new ReadValueId
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value
            },
            RequestedParameters = new MonitoringParameters
            {
                ClientHandle = clientHandle,
                SamplingInterval = revisedIntervalMs
            }
        };
        var result = new MonitoredItemCreateResult
        {
            StatusCode = StatusCodes.Good,
            MonitoredItemId = clientHandle == 0 ? 1u : clientHandle,
            RevisedSamplingInterval = revisedIntervalMs
        };

        SetCreateResult(request, result, 0, new DiagnosticInfoCollection(), new ResponseHeader());
    }

    public static CreatedMonitoredItem Create(uint clientHandle, NodeId nodeId, double revisedIntervalMs, object handle)
        => new(clientHandle, nodeId, revisedIntervalMs, handle);
}
