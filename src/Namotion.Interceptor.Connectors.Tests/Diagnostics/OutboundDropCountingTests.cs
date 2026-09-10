using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

/// <summary>
/// Pins that every outbound path which discards a write counts it, and that the connector buffers
/// report their depth and their bound.
/// </summary>
public class OutboundDropCountingTests
{
    private const int OverflowChangeCount = 4;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TeardownWaitTimeout =
        ChangeQueueProcessor.TeardownFlushBound + TimeSpan.FromSeconds(5);

    [Fact]
    public void WhenTheRetryQueueOverflows_ThenTheDroppedWritesAreCounted()
    {
        // Arrange
        var metrics = new SourceMetrics();
        using var queue = new WriteRetryQueue(maxQueueSize: 2, NullLogger.Instance, metrics.OutboundRetries);
        var diagnostics = new SourceDiagnostics(metrics);

        // Act
        queue.Enqueue(CreateChanges(count: 5));

        // Assert
        Assert.Equal(3, diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenAQueuedWriteHasNoSetter_ThenItIsCountedAsDropped()
    {
        // Arrange: a write parked on a getter-only property, which the reconcile can neither restore
        // nor recognize as already current.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance);

        // Pins the no-setter branch, so the drop below cannot come from the catch beside it.
        Assert.Null(new PropertyReference(person, nameof(Person.FullName)).Metadata.SetValue);

        source.WriteRetryQueue.Enqueue(new[]
        {
            CreateChange(person, nameof(Person.FullName), oldValue: "old", newValue: "never-current")
        });

        // Act
        await source.ReconcileRetryQueueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenReconcileThrowsForAChange_ThenItIsCountedAsDropped()
    {
        // Arrange: a parked write whose restore throws.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var device = new ThrowingDevice(context)
        {
            ThrowingEnabled = true,
            ShouldThrow = _ => true
        };

        using var source = new TestSubjectSource(device, context, NullLogger.Instance);

        // Pins that a setter exists, so the drop below comes from the catch and not the no-setter branch.
        Assert.NotNull(new PropertyReference(device, nameof(ThrowingDevice.PropertyA)).Metadata.SetValue);

        source.WriteRetryQueue.Enqueue(new[]
        {
            CreateChange(device, nameof(ThrowingDevice.PropertyA), oldValue: false, newValue: true)
        });

        // Act
        await source.ReconcileRetryQueueAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public async Task WhenRetryCapacityIsZeroAndACurrentWriteFails_ThenTheChangeIsCountedAsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);

        var gate = new object();
        var failFirstName = false;
        var receivedWrites = new List<string>();

        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8), writeRetryQueueSize: 0)
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    SubjectPropertyChange[] failed = failFirstName
                        ? changes.ToArray().Where(change => change.Property.Name == nameof(Person.FirstName)).ToArray()
                        : [];

                    foreach (var change in changes.ToArray())
                    {
                        if (change.Property.Name != nameof(Person.FirstName) || failed.Length == 0)
                        {
                            receivedWrites.Add(change.Property.Name);
                        }
                    }

                    return failed.Length == 0
                        ? ValueTask.FromResult(WriteResult.Success)
                        : ValueTask.FromResult(WriteResult.Failure(failed, new InvalidOperationException("boom")));
                }
            }
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Re-written on each poll because writes captured before the connected phase are drained
            // rather than written, so a single write proves nothing about the pump.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Contains(nameof(Person.LastName));
                }
            }, message: "The pump did not reach the connected phase.");
            var droppedBeforeFailure = source.Diagnostics.OutboundRetries.TotalDropped;

            // Act
            lock (gate)
            {
                failFirstName = true;
            }

            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.TotalDropped > droppedBeforeFailure,
                message: "The discarded direct write was not counted.");

            // Assert
            Assert.Equal(droppedBeforeFailure + 1, source.Diagnostics.OutboundRetries.TotalDropped);
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Capacity);
            Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAWriteRemainsUnconfirmedAtTheStopDeadline_ThenItIsCountedAsDropped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithFullPropertyTracking();
        var person = new Person(context);

        var warmupWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockWrite = false;
        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8), writeRetryQueueSize: 0)
        {
            WriteChangesOverride = async (changes, _) =>
            {
                if (Volatile.Read(ref blockWrite) &&
                    changes.Span[0].Property.Name == nameof(Person.FirstName))
                {
                    writeStarted.TrySetResult();
                    await releaseWrite.Task.ConfigureAwait(false);
                    writeFinished.TrySetResult();
                }
                else
                {
                    warmupWritten.TrySetResult();
                }

                return WriteResult.Success;
            }
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var watchdogRegistration = watchdog.Token.Register(() => releaseWrite.TrySetResult());
        await source.StartAsync(CancellationToken.None);
        var probeValue = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.LastName = "probe" + probeValue++;
            return source.Diagnostics.OutboundChanges.Depth > 0;
        }, message: "The connected processor did not start buffering the warmup write.");
        await warmupWritten.Task.WaitAsync(TestTimeout);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundChanges.Depth == 0,
            message: "The warmup write did not leave the processor queue.");
        var droppedBeforeDeadline = source.Diagnostics.OutboundRetries.TotalDropped;

        Volatile.Write(ref blockWrite, true);
        person.FirstName = "unconfirmed";
        await writeStarted.Task.WaitAsync(TestTimeout);

        try
        {
            // Act
            await source.StopAsync(CancellationToken.None).WaitAsync(TeardownWaitTimeout);
        }
        finally
        {
            releaseWrite.TrySetResult();
        }

        await writeFinished.Task.WaitAsync(TestTimeout);
        await source.WriteRetryQueue.FlushAsync(source, CancellationToken.None).AsTask().WaitAsync(TestTimeout);

        // Assert
        Assert.Equal(droppedBeforeDeadline + 1, source.Diagnostics.OutboundRetries.TotalDropped);
        Assert.Equal(0, source.Diagnostics.OutboundRetries.Capacity);
        Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
    }

    [Fact]
    public void WhenTheDisabledQueueDrainRuns_ThenAnUnownedChangeIsNotCounted()
    {
        // Arrange: retry capacity is zero, and the source does not own the changed property.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance, writeRetryQueueSize: 0);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        person.FirstName = "unowned";
        Assert.True(subscription.Count > 0, "The change was not captured, so the drain would be a no-op.");

        // Act
        source.DrainOwnedWritesToRetryQueue(subscription);

        // Assert
        Assert.Equal(0, subscription.Count);
        Assert.Equal(0, source.Diagnostics.OutboundRetries.TotalDropped);
    }

    [Fact]
    public void WhenABufferedLoadIsSuperseded_ThenTheDiscardedUpdatesAreCounted()
    {
        // Arrange
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        using var source = new TestSubjectSource(
            new Person(context), context, NullLogger.Instance, writeRetryQueueSize: 0);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance, metrics.InboundBuffer);

        writer.StartBuffering();
        writer.Write(0, static _ => { });
        writer.Write(1, static _ => { });
        Assert.Equal(2, diagnostics.InboundBuffer.Depth);

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(2, diagnostics.InboundBuffer.TotalDropped);
        Assert.Equal(0, diagnostics.InboundBuffer.Depth);
    }

    [Fact]
    public async Task WhenTheProcessorIsRecreated_ThenTheAccumulatedDropCountSurvives()
    {
        // Arrange: the in-repo connectors all pass maxQueueDepth: null, so this drives QueueMetrics
        // and ChangeQueueProcessor directly rather than through a source.
        var metrics = new SourceMetrics();
        var diagnostics = new SourceDiagnostics(metrics);
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        using var first = CreateBoundedProcessor(
            context, maxQueueDepth: 1, dropHandler: metrics.OutboundChanges.AddDropped);
        var firstRegistration = metrics.OutboundChanges.Register(() => first.QueueDepth, capacity: 1);
        await OverflowAsync(first, person, tag: "one");
        var afterFirst = diagnostics.OutboundChanges.TotalDropped;

        // Act
        firstRegistration.Dispose();
        using var second = CreateBoundedProcessor(
            context, maxQueueDepth: 1, dropHandler: metrics.OutboundChanges.AddDropped);
        using var secondRegistration = metrics.OutboundChanges.Register(() => second.QueueDepth, capacity: 1);
        await OverflowAsync(second, person, tag: "two");

        // Assert
        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst * 2, diagnostics.OutboundChanges.TotalDropped);
    }

    [Fact]
    public async Task WhenASourceIsRunning_ThenItsOutboundChangeQueueIsRegisteredAsUnbounded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = CreateSourceWithStalledOutboundQueue(person, context);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Act
            await WaitForBufferedOutboundChangeAsync(source, person);

            // Assert
            // An unregistered QueueMetrics reports a null capacity too, so the capacity below only
            // means "registered as unbounded" once the depth has proven the registration is live.
            Assert.True(source.Diagnostics.OutboundChanges.Depth > 0);
            Assert.Null(source.Diagnostics.OutboundChanges.Capacity);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenARunningSourceStops_ThenItsOutboundChangeQueueRegistrationIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = CreateSourceWithStalledOutboundQueue(person, context);

        await source.StartAsync(CancellationToken.None);
        await WaitForBufferedOutboundChangeAsync(source, person);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        // Nothing drains the processor's queue on the way out, so a non-zero depth here would mean
        // the depth provider outlived the processor.
        Assert.Equal(0, source.Diagnostics.OutboundChanges.Depth);
    }

    [Fact]
    public async Task WhenWritesAreParkedInTheRetryQueue_ThenTheOutboundRetriesDepthReportsThem()
    {
        // Arrange: every write fails, so the connected phase parks the changes in the retry queue.
        var context = InterceptorSubjectContext.Create().WithRegistry().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) => ValueTask.FromResult(
                WriteResult.Failure(changes, new InvalidOperationException("boom")))
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Act: the observed depth is captured, because the retry loop can drain the queue again
            // before the assertion reads it.
            var probeValue = 0;
            var observedDepth = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.FirstName = "v" + probeValue++;
                observedDepth = source.Diagnostics.OutboundRetries.Depth;
                return observedDepth > 0;
            }, message: "Parked writes were not reported by the registered depth provider.");

            // Assert
            Assert.True(observedDepth > 0);
            Assert.Equal(1000, source.Diagnostics.OutboundRetries.Capacity);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A source whose buffer time outlasts the test, so a captured change stays in the processor's
    /// queue instead of being flushed away before the depth can be read.
    /// </summary>
    private static TestSubjectSource CreateSourceWithStalledOutboundQueue(
        Person person, IInterceptorSubjectContext context)
    {
        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMinutes(5));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        return source;
    }

    private static Task WaitForBufferedOutboundChangeAsync(TestSubjectSource source, Person person)
    {
        // Re-written on each poll because writes captured before the connected phase are parked in
        // the retry queue rather than buffered by the processor.
        var probeValue = 0;
        return AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.FirstName = "v" + probeValue++;
            return source.Diagnostics.OutboundChanges.Depth > 0;
        }, message: "The outbound change queue never reported a depth.");
    }

    private static ChangeQueueProcessor CreateBoundedProcessor(
        IInterceptorSubjectContext context,
        int maxQueueDepth,
        Action<long> dropHandler)
    {
        // The long buffer time keeps anything from flushing the queue, so every change past the bound
        // overflows. The source sentinel must be non-null, or every local change would match the echo
        // check and be skipped.
        return new ChangeQueueProcessor(
            source: new object(),
            context,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            bufferTime: TimeSpan.FromMinutes(5),
            maxQueueDepth: maxQueueDepth,
            logger: NullLogger.Instance,
            dropHandler: dropHandler);
    }

    /// <summary>
    /// Commits <see cref="OverflowChangeCount"/> changes into a processor bounded at 1. Each change is
    /// on its own property and carries a value the model has not held before, so none supersedes
    /// another and the resulting drop count is exact rather than racy.
    /// </summary>
    private static async Task OverflowAsync(ChangeQueueProcessor processor, Person person, string tag)
    {
        using var cancellation = new CancellationTokenSource();

        // Started before the writes: ProcessAsync drops whatever the model has already moved past, so
        // earlier writes would not all reach the bounded queue.
        var processing = processor.ProcessAsync(cancellation.Token);

        person.FirstName = "a" + tag;
        person.LastName = "b" + tag;
        person.FirstName_MaxLength_Unit = "c" + tag;
        person.FirstName_MaxLength++;

        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.DropCount >= OverflowChangeCount - 1,
            message: "The bounded queue did not overflow.");

        await cancellation.CancelAsync();
        try
        {
            await processing;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // ProcessAsync may surface the requested cancellation when its timer task observes it first.
        }
    }

    private static SubjectPropertyChange CreateChange<TValue>(
        IInterceptorSubject subject, string propertyName, TValue oldValue, TValue newValue)
    {
        // Boxed rather than typed, because the reconcile reads every parked change as object.
        return SubjectPropertyChange.Create<object?>(
            new PropertyReference(subject, propertyName),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue);
    }

    private static ReadOnlyMemory<SubjectPropertyChange> CreateChanges(int count)
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        var changes = new SubjectPropertyChange[count];
        for (var i = 0; i < count; i++)
        {
            changes[i] = CreateChange<object?>(subjectMock.Object, $"Property{i}", i, i + 1);
        }

        return changes;
    }
}
