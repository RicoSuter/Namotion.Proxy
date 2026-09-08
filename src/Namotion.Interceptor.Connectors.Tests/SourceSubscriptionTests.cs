using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Pins the disposal contract documented on <see cref="SourceSubscription.Dispose"/>: an in-flight
/// handler is not interrupted by Dispose, and an event still queued (dequeued but not yet handled)
/// at the moment the drain loop observes disposal is dropped rather than delivered.
/// </summary>
public class SourceSubscriptionTests
{
    [Fact]
    public void WhenDisposedWhileAHandlerIsInFlight_ThenDisposeDoesNotWaitAndTheHandlerStillCompletes()
    {
        // Arrange
        var handlerEntered = new ManualResetEventSlim(false);
        var releaseHandler = new ManualResetEventSlim(false);
        var handlerCompleted = new ManualResetEventSlim(false);

        using var subscription = new SourceSubscription(
            _ =>
            {
                handlerEntered.Set();
                releaseHandler.Wait(TimeSpan.FromSeconds(10));
                handlerCompleted.Set();
            },
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            new SourceMonitor());

        subscription.Enqueue(CreateEvent());
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(10)));

        // Act
        subscription.Dispose();

        // Assert
        // Dispose does not block on the in-flight handler: at this point it is still parked on
        // releaseHandler, so if Dispose had waited for it, handlerCompleted could not be unset here.
        Assert.False(handlerCompleted.IsSet);
        releaseHandler.Set();
        Assert.True(handlerCompleted.Wait(TimeSpan.FromSeconds(10)),
            "An in-flight handler must run to completion even after Dispose() has returned.");
    }

    [Fact]
    public async Task WhenAnEventIsQueuedButNotYetDrainedAtDisposal_ThenItIsNeverDelivered()
    {
        // Arrange
        var deliveredCount = 0;
        var firstHandlerEntered = new ManualResetEventSlim(false);
        var releaseFirstHandler = new ManualResetEventSlim(false);

        var subscription = new SourceSubscription(
            _ =>
            {
                if (Interlocked.Increment(ref deliveredCount) == 1)
                {
                    firstHandlerEntered.Set();
                    releaseFirstHandler.Wait(TimeSpan.FromSeconds(10));
                }
            },
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            new SourceMonitor());

        subscription.Enqueue(CreateEvent());
        Assert.True(firstHandlerEntered.Wait(TimeSpan.FromSeconds(10)));

        // The second event sits in the internal queue, not yet drained, while the first handler
        // blocks inside the drain loop.
        subscription.Enqueue(CreateEvent());

        // Act
        subscription.Dispose();
        releaseFirstHandler.Set();

        // Assert
        // TryDequeue always removes an item from the internal queue before the disposed check
        // decides whether to deliver it, so "the queue is empty" is a race-free proxy for "the
        // drain loop has finished deciding the fate of every item that was ever enqueued" -
        // whether or not disposal caused any of them to be dropped along the way.
        await AsyncTestHelpers.WaitUntilAsync(() => IsInternalQueueEmpty(subscription));
        Assert.Equal(1, deliveredCount);
    }

    [Fact]
    public async Task WhenManyProducersEnqueueConcurrentlyWhileDraining_ThenEveryEventIsEventuallyDelivered()
    {
        // Arrange
        // Best-effort regression coverage for the drain handoff fixed by using Interlocked.Exchange
        // instead of Volatile.Write when clearing _draining (see Drain's comment on that line): the
        // actual store-load reordering defect reproduced only once in roughly 501 million aligned
        // attempts on ARM64, far beyond what a unit test can afford to run, so this cannot reliably
        // force that exact hardware race. It does exercise the same enqueue-during-drain-exit
        // handoff under heavy concurrency, many times over, which would also catch a logical
        // regression in the re-check loop (e.g. dropping the CompareExchange re-check, or reverting
        // to a plain read) - stranding an event here fails the test instead of hanging it, because
        // the wait below is bounded.
        const int producerCount = 8;
        const int eventsPerProducer = 20_000;
        const int totalEvents = producerCount * eventsPerProducer;

        var delivered = 0;
        using var allDelivered = new ManualResetEventSlim(false);

        var subscription = new SourceSubscription(
            _ =>
            {
                if (Interlocked.Increment(ref delivered) == totalEvents)
                {
                    allDelivered.Set();
                }
            },
            ImmutableArray<ISubjectSource>.Empty,
            _ => { },
            new SourceMonitor());

        var producers = Enumerable.Range(0, producerCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < eventsPerProducer; i++)
                {
                    subscription.Enqueue(CreateEvent());
                }
            }))
            .ToArray();

        // Act
        await Task.WhenAll(producers);

        // Assert
        Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(60)),
            $"Expected all {totalEvents} events to be delivered; only {Volatile.Read(ref delivered)} were - " +
            "an event was stranded.");

        subscription.Dispose();
    }

    [Fact]
    public void WhenTheDrainLoopClearsTheDrainingFlag_ThenItUsesInterlockedExchangeNotVolatileWrite()
    {
        // Arrange
        // The reordering the fence guards against (see Drain's comment on the Interlocked.Exchange
        // line) reproduces roughly once in 500 million aligned attempts, which is why the stress
        // test above states plainly that it cannot force that exact hardware race either: no
        // dynamic test in this suite can turn a regression to Volatile.Write into a reliable
        // failure within a feasible run time. What can be pinned instead is the API actually used
        // at that call site, so a "simplification" back to Volatile.Write is at least caught here,
        // even though the reordering it would reintroduce is not independently exercised by any test.
        var sourceFilePath = GetSourceSubscriptionFilePath();
        var source = File.ReadAllText(sourceFilePath);

        // Act & Assert
        Assert.Contains("Interlocked.Exchange(ref _draining, 0)", source);
        Assert.DoesNotContain("Volatile.Write(ref _draining", source);

        // Drain must actually run the handoff, not merely contain it: the two handoff tests drive
        // TryReacquireForPendingEvents directly, so deleting the loop that calls it leaves them
        // green. Nothing dynamic can reach that loop, so its presence is pinned here instead.
        Assert.Contains("while (TryReacquireForPendingEvents());", source);
    }

    private static string GetSourceSubscriptionFilePath([CallerFilePath] string testFilePath = "")
    {
        // CallerFilePath is resolved at this call's compile time, from this test file's own path -
        // resilient to whatever the test runner's current directory happens to be (bin/Debug/...),
        // unlike a path built from Environment.CurrentDirectory or the test assembly's location.
        var testDirectory = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(
            testDirectory, "..", "Namotion.Interceptor.Connectors", "Monitoring", "SourceSubscription.cs"));
    }

    private static bool IsInternalQueueEmpty(SourceSubscription subscription)
    {
        var field = typeof(SourceSubscription).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = (ConcurrentQueue<SourceEvent>)field.GetValue(subscription)!;
        return queue.IsEmpty;
    }

    private static SourceEvent CreateEvent()
    {
        var source = new TestStateSource(new Person());
        return new SourceEvent(
            SourceEventKind.SourceRegistered, source, null, SourceState.Synchronizing, SourceState.Synchronizing, DateTimeOffset.UtcNow);
    }
}

