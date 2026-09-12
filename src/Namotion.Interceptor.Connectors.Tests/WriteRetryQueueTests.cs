using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class WriteRetryQueueTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenRetryOwnershipIsDisposedWithACurrentWriteWaiting_ThenBothCompleteAndAreCountedOnce()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
        var source = new Mock<ISubjectSource>();
        var olderWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlderWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiterCancellation = new CancellationTokenSource();
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                if (changes.Span[0].GetOldValue<int>() == 0)
                {
                    olderWriteStarted.TrySetResult();
                    await releaseOlderWrite.Task.ConfigureAwait(false);
                }
                else
                {
                    currentWriteStarted.TrySetResult();
                }

                return WriteResult.Success;
            });
        queue.Enqueue(CreateChanges(1, startId: 0));
        var olderFlush = queue.FlushAsync(source.Object, CancellationToken.None).AsTask();
        await olderWriteStarted.Task.WaitAsync(TestTimeout);
        var currentWrite = queue.WriteAsync(
            source.Object,
            CreateChanges(1, startId: 1),
            waiterCancellation.Token).AsTask();

        try
        {
            // Act
            queue.Dispose();
            releaseOlderWrite.TrySetResult();
            await olderFlush.WaitAsync(TestTimeout);
            await AsyncTestHelpers.WaitUntilAsync(
                () => currentWrite.IsCompleted,
                timeout: TestTimeout,
                message: "The registered waiter should complete after the semaphore holder exits.");
            await currentWrite;

            // Assert
            Assert.False(currentWriteStarted.Task.IsCompleted);
            Assert.Equal(2, diagnostics.TotalDropped);
        }
        finally
        {
            releaseOlderWrite.TrySetResult();
            await waiterCancellation.CancelAsync();
            await Task.WhenAll(olderFlush, currentWrite).WaitAsync(TestTimeout);
            queue.Dispose();
        }
    }

    [Fact]
    public async Task WhenACurrentPartialFailureSettlesBeforeRetirement_ThenOnlyFailedChangesAreCounted()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        using var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
        var source = new Mock<ISubjectSource>();
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.PartialFailure(
                    changes.Slice(1, 1),
                    new InvalidOperationException("One change failed"))));

        // Act
        await queue.WriteAsync(source.Object, CreateChanges(3), CancellationToken.None);
        queue.Retire();

        // Assert
        Assert.Equal(1, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenACurrentWriteFails_ThenTheFailureIsLoggedAndRetained()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var logger = new RecordingLogger();
        using var queue = new WriteRetryQueue(100, logger, metrics);
        var source = new Mock<ISubjectSource>();
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(
                    changes,
                    new InvalidOperationException("Connection failed"))));

        // Act
        await queue.WriteAsync(source.Object, CreateChanges(1), CancellationToken.None);

        // Assert
        Assert.Equal(1, queue.PendingWriteCount);
        Assert.Contains(logger.Warnings, warning => warning.Contains("Failed to write 1 changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheQueueIsRetiredWhileAFlushIsInFlight_ThenTheBatchIsCountedExactlyOnce()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        using var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
        var source = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                writeStarted.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
                return WriteResult.Success;
            });
        queue.Enqueue(CreateChanges(3));
        var flush = queue.FlushAsync(source.Object, CancellationToken.None);
        await writeStarted.Task.WaitAsync(TestTimeout);

        // Act
        queue.Retire();

        // Assert
        Assert.Equal(3, diagnostics.TotalDropped);
        releaseWrite.TrySetResult();
        Assert.True(await flush.AsTask().WaitAsync(TestTimeout));
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenRetireIsCalledTwice_ThenPendingAndActiveWritesAreCountedOnce()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        using var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
        var source = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                writeStarted.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
                return WriteResult.Success;
            });
        queue.Enqueue(CreateChanges(1));
        var flush = queue.FlushAsync(source.Object, CancellationToken.None);
        await writeStarted.Task.WaitAsync(TestTimeout);
        queue.Enqueue(CreateChanges(2));

        // Act
        queue.Retire();
        queue.Retire();

        // Assert
        Assert.Equal(0, queue.PendingWriteCount);
        Assert.Equal(3, diagnostics.TotalDropped);
        releaseWrite.TrySetResult();
        Assert.True(await flush.AsTask().WaitAsync(TestTimeout));
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenAWriteIsEnqueuedAfterRetirement_ThenItIsCountedWithoutEnteringTheQueue()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        using var queue = new WriteRetryQueue(100, NullLogger.Instance, metrics);
        queue.Retire();

        // Act
        queue.Enqueue(CreateChanges(3));

        // Assert
        Assert.Equal(0, queue.PendingWriteCount);
        Assert.Equal(3, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenAFailingFlushSettlesAfterRetirement_ThenItIsNotRequeuedOrCountedAgain()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        var logger = new RecordingLogger();
        using var queue = new WriteRetryQueue(100, logger, metrics);
        var source = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writeStarted.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
                return WriteResult.Failure(changes, new InvalidOperationException("Connection failed"));
            });
        queue.Enqueue(CreateChanges(3));
        var flush = queue.FlushAsync(source.Object, CancellationToken.None);
        await writeStarted.Task.WaitAsync(TestTimeout);
        queue.Retire();

        // Act
        releaseWrite.TrySetResult();
        var result = await flush.AsTask().WaitAsync(TestTimeout);

        // Assert
        Assert.False(result);
        Assert.Equal(0, queue.PendingWriteCount);
        Assert.Equal(3, diagnostics.TotalDropped);
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("re-queuing failed items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenASuccessfulFlushSettlesAfterRetirement_ThenItDoesNotReportRecovery()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var logger = new RecordingLogger();
        using var queue = new WriteRetryQueue(100, logger, metrics);
        var source = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeAttempt = 0;
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref writeAttempt) == 1)
                {
                    return WriteResult.Failure(changes, new InvalidOperationException("Connection failed"));
                }

                writeStarted.TrySetResult();
                await releaseWrite.Task.ConfigureAwait(false);
                return WriteResult.Success;
            });
        queue.Enqueue(CreateChanges(3));
        Assert.False(await queue.FlushAsync(source.Object, CancellationToken.None));
        var flush = queue.FlushAsync(source.Object, CancellationToken.None);
        await writeStarted.Task.WaitAsync(TestTimeout);

        // Act
        queue.Retire();
        releaseWrite.TrySetResult();
        var result = await flush.AsTask().WaitAsync(TestTimeout);

        // Assert
        Assert.True(result);
        Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("Successfully flushed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenFlushFails_ThenWarningReportsResultingQueueDepth()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var logger = new RecordingLogger();
        using var queue = new WriteRetryQueue(100, logger, metrics);
        var source = new Mock<ISubjectSource>();
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException("Connection failed"))));
        queue.Enqueue(CreateChanges(3));

        // Act
        var result = await queue.FlushAsync(source.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Contains(logger.Warnings, warning => warning.Contains("3 writes queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenEnqueueAndFlush_ThenChangesAreWritten()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writtenChanges = changes.ToArray();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.NotNull(writtenChanges);
        Assert.Equal(3, writtenChanges.Length);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task WhenQueueIsEmpty_ThenFlushReturnsTrue()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        // Act
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        sourceMock.Verify(
            c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenQueueAppearsEmptyDuringCurrentWrite_ThenFlushWaitsForCurrentOwner()
    {
        // Arrange
        using var queue = new WriteRetryQueue(
            100,
            NullLogger.Instance,
            new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var source = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeAttempts = 0;
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref writeAttempts) == 1)
                {
                    writeStarted.TrySetResult();
                    await releaseWrite.Task.ConfigureAwait(false);
                    return WriteResult.Failure(changes, new InvalidOperationException("retry current write"));
                }

                return WriteResult.Success;
            });
        var currentWrite = queue.WriteAsync(
            source.Object,
            CreateChanges(1),
            CancellationToken.None).AsTask();
        await writeStarted.Task.WaitAsync(TestTimeout);

        // Act
        var flush = queue.FlushAsync(source.Object, CancellationToken.None).AsTask();

        try
        {
            // Assert
            Assert.False(flush.IsCompleted);
            releaseWrite.TrySetResult();
            await currentWrite.WaitAsync(TestTimeout);
            Assert.True(await flush.WaitAsync(TestTimeout));
            Assert.Equal(2, writeAttempts);
            Assert.True(queue.IsEmpty);
        }
        finally
        {
            releaseWrite.TrySetResult();
            await Task.WhenAll(currentWrite, flush).WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task WhenQueueIsEmpty_ThenFlushDoesNotGrowTheScratchBuffer()
    {
        // Arrange
        using var queue = new WriteRetryQueue(
            100,
            NullLogger.Instance,
            new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var source = new Mock<ISubjectSource>();
        var initialBuffer = GetScratchBuffer(queue);

        // Act
        var result = await queue.FlushAsync(source.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.Same(initialBuffer, GetScratchBuffer(queue));
    }

    [Fact]
    public async Task WhenCurrentWriteHasNoOlderRetries_ThenScratchBufferIsNotGrown()
    {
        // Arrange
        using var queue = new WriteRetryQueue(
            100,
            NullLogger.Instance,
            new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var source = new Mock<ISubjectSource>();
        source.Setup(item => item.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));
        var initialBuffer = GetScratchBuffer(queue);

        // Act
        await queue.WriteAsync(source.Object, CreateChanges(1), CancellationToken.None);

        // Assert
        Assert.Same(initialBuffer, GetScratchBuffer(queue));
    }

    [Fact]
    public async Task WhenQueueIsFull_ThenOldestAreDropped()
    {
        // Arrange
        var queue = new WriteRetryQueue(5, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writtenChanges = changes.ToArray();
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        queue.Enqueue(CreateChanges(3, startId: 0));  // Items 0, 1, 2
        queue.Enqueue(CreateChanges(4, startId: 10)); // Items 10, 11, 12, 13 -> should drop 0, 1

        // Assert
        Assert.Equal(5, queue.PendingWriteCount);

        // Flush and verify the actual items (2, 10, 11, 12, 13)
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        Assert.NotNull(writtenChanges);
        Assert.Equal(5, writtenChanges.Length);
        Assert.Equal(2, writtenChanges[0].GetOldValue<int>());   // Item 2
        Assert.Equal(10, writtenChanges[1].GetOldValue<int>());  // Item 10
        Assert.Equal(11, writtenChanges[2].GetOldValue<int>());  // Item 11
        Assert.Equal(12, writtenChanges[3].GetOldValue<int>());  // Item 12
        Assert.Equal(13, writtenChanges[4].GetOldValue<int>());  // Item 13
    }

    [Fact]
    public async Task WhenFlushFails_ThenChangesAreRequeued()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed"))));

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(3, queue.PendingWriteCount); // Re-queued
    }

    [Fact]
    public async Task WhenFlushFailsWithoutEnumeratedFailedChanges_ThenWholeBatchIsRequeued()
    {
        // Arrange: the source fails wholesale but does not enumerate the failed changes.
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(
                    ReadOnlyMemory<SubjectPropertyChange>.Empty, new Exception("Connection failed"))));

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(3, queue.PendingWriteCount); // the whole attempted batch is re-queued, not dropped
    }

    [Fact]
    public async Task WhenFlushFailsAtCapacity_ThenRequeueDoesNotDropItems()
    {
        // Arrange
        var queue = new WriteRetryQueue(5, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Connection failed"))));

        // Act
        queue.Enqueue(CreateChanges(5)); // Fill to capacity
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(5, queue.PendingWriteCount); // All items re-queued, none dropped
    }

    [Fact]
    public async Task WhenFailedInflightBatchIsRequeuedAfterNewWritesFillCapacity_ThenOldestFailedWritesAreDropped()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var diagnostics = new QueueDiagnostics(metrics);
        var queue = new WriteRetryQueue(2, NullLogger.Instance, metrics);
        var sourceMock = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sourceMock
            .Setup(source => source.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writeStarted.SetResult();
                await completeWrite.Task;
                return WriteResult.Failure(changes, new InvalidOperationException("Connection failed"));
            });

        queue.Enqueue(CreateChanges(2, startId: 0)); // A/B

        // Act
        var flush = queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        await writeStarted.Task;
        queue.Enqueue(CreateChanges(2, startId: 2)); // C/D
        Assert.Equal(2, queue.PendingWriteCount);
        completeWrite.SetResult();
        var result = await flush;
        var retained = await queue.DrainForLocalReapplyAsync(CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(2, retained.Length);
        Assert.Equal(2, retained[0].GetOldValue<int>());
        Assert.Equal(3, retained[1].GetOldValue<int>());
        Assert.Equal(2, diagnostics.TotalDropped);
    }

    [Fact]
    public void WhenMaxQueueSizeIsZero_ThenWritesAreDropped()
    {
        // Arrange
        var metrics = new QueueMetrics(nameof(SourceMetrics.OutboundRetries));
        var queue = new WriteRetryQueue(0, NullLogger.Instance, metrics);
        var diagnostics = new QueueDiagnostics(metrics);

        // Act
        queue.Enqueue(CreateChanges(5));

        // Assert
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
        Assert.Equal(5, diagnostics.TotalDropped);
    }

    [Fact]
    public async Task WhenManyItems_ThenFlushProcessesInBatches()
    {
        // Arrange
        var queue = new WriteRetryQueue(2000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var totalWritten = 0;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                totalWritten += changes.Length;
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act - enqueue more than MaxBatchSize (1024)
        queue.Enqueue(CreateChanges(1500));
        var result = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.Equal(1500, totalWritten);
        Assert.True(queue.IsEmpty);

        // Should have been called at least twice (1024 + 476)
        sourceMock.Verify(
            c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task WhenCancelled_ThenFlushReturnsFalse()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        queue.Enqueue(CreateChanges(3));

        // Act
        var result = await queue.FlushAsync(sourceMock.Object, cts.Token);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenMultipleFlushes_ThenOnlyOneRunsAtATime()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var callCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken ct) =>
            {
                Interlocked.Increment(ref callCount);
                await tcs.Task; // Block until released
                return WriteResult.Success;
            });

        queue.Enqueue(CreateChanges(3));

        // Act - start two flushes
        var flush1 = queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        var flush2 = queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        await AsyncTestHelpers.WaitUntilAsync(() => callCount >= 1);

        // Assert - only one should be running
        Assert.Equal(1, callCount);

        tcs.SetResult(); // Release the blocked flush
        await flush1;
        await flush2;
    }

    [Fact]
    public async Task WhenSourceBatchSizeSet_ThenWriteChangesInBatchesRespectsBatchSize()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var batchSizes = new List<int>();
        sourceMock.Setup(c => c.WriteBatchSize).Returns(2);
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                batchSizes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act
        queue.Enqueue(CreateChanges(5));
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert - WriteChangesInBatchesAsync should split into batches of 2
        Assert.Equal(3, batchSizes.Count); // 2 + 2 + 1
        Assert.Equal(2, batchSizes[0]);
        Assert.Equal(2, batchSizes[1]);
        Assert.Equal(1, batchSizes[2]);
    }

    [Fact]
    public async Task WhenConcurrentEnqueues_ThenAllItemsAreQueued()
    {
        // Arrange
        var queue = new WriteRetryQueue(10000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var tasks = new List<Task>();

        // Act - enqueue from multiple threads
        for (var i = 0; i < 10; i++)
        {
            var batch = i;
            tasks.Add(Task.Run(() => queue.Enqueue(CreateChanges(100, startId: batch * 100))));
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1000, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenFlushSucceeds_ThenQueueIsEmpty()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        // Act
        queue.Enqueue(CreateChanges(10));
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert - queue should be empty after successful flush
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenExactlyMaxBatchSizeItems_ThenAllItemsAreFlushed()
    {
        // Arrange
        var queue = new WriteRetryQueue(2000, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();

        var totalWritten = 0;
        sourceMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                totalWritten += changes.Length;
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        // Act - enqueue exactly MaxBatchSize (1024)
        queue.Enqueue(CreateChanges(1024));
        await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert
        Assert.Equal(1024, totalWritten);
        Assert.True(queue.IsEmpty);
    }
    
    [Fact]
    public async Task WhenDrainForLocalReapply_ThenReturnsAllItemsAndClearsQueue()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        queue.Enqueue(CreateChanges(5));
        Assert.Equal(5, queue.PendingWriteCount);

        // Act
        var drained = await queue.DrainForLocalReapplyAsync(CancellationToken.None);

        // Assert
        Assert.Equal(5, drained.Length);
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenDrainForLocalReapplyOnEmptyQueue_ThenReturnsEmptyArray()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));

        // Act
        var drained = await queue.DrainForLocalReapplyAsync(CancellationToken.None);

        // Assert
        Assert.Empty(drained);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task WhenDrainForLocalReapplyRunsWhileAFlushIsInFlight_ThenTheDrainWaitsForTheFlush()
    {
        // Arrange: a flush that blocks mid-write, so the drain started while it is still holding the
        // flush semaphore must wait rather than run concurrently with it.
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sourceMock
            .Setup(source => source.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                writeStarted.SetResult();
                await completeWrite.Task;
                return WriteResult.Success;
            });

        queue.Enqueue(CreateChanges(1));

        // Act
        var flush = queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        await writeStarted.Task;

        // The flush is blocked inside the write handler, still holding the flush semaphore, so a
        // WaitAsync against it cannot complete until that semaphore is released, whatever the scheduler
        // does next: no delay is needed to observe this.
        var drainTask = queue.DrainForLocalReapplyAsync(CancellationToken.None);
        Assert.False(drainTask.IsCompleted, "The drain must wait for the in-flight flush to release the semaphore.");

        completeWrite.SetResult();
        await flush;
        var drained = await drainTask;

        // Assert - the flush already took the only item, so the drain that waited for it finds nothing left
        Assert.Empty(drained);
    }

    [Fact]
    public async Task WhenAFlushThatWillSendItsOwnBatchRunsWhileAFlushIsInFlight_ThenItWaitsForTheFlush()
    {
        // Arrange: the queue reads empty from the moment a flush moves its batch into the scratch
        // buffer, well before that batch reaches the peer. A caller that goes on to send a newer batch
        // must not take that as "nothing is in flight", or the two batches race to the peer and the
        // older parked value can land after the newer one that supersedes it.
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var sourceMock = new Mock<ISubjectSource>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sourceMock
            .Setup(source => source.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                writeStarted.SetResult();
                await completeWrite.Task;
                return WriteResult.Success;
            });

        queue.Enqueue(CreateChanges(1));

        // Act - the idle drain takes the parked write and blocks inside the send, holding the gate
        var idleDrain = queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        await writeStarted.Task;

        var handlerFlush = queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert - the queue reads empty here, so only the gate can hold this back
        Assert.True(queue.IsEmpty);
        Assert.False(handlerFlush.IsCompleted,
            "A flush that will send its own batch must wait for the in-flight flush to land.");

        completeWrite.SetResult();
        Assert.True(await idleDrain);
        Assert.True(await handlerFlush);
    }

    private static SubjectPropertyChange CreateChange(int id)
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        return SubjectPropertyChange.Create(
            new PropertyReference(subjectMock.Object, $"Property{id}"),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            id,
            id + 1);
    }

    private static ReadOnlyMemory<SubjectPropertyChange> CreateChanges(int count, int startId = 0)
    {
        var changes = new SubjectPropertyChange[count];
        for (var i = 0; i < count; i++)
        {
            changes[i] = CreateChange(startId + i);
        }
        return changes;
    }

    private static SubjectPropertyChange[] GetScratchBuffer(WriteRetryQueue queue) =>
        (SubjectPropertyChange[])typeof(WriteRetryQueue)
            .GetField("_scratchBuffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(queue)!;
}
