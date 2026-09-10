using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Client.Polling;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionManagerDiagnosticsTests
{
    [Fact]
    public async Task WhenTrackedStateMutates_ThenCachedCountsStayCurrent()
    {
        // Arrange
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            new ConcurrentQueue<Exception>());
        var firstSubscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var secondSubscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var replacementSubscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = property
        };

        // Act & Assert
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
        manager.UpdateTransferredSubscriptions([firstSubscription, secondSubscription]);
        Assert.Equal(2, manager.SubscriptionCount);
        manager.UpdateTransferredSubscriptions([replacementSubscription]);
        Assert.Equal(1, manager.SubscriptionCount);

        manager.TrackMonitoredItem(monitoredItem);
        Assert.Equal(1, manager.MonitoredItemCount);
        manager.TrackMonitoredItem(monitoredItem);
        Assert.Equal(1, manager.MonitoredItemCount);
        manager.RemoveItemsForSubject(source.RootSubject);
        Assert.Equal(0, manager.MonitoredItemCount);
        manager.TrackMonitoredItem(monitoredItem);
        Assert.Equal(1, manager.MonitoredItemCount);

        await manager.DisposeAsync();
        Assert.Equal(
            (Subscriptions: 0, MonitoredItems: 0),
            (Subscriptions: manager.SubscriptionCount, MonitoredItems: manager.MonitoredItemCount));
    }

    [Fact]
    public async Task WhenConversionThrows_ThenSourceReportsExactFailureOnceAndSdkCallbackStillThrows()
    {
        // Arrange
        var error = new InvalidOperationException("conversion failed");
        var configuration = CreateConfiguration();
        configuration.ValueConverter = new ThrowingConverter(error);
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors);
        var clientHandle = TrackNameProperty(manager, source);
        var notification = CreateNotification(clientHandle, "value");

        // Act & Assert
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            manager.OnFastDataChange(
                new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
                notification,
                []));

        Assert.Same(error, thrown);
        var reported = Assert.Single(reportedErrors);
        Assert.Same(error, reported);
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenPropertyApplicationThrows_ThenSourceReportsExactFailureOnceAndKeepsItAfterRecovery()
    {
        // Arrange
        var configuration = CreateConfiguration();
        configuration.ValueConverter = new WrongTypeConverter();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors);
        var clientHandle = TrackNameProperty(manager, source);

        manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            CreateNotification(clientHandle, "value"),
            []);

        // Act
        await propertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        var error = Assert.Single(reportedErrors);
        Assert.IsType<InvalidCastException>(error);
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenDisposalWinsDuringConversion_ThenFailureIsRethrownWithoutReporting()
    {
        // Arrange
        using var converter = new GatedThrowingConverter();
        var configuration = CreateConfiguration();
        configuration.ValueConverter = converter;
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(source, propertyWriter, configuration, reportedErrors);
        var clientHandle = TrackNameProperty(manager, source);

        var callback = Task.Run(() => manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            CreateNotification(clientHandle, "value"),
            []));
        await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await manager.DisposeAsync();
        converter.Release();

        // Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => callback);
        Assert.Same(converter.Error, thrown);
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenPrimaryApplyChangesThrows_ThenSourceReportsExactFailureOnceAndFallsBack()
    {
        // Arrange
        var error = new ServiceResultException(StatusCodes.BadUnexpectedError, "primary apply failed");
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var pollingManager = new PollingManager(
            source,
            sessionProvider: () => null,
            propertyWriter,
            configuration,
            source.PollingMetrics,
            source.ReportBackgroundError,
            NullLogger.Instance);
        var applyAttempts = 0;
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Interlocked.Increment(ref applyAttempts) == 1
                ? Task.FromException(error)
                : Task.CompletedTask,
            pollingManager);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        var monitoredItem = CreateFailedNameItem(source, StatusCodes.BadNotSupported);
        subscription.AddItem(monitoredItem);
        manager.TrackMonitoredItem(monitoredItem);

        // Act
        await manager.ApplyChangesAndFilterFailedMonitoredItemsAsync(subscription, CancellationToken.None);

        // Assert
        var reported = Assert.Single(reportedErrors);
        Assert.Same(error, reported);
        Assert.Same(error, source.Diagnostics.LastError);
        Assert.Equal(2, applyAttempts);
        Assert.Empty(subscription.MonitoredItems);
        Assert.Equal(1, pollingManager.PollingItemCount);
    }

    [Fact]
    public async Task WhenPrimaryApplyChangesTokenIsCancelled_ThenFailureIsNotReportedAndFilteringContinues()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var error = new ServiceResultException(StatusCodes.BadRequestCancelledByClient, "cancelled primary apply failed");
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        var applyAttempts = 0;
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Interlocked.Increment(ref applyAttempts) == 1
                ? Task.FromException(error)
                : Task.CompletedTask);
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(CreateFailedNameItem(source, StatusCodes.BadAttributeIdInvalid));

        // Act
        await manager.ApplyChangesAndFilterFailedMonitoredItemsAsync(subscription, cancellationTokenSource.Token);

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
        Assert.Equal(2, applyAttempts);
        Assert.Empty(subscription.MonitoredItems);
    }

    [Fact]
    public async Task WhenDisposalWinsDuringPrimaryApplyChanges_ThenFailureIsNotReportedAndFilteringContinues()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new ServiceResultException(StatusCodes.BadSessionClosed, "shutdown primary apply failed");
        var configuration = CreateConfiguration();
        configuration.EnablePollingFallback = false;
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        var applyAttempts = 0;
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            async (_, _) =>
            {
                if (Interlocked.Increment(ref applyAttempts) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    throw error;
                }
            });
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(CreateFailedNameItem(source, StatusCodes.BadAttributeIdInvalid));

        var applyChanges = manager.ApplyChangesAndFilterFailedMonitoredItemsAsync(
            subscription,
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await manager.DisposeAsync();
        release.TrySetResult();
        await applyChanges;

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
        Assert.Equal(2, applyAttempts);
        Assert.Empty(subscription.MonitoredItems);
    }

    [Fact]
    public async Task WhenFallbackApplyChangesThrows_ThenSourceReportsExactFailureOnceAndContinues()
    {
        // Arrange
        var error = new InvalidOperationException("fallback apply failed");
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Task.FromException(error));

        // Act
        await manager.RemoveAndFallBackToPollingAsync(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            [],
            [],
            CancellationToken.None);

        // Assert
        var reported = Assert.Single(reportedErrors);
        Assert.Same(error, reported);
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenFallbackApplyChangesIsCancelledByWorkToken_ThenFailureIsNotReportedAndContinues()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var error = new OperationCanceledException(cancellationTokenSource.Token);
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            (_, _) => Task.FromException(error));

        // Act
        await manager.RemoveAndFallBackToPollingAsync(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            [],
            [],
            cancellationTokenSource.Token);

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenDisposalWinsDuringFallbackApplyChanges_ThenFailureIsNotReportedAndContinues()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("shutdown fallback apply failed");
        var configuration = CreateConfiguration();
        await using var source = CreateClientSource(configuration: configuration);
        var reportedErrors = new ConcurrentQueue<Exception>();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var manager = CreateManager(
            source,
            propertyWriter,
            configuration,
            reportedErrors,
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                throw error;
            });

        var applyChanges = manager.RemoveAndFallBackToPollingAsync(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            [],
            [],
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await manager.DisposeAsync();
        release.TrySetResult();
        await applyChanges;

        // Assert
        Assert.Empty(reportedErrors);
        Assert.Null(source.Diagnostics.LastError);
    }

    private static SubscriptionManager CreateManager(
        OpcUaSubjectClientSource source,
        SubjectPropertyWriter propertyWriter,
        OpcUaClientConfiguration configuration,
        ConcurrentQueue<Exception> reportedErrors,
        Func<Subscription, CancellationToken, Task>? applyChangesAsync = null,
        PollingManager? pollingManager = null) =>
        new(
            source,
            propertyWriter,
            pollingManager,
            readAfterWriteManager: null,
            configuration,
            error =>
            {
                reportedErrors.Enqueue(error);
                source.ReportBackgroundError(error);
            },
            NullLogger.Instance,
            applyChangesAsync);

    private static MonitoredItem CreateFailedNameItem(OpcUaSubjectClientSource source, uint statusCode)
    {
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            DisplayName = nameof(TestRoot.Name),
            Handle = property
        };
        monitoredItem.SetError(new ServiceResult(statusCode));
        return monitoredItem;
    }

    private static uint TrackNameProperty(SubscriptionManager manager, OpcUaSubjectClientSource source)
    {
        var property = new RegisteredSubject((TestRoot)source.RootSubject)
            .TryGetProperty(nameof(TestRoot.Name))!;
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("Name", 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = property
        };
        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(monitoredItem);
        manager.TrackMonitoredItem(monitoredItem);
        return monitoredItem.ClientHandle;
    }

    private static DataChangeNotification CreateNotification(uint clientHandle, object value) =>
        new()
        {
            MonitoredItems =
            [
                new MonitoredItemNotification
                {
                    ClientHandle = clientHandle,
                    Value = new DataValue
                    {
                        Value = value,
                        SourceTimestamp = DateTime.UtcNow,
                        StatusCode = StatusCodes.Good
                    }
                }
            ],
            DiagnosticInfos = []
        };

    private sealed class ThrowingConverter(Exception error) : OpcUaValueConverter
    {
        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property) =>
            throw error;
    }

    private sealed class WrongTypeConverter : OpcUaValueConverter
    {
        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property) => 0;
    }

    private sealed class GatedThrowingConverter : OpcUaValueConverter, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal InvalidOperationException Error { get; } = new("shutdown conversion failed");

        public override object? ConvertToPropertyValue(object? nodeValue, RegisteredSubjectProperty property)
        {
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the gated converter.");
            }

            throw Error;
        }

        internal void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