public class SourceSubscriptionHandoffTests
{
    [Fact]
    public void WhenTheQueueIsEmptyAtHandoff_ThenTheDrainReleasesAndStops()
    {
        // Arrange
        // The handoff is a few nanoseconds wide inside a running drain, so it is exercised directly:
        // deleting the whole re-check loop leaves every other test in the suite green. The flag is set
        // because that is the state a drain calls this in, and the release it performs is half of what
        // is under test.
        var subscription = CreateSubscription();
        MarkDraining(subscription);

        // Act
        var keepDraining = subscription.TryReacquireForPendingEvents();

        // Assert
        Assert.False(keepDraining);
        Assert.Equal(0, GetDrainingFlag(subscription));
    }

    [Fact]
    public void WhenAnEventArrivedDuringHandoff_ThenTheDrainReacquiresAndKeepsGoing()
    {
        // Arrange
        // This is the stranded-event case: a producer that enqueued while _draining was still 1
        // declined to schedule a new drain, so this thread must notice the queue is non-empty and
        // take the flag straight back. Without the re-check the event is never delivered.
        //
        // The flag has to be set before the enqueue, and that is the whole scenario rather than test
        // scaffolding: it is what makes the producer decline. Enqueueing with it clear instead has
        // Enqueue schedule a real drain, which empties the queue from the thread pool and races this
        // thread, so the assertion below could only hold while the pool was too busy to run it.
        var subscription = CreateSubscription();
        MarkDraining(subscription);
        subscription.Enqueue(CreateEvent());

        // Act
        var keepDraining = subscription.TryReacquireForPendingEvents();

        // Assert
        Assert.True(keepDraining);
        Assert.Equal(1, GetDrainingFlag(subscription));
    }

    private static SourceSubscription CreateSubscription() =>
        new(_ => { }, ImmutableArray<ISubjectSource>.Empty, _ => { }, new SourceMonitor());

    private static readonly FieldInfo DrainingField =
        typeof(SourceSubscription).GetField("_draining", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static int GetDrainingFlag(SourceSubscription subscription) =>
        (int)DrainingField.GetValue(subscription)!;

    /// <summary>
    /// Puts the subscription in the state the handoff runs in, a drain already in progress, which is
    /// the precondition both tests describe and neither could reach through the public surface.
    /// </summary>
    private static void MarkDraining(SourceSubscription subscription) =>
        DrainingField.SetValue(subscription, 1);

    private static SourceEvent CreateEvent() => new(
        SourceEventKind.SourceRegistered, new HandoffTestSource(), null,
        SourceState.Synchronizing, SourceState.Synchronizing, DateTimeOffset.UtcNow);

    private sealed class HandoffTestSource : ISubjectSource
    {
        public IInterceptorSubject RootSubject => throw new NotSupportedException();
        public int WriteBatchSize => 0;
        public SourceState State => SourceState.Synchronizing;
        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastSynchronizedAt => null;
        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());
        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

        public event EventHandler<SourceEvent>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => Task.FromResult<Action?>(null);

        public ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => new(WriteResult.Success);
    }
}
