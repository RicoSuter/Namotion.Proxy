using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
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
/// Wires up a DynamicSubject, OpcUaSubjectClientSource, and SubscriptionManager
/// without a live OPC UA session. The SubjectPropertyWriter is put into the
/// applying (non-buffering) state by calling StartBuffering + LoadInitialStateAndResumeAsync
/// against the real source, whose initial state load returns null while the subject has no mapped properties.
/// </summary>
internal sealed class SubscriptionManagerTestHarness
{
    private readonly IInterceptorSubject _subject;

    public SubscriptionManager Manager { get; }

    /// <summary>
    /// Read-after-write spy injected by <see cref="CreateWithReadAfterWriteSpy"/>.
    /// Null when built with <see cref="Create"/>.
    /// </summary>
    public ReadAfterWriteRegistrarSpy? ReadAfterWriteSpy { get; }

    private SubscriptionManagerTestHarness(
        IInterceptorSubject subject,
        SubscriptionManager manager,
        ReadAfterWriteRegistrarSpy? readAfterWriteSpy)
    {
        _subject = subject;
        Manager = manager;
        ReadAfterWriteSpy = readAfterWriteSpy;
    }

    public static SubscriptionManagerTestHarness Create()
        => Build(readAfterWriteSpy: null);

    /// <summary>
    /// Builds the harness with a recording <see cref="ReadAfterWriteRegistrarSpy"/> injected
    /// as the SubscriptionManager's read-after-write registrar.
    /// The spy records every <c>RegisterProperty</c> call unconditionally (no filter).
    /// </summary>
    public static SubscriptionManagerTestHarness CreateWithReadAfterWriteSpy()
        => Build(new ReadAfterWriteRegistrarSpy());

    private static SubscriptionManagerTestHarness Build(ReadAfterWriteRegistrarSpy? readAfterWriteSpy)
    {
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var subject = new DynamicSubject(context);

        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false)
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

        var manager = new SubscriptionManager(
            source,
            propertyWriter,
            pollingManager: null,
            readAfterWriteManager: readAfterWriteSpy,
            configuration,
            source.ReportBackgroundError,
            NullLogger<OpcUaSubjectClientSource>.Instance);

        return new SubscriptionManagerTestHarness(subject, manager, readAfterWriteSpy);
    }

    /// <summary>
    /// Adds a dynamic double property to the subject and registers it in the manager's
    /// monitored items dictionary under the given client handle.
    /// </summary>
    public RegisteredSubjectProperty RegisterMonitoredItem(uint clientHandle, string propertyName)
    {
        var registeredSubject = _subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject has no registered subject. Ensure context has WithRegistry().");

        double storedValue = 0d;
        var property = registeredSubject.AddProperty<double>(
            propertyName,
            getValue: _ => storedValue,
            setValue: (_, value) => storedValue = value is double d ? d : 0d);

        Manager.MonitoredItemsForTesting[clientHandle] = property;

        return property;
    }

    /// <summary>
    /// Adds a dynamic double property to a new child subject that stays attached to the graph, so
    /// <c>TryGetRegisteredSubject()</c> keeps returning non-null. The property is deliberately not
    /// put into the manager's monitored items, which lets a test drive a detach callback before the
    /// items exist and then add them, the ordering a detach mid-setup produces.
    /// </summary>
    public RegisteredSubjectProperty CreateAttachedChildSubjectProperty(string propertyName)
    {
        var childSubject = new DynamicSubject(_subject.Context);

        var registeredChild = childSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Child subject has no registered subject.");

        double storedValue = 0d;
        return registeredChild.AddProperty<double>(
            propertyName,
            getValue: _ => storedValue,
            setValue: (_, value) => storedValue = value is double d ? d : 0d);
    }

    /// <summary>
    /// Registers a monitored item for a new child subject, then immediately detaches that
    /// child subject from the registry so that <c>TryGetRegisteredSubject()</c> returns null.
    /// Returns the property whose subject is now detached.
    /// </summary>
    public RegisteredSubjectProperty RegisterMonitoredItemThenDetachSubject(uint clientHandle, string propertyName)
    {
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var childSubject = new DynamicSubject(context);

        var registeredChild = childSubject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Child subject has no registered subject.");

        double storedValue = 0d;
        var property = registeredChild.AddProperty<double>(
            propertyName,
            getValue: _ => storedValue,
            setValue: (_, value) => storedValue = value is double d ? d : 0d);

        Manager.MonitoredItemsForTesting[clientHandle] = property;

        // Detach the child subject from its context so TryGetRegisteredSubject() returns null.
        context.TryGetLifecycleInterceptor()!.DetachSubjectFromContext(childSubject);

        return property;
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
}

/// <summary>
/// Records every <c>RegisterProperty</c> call. Does not apply any filter.
/// </summary>
internal sealed class ReadAfterWriteRegistrarSpy : IReadAfterWriteRegistrar
{
    private readonly List<RegisteredSubjectProperty> _registeredSubjects = [];

    public IReadOnlyList<RegisteredSubjectProperty> RegisteredSubjects => _registeredSubjects;

    public void RegisterProperty(NodeId nodeId, RegisteredSubjectProperty property, int? requestedSamplingInterval, TimeSpan revisedSamplingInterval)
    {
        _registeredSubjects.Add(property);
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
