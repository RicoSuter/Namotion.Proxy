using Moq;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceExtensionsTests
{
    [Fact]
    public async Task WriteChangesInBatchesAsync_EmptyChanges_ReturnsSuccess()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        var changes = ReadOnlyMemory<SubjectPropertyChange>.Empty;

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Empty(result.FailedChanges);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_SingleBatchWhenCountLessThanBatchSize_DelegatesDirectly()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(5);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(3);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_SingleBatchWhenCountEqualsBatchSize_DelegatesDirectly()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(3);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(3);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_MultipleBatches_SplitsCorrectly()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var batchSizes = new List<int>();
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                batchSizes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Equal(3, batchSizes.Count); // 2 + 2 + 1
        Assert.Equal(2, batchSizes[0]);
        Assert.Equal(2, batchSizes[1]);
        Assert.Equal(1, batchSizes[2]);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_MultipleBatches_StopsOnFirstFailure()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 2) // Second batch fails
                {
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Batch 2 failed")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Equal(2, callCount); // Should stop after second batch fails
        Assert.True(result.FailedChanges.Length > 0); // Remaining changes are marked as failed
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_BatchSizeZero_TreatedAsSingleBatch()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(10);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_NegativeBatchSize_TreatedAsSingleBatch()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(-1);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(10);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_FirstBatchPartialFailure_ReturnsCorrectRemainingChanges()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var changes = CreateChanges(5);
        var failedChange = changes.Span[1]; // Second item in first batch fails

        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                // First batch returns partial failure (1 item failed)
                return new ValueTask<WriteResult>(WriteResult.PartialFailure(
                    new[] { failedChange },
                    new Exception("Partial failure")));
            });

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.True(result.FailedChanges.Length >= 1); // At least the partial failure + remaining
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_AllBatchesSucceed_ReturnsSuccess()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(3);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(9); // 3 batches of 3

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Empty(result.FailedChanges);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_CompleteBatchFailure_ReturnsAllRemainingAsFailed()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> batchChanges, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1) // First batch completely fails
                {
                    return new ValueTask<WriteResult>(WriteResult.Failure(batchChanges, new Exception("Complete batch failure")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Equal(1, callCount); // Should stop after first batch fails
        Assert.Equal(5, result.FailedChanges.Length); // All 5 changes should be marked as failed
    }

    [Fact]
    public async Task WhenSingleBatchFailsWithoutEnumeratedFailedChanges_ThenAllChangesAreReportedFailed()
    {
        // Arrange: the source fails wholesale (error, no enumerated failed changes) on the
        // single-batch fast path.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(
                    ReadOnlyMemory<SubjectPropertyChange>.Empty, new InvalidOperationException("Wholesale boom"))));

        var changes = CreateChanges(3);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the choke point normalizes the unattributed error into "the whole batch failed".
        Assert.NotNull(result.Error);
        Assert.Equal("Wholesale boom", result.Error!.Message);
        Assert.Equal(3, result.FailedChanges.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal($"Property{i}", result.FailedChanges[i].Property.Name);
        }
    }

    [Fact]
    public async Task WhenSourceThrowsOnSecondBatch_ThenConfirmedFirstBatchIsNotReportedFailed()
    {
        // Arrange: 5 changes, batch size 2. The first batch [0,1] is confirmed written,
        // the second batch [2,3] throws.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 2)
                {
                    throw new InvalidOperationException("Batch 2 boom");
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the confirmed first batch counts as written; failed = the throwing batch
        // (outcome unknown) plus the unprocessed remainder.
        Assert.NotNull(result.Error);
        Assert.Equal("Batch 2 boom", result.Error!.Message);
        Assert.True(result.IsPartialFailure);
        Assert.Equal(3, result.FailedChanges.Length);
        var failedNames = result.FailedChanges.Select(change => change.Property.Name).ToArray();
        Assert.DoesNotContain("Property0", failedNames);
        Assert.DoesNotContain("Property1", failedNames);
        Assert.Contains("Property2", failedNames);
        Assert.Contains("Property3", failedNames);
        Assert.Contains("Property4", failedNames);
    }

    [Fact]
    public async Task WhenFirstItemOfBatchFails_ThenFailedChangesContainTheActualFailedChange()
    {
        // Arrange: 6 changes, batch size 3. The first batch [0,1,2] fails item 0 only;
        // items 1 and 2 are written.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(3);

        var changes = CreateChanges(6);
        var failedChange = changes.Span[0];

        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.PartialFailure(
                    new[] { failedChange }, new InvalidOperationException("Item 0 failed"))));

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: failed = the actually failed change plus the unprocessed remainder [3,4,5];
        // the written items 1 and 2 must not be reported as failed.
        Assert.NotNull(result.Error);
        Assert.Equal(4, result.FailedChanges.Length);
        var failedNames = result.FailedChanges.Select(change => change.Property.Name).ToArray();
        Assert.Contains("Property0", failedNames);
        Assert.DoesNotContain("Property1", failedNames);
        Assert.DoesNotContain("Property2", failedNames);
        Assert.Contains("Property3", failedNames);
        Assert.Contains("Property4", failedNames);
        Assert.Contains("Property5", failedNames);
    }

    /// <summary>
    /// Records a new maximum seen by several callers at once. A plain read-modify-write loses updates
    /// when the writes overlap, which is the very situation these tests manufacture: the concurrent case
    /// can then observe two of its three overlapping calls and fail for no reason, and the serialized
    /// case can drop the evidence that serialization leaked.
    /// </summary>
    private static void RecordMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_RegularSource_SerializesWrites()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken ct) =>
            {
                var current = Interlocked.Increment(ref concurrentCalls);
                RecordMaximum(ref maxConcurrentCalls, current);

                // Wait a bit to allow potential concurrent calls to overlap
                await Task.Delay(50, ct);

                Interlocked.Decrement(ref concurrentCalls);
                return WriteResult.Success;
            });

        var changes = CreateChanges(1);

        // Act - Start multiple concurrent writes
        var tasks = new[]
        {
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        await Task.WhenAll(tasks);

        // Assert - Regular source should serialize writes (max 1 concurrent)
        Assert.Equal(1, maxConcurrentCalls);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_ConcurrentSource_AllowsConcurrentWrites()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var source = new ConcurrentTestSource(async () =>
        {
            var current = Interlocked.Increment(ref concurrentCalls);
            RecordMaximum(ref maxConcurrentCalls, current);

            // Signal when all 3 calls have started
            if (current >= 3)
            {
                allStarted.TrySetResult();
            }

            // Wait for signal to continue
            await canContinue.Task;

            Interlocked.Decrement(ref concurrentCalls);
            return WriteResult.Success;
        });

        var changes = CreateChanges(1);

        // Act - Start multiple concurrent writes
        var tasks = new[]
        {
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        // Wait for all three calls to be in flight, and fail here if they never are. Letting a bounded
        // wait expire silently opens the gate below while the calls are still queued, and the assertion
        // then fails because they ran one after another rather than because the source refused to run
        // them together.
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Allow all to complete
        canContinue.SetResult();
        await Task.WhenAll(tasks);

        // Assert - Concurrent source should allow concurrent writes (max 3)
        Assert.Equal(3, maxConcurrentCalls);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_CancellationDuringSemaphoreWait_ReturnsFailure()
    {
        // Arrange
        var blockingSource = new BlockingTestSource();
        var changes = CreateChanges(1);

        // Start a blocking write
        var blockingTask = blockingSource.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Try to start another write with a cancelled token
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var result = await blockingSource.WriteChangesInBatchesAsync(changes, cts.Token);

        // Assert - Should return failure instead of throwing
        Assert.NotNull(result.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);

        // Cleanup
        blockingSource.UnblockWrite();
        await blockingTask;
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

    /// <summary>
    /// Shared ISubjectSource member implementation for the two hand-rolled test doubles below,
    /// which differ only in RootSubject and WriteChangesAsync. Neither exercises state transitions,
    /// so State is fixed at Synchronizing for the lifetime of the double.
    /// </summary>
    private abstract class StateTrackingTestSource : ISubjectSource
    {
        public IInterceptorSubject RootSubject => throw new NotSupportedException();
        public int WriteBatchSize => 0;
        public abstract ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);
        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => Task.FromResult<Action?>(null);

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
    }

    /// <summary>
    /// Test source that implements ISupportsConcurrentWrites to opt-out of automatic synchronization.
    /// </summary>
    private sealed class ConcurrentTestSource(Func<Task<WriteResult>> writeCallback)
        : StateTrackingTestSource, ISupportsConcurrentWrites
    {
        public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => await writeCallback();
    }

    /// <summary>
    /// Test source that blocks on write until explicitly unblocked.
    /// </summary>
    private sealed class BlockingTestSource : StateTrackingTestSource
    {
        private readonly TaskCompletionSource _writeStarted = new();
        private readonly TaskCompletionSource _canComplete = new();

        public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            _writeStarted.TrySetResult();
            await _canComplete.Task;
            return WriteResult.Success;
        }

        public void UnblockWrite() => _canComplete.TrySetResult();
    }
}
