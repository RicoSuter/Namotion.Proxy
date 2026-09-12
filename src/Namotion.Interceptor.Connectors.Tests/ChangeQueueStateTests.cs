using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueStateTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void WhenTheBufferOverflows_ThenOnlyTheOldestChangesAreDropped()
    {
        // Arrange
        var reportedDrops = 0L;
        var state = CreateState(maxQueueDepth: 2, dropHandler: count => reportedDrops += count);
        var changes = CreateChanges("first", "second", "third");
        var buffer = new List<SubjectPropertyChange> { changes[0] };

        // Act
        foreach (var change in changes)
        {
            state.Enqueue(change);
        }
        state.DrainBufferedChangesInto(buffer);

        // Assert
        Assert.Equal(new[] { "second", "third" }, buffer.Select(change => change.GetNewValue<string>()));
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(1, state.DropCount);
        Assert.Equal(1, reportedDrops);
    }

    [Fact]
    public void WhenTheBufferIsUnbounded_ThenEveryChangeIsRetainedInOrder()
    {
        // Arrange
        var state = CreateState();
        var changes = CreateChanges("first", "second", "third");
        var buffer = new List<SubjectPropertyChange>();

        // Act
        foreach (var change in changes)
        {
            state.Enqueue(change);
        }
        state.DrainBufferedChangesInto(buffer);

        // Assert
        Assert.Equal(new[] { "first", "second", "third" }, buffer.Select(change => change.GetNewValue<string>()));
        Assert.Equal(0, state.DropCount);
    }

    [Fact]
    public void WhenDeliveryCompletes_ThenAnotherDeliveryCanBeginWithoutDrops()
    {
        // Arrange
        var state = CreateState();
        Assert.True(state.TryBeginDeliveryCallback!(2));

        // Act
        state.CompleteDelivery(2);
        var nextDeliveryStarted = state.TryBeginDeliveryOrCountAsDropped(1);
        state.CompleteDelivery(1);
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.True(nextDeliveryStarted);
        Assert.Equal(0, state.DropCount);
    }

    [Fact]
    public void WhenAnotherDeliveryIsActive_ThenRejectionDoesNotReplaceItsOwnership()
    {
        // Arrange
        var state = CreateState();
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));

        // Act
        var nextDeliveryStarted = state.TryBeginDeliveryOrCountAsDropped(3);
        state.CompleteDelivery(2);
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.False(nextDeliveryStarted);
        Assert.Equal(3, state.DropCount);
    }

    [Fact]
    public void WhenDeliveryFails_ThenItIsReportedOnceAndAnotherDeliveryCanBegin()
    {
        // Arrange
        var reportedDrops = 0L;
        var state = CreateState(dropHandler: count => reportedDrops += count);
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));

        // Act
        var failureCompleted = state.TryCompleteFailedDelivery(2);
        var repeatedFailureCompleted = state.TryCompleteFailedDelivery(2);
        var nextDeliveryStarted = state.TryBeginDeliveryOrCountAsDropped(1);
        state.CompleteDelivery(1);
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.True(failureCompleted);
        Assert.False(repeatedFailureCompleted);
        Assert.True(nextDeliveryStarted);
        Assert.Equal(2, state.DropCount);
        Assert.Equal(2, reportedDrops);
    }

    [Fact]
    public void WhenCancelledDeliveryIsRequeued_ThenAllItsChangesSurviveForRetry()
    {
        // Arrange
        var state = CreateState(maxQueueDepth: 1);
        var changes = CreateChanges("first", "second");
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));
        var buffer = new List<SubjectPropertyChange>();

        // Act
        state.RequeueCancelledDelivery(changes, 2);
        state.DrainBufferedChangesInto(buffer);
        var retryStarted = state.TryBeginDeliveryOrCountAsDropped(2);
        state.CompleteDelivery(2);
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.Equal(new[] { "first", "second" }, buffer.Select(change => change.GetNewValue<string>()));
        Assert.True(retryStarted);
        Assert.Equal(0, state.DropCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenClosureAndCancellationOccurInEitherOrder_ThenTheBatchIsCountedOnce(bool closeFirst)
    {
        // Arrange
        var state = CreateState();
        var changes = CreateChanges("first", "second");
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));

        // Act
        if (closeFirst)
        {
            state.CloseAndCountRemainingAsDropped();
            state.RequeueCancelledDelivery(changes, 2);
        }
        else
        {
            state.RequeueCancelledDelivery(changes, 2);
            state.CloseAndCountRemainingAsDropped();
        }
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(2, state.DropCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenClosureAndFailureOccurInEitherOrder_ThenTheBatchIsCountedOnce(bool closeFirst)
    {
        // Arrange
        var state = CreateState();
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));
        bool failureCompleted;

        // Act
        if (closeFirst)
        {
            state.CloseAndCountRemainingAsDropped();
            failureCompleted = state.TryCompleteFailedDelivery(2);
        }
        else
        {
            failureCompleted = state.TryCompleteFailedDelivery(2);
            state.CloseAndCountRemainingAsDropped();
        }
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.Equal(!closeFirst, failureCompleted);
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(2, state.DropCount);
    }

    [Fact]
    public void WhenDeliveryCompletesAfterClosure_ThenDeliveryCannotRestart()
    {
        // Arrange
        var state = CreateState();
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));
        state.CloseAndCountRemainingAsDropped();

        // Act
        state.CompleteDelivery(2);
        var nextDeliveryStarted = state.TryBeginDeliveryOrCountAsDropped(1);

        // Assert
        Assert.False(nextDeliveryStarted);
        Assert.Equal(3, state.DropCount);
    }

    [Fact]
    public async Task WhenClosedRepeatedly_ThenBufferedAndActiveChangesAreReportedOnce()
    {
        // Arrange
        var dropReport = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = CreateState(dropHandler: count => dropReport.TrySetResult(count));
        var changes = CreateChanges("buffered first", "buffered second");
        foreach (var change in changes)
        {
            state.Enqueue(change);
        }
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(3));

        // Act
        state.CloseAndCountRemainingAsDropped();
        state.CloseAndCountRemainingAsDropped();
        var reportedDrops = await dropReport.Task.WaitAsync(TestTimeout);

        // Assert
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(5, state.DropCount);
        Assert.Equal(5, reportedDrops);
    }

    [Fact]
    public void WhenClosedBetweenDrainAndDelivery_ThenTheDrainedBatchIsCountedOnRejection()
    {
        // Arrange
        var state = CreateState();
        foreach (var change in CreateChanges("first", "second"))
        {
            state.Enqueue(change);
        }
        var buffer = new List<SubjectPropertyChange>();
        state.DrainBufferedChangesInto(buffer);

        // Act
        state.CloseAndCountRemainingAsDropped();
        var deliveryStarted = state.TryBeginDeliveryCallback!(buffer.Count);

        // Assert
        Assert.False(deliveryStarted);
        Assert.Equal(2, state.DropCount);
    }

    [Fact]
    public void WhenTheHandlerOwnsDelivery_ThenOnlyBufferedChangesAreCountedOnClosure()
    {
        // Arrange
        var state = CreateState(tracksDeliveryOutcomes: false);
        var changes = CreateChanges("handed off", "still buffered");
        var buffer = new List<SubjectPropertyChange>();
        state.Enqueue(changes[0]);
        state.DrainBufferedChangesInto(buffer);
        state.Enqueue(changes[1]);

        // Act
        state.CloseAndCountRemainingAsDropped();

        // Assert
        Assert.Null(state.TryBeginDeliveryCallback);
        Assert.Equal("handed off", Assert.Single(buffer).GetNewValue<string>());
        Assert.Equal(1, state.DropCount);
        Assert.Equal(0, state.BufferedCount);
    }

    [Fact]
    public async Task WhenClosureRacesCancellation_ThenTheBatchIsCountedOnceWithoutRequeueingAfterClosure()
    {
        // Arrange
        var state = CreateState();
        var changes = CreateChanges("first", "second");
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));

        // Act
        await RunConcurrentlyAsync(state.CloseAndCountRemainingAsDropped, () => state.RequeueCancelledDelivery(changes, 2));

        // Assert
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(2, state.DropCount);
    }

    [Fact]
    public async Task WhenClosureRacesFailure_ThenTheBatchIsCountedOnce()
    {
        // Arrange
        var state = CreateState();
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));

        // Act
        await RunConcurrentlyAsync(state.CloseAndCountRemainingAsDropped, () => state.TryCompleteFailedDelivery(2));

        // Assert
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(2, state.DropCount);
    }

    [Fact]
    public async Task WhenClosuresRace_ThenBufferedAndActiveChangesAreCountedOnce()
    {
        // Arrange
        var state = CreateState();
        state.Enqueue(CreateChanges("buffered")[0]);
        Assert.True(state.TryBeginDeliveryOrCountAsDropped(2));

        // Act
        await RunConcurrentlyAsync(state.CloseAndCountRemainingAsDropped, state.CloseAndCountRemainingAsDropped);

        // Assert
        Assert.Equal(0, state.BufferedCount);
        Assert.Equal(3, state.DropCount);
    }

    private static ChangeQueueState CreateState(
        int? maxQueueDepth = null,
        Action<long>? dropHandler = null,
        bool tracksDeliveryOutcomes = true) =>
        new(maxQueueDepth, dropHandler, NullLogger.Instance, tracksDeliveryOutcomes);

    private static SubjectPropertyChange[] CreateChanges(params string[] values)
    {
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));
        return values.Select(value => SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UnixEpoch, null, string.Empty, value)).ToArray();
    }

    private static async Task RunConcurrentlyAsync(Action first, Action second)
    {
        using var start = new Barrier(2);
        Task Start(Action action) => Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(TestTimeout))
            {
                throw new TimeoutException("The competing delivery operation did not start.");
            }
            action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Task.WhenAll(Start(first), Start(second)).WaitAsync(TestTimeout);
    }
}
