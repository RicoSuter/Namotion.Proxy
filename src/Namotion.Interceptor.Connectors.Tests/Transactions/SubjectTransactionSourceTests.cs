using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for source integration: SetSource, TryGetSource, WriteChangesAsync, and multi-context scenarios.
/// </summary>
public class SubjectTransactionSourceTests : TransactionTestBase
{
    [Fact]
    public void WhenSetSourceCalled_ThenSourceReferenceIsStored()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        // Act
        property.SetSource(sourceMock);

        // Assert
        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(sourceMock, retrievedSource);
    }

    [Fact]
    public void WhenNoSourceSet_ThenTryGetSourceReturnsFalse()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = property.TryGetSource(out var source);

        // Assert
        Assert.False(result);
        Assert.Null(source);
    }

    [Fact]
    public void WhenSetSourceCalledWithDifferentSource_ThenReturnsFalse()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source1Mock = Mock.Of<ISubjectSource>();
        var source2Mock = Mock.Of<ISubjectSource>();

        // Act
        var result1 = property.SetSource(source1Mock);
        var result2 = property.SetSource(source2Mock);

        // Assert
        Assert.True(result1);
        Assert.False(result2); // Different source should return false
        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(source1Mock, retrievedSource); // Original source should remain
    }

    [Fact]
    public void WhenSetSourceCalledWithSameSource_ThenReturnsTrue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        // Act
        var result1 = property.SetSource(sourceMock);
        var result2 = property.SetSource(sourceMock);

        // Assert
        Assert.True(result1);
        Assert.True(result2); // The same source should return true (idempotent)
    }

    [Fact]
    public void WhenRemoveSourceCalledWithMatchingSource_ThenSourceReferenceIsCleared()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        // Act
        var removed = property.RemoveSource(sourceMock);

        // Assert
        Assert.True(removed);
        Assert.False(property.TryGetSource(out _));
    }

    [Fact]
    public void WhenRemoveSourceCalledWithDifferentSource_ThenSourceReferenceIsKept()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        var otherSourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        // Act
        var removed = property.RemoveSource(otherSourceMock);

        // Assert
        Assert.False(removed);
        Assert.True(property.TryGetSource(out var source));
        Assert.Same(sourceMock, source);
    }

    [Fact]
    public async Task WhenSourceBoundPropertyCommitted_ThenSourceIsWritten()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateSucceedingSource();

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task WhenSourceWriteFails_ThenCommitThrowsTransactionException()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateFailingSource("Write failed");

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            // Assert
            Assert.Single(exception.FailedChanges);
            Assert.Single(exception.Errors);
            Assert.IsType<SourceTransactionWriteException>(exception.Errors[0]);
        }

        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task WhenMixedChangesCommittedInBestEffortMode_ThenLocalAndSuccessfulSourceChangesAreApplied()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var failingSource = CreateFailingSource("Write failed");

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        firstNameProp.SetSource(failingSource.Object);

        // Act
        // Use BestEffort mode to test partial success (local properties should be applied)
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            // Assert
            Assert.Single(exception.FailedChanges);
        }

        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenMultipleSourcesCommitted_ThenChangesAreGroupedBySource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source1Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(source1Mock.Object);
        lastNameProp.SetSource(source2Mock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal(1, source1Writes[0]);
        Assert.Equal(1, source2Writes[0]);
    }

    [Fact]
    public async Task WhenMultipleSourcesAndLocalCommitted_ThenSourcesAreWrittenAndLocalApplied()
    {
        // Arrange - two distinct sources plus a local (no-source) change in one transaction;
        // exercises the grouped path with a non-empty local set.
        var context = CreateContext();
        var person = new Person(context);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source1Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1Mock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2Mock.Object);
        // FirstName_MaxLength is left without a source -> local change applied to the local model.

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            person.FirstName_MaxLength = 42;
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - each source written once with its single change; the local change is applied.
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal(1, source1Writes[0]);
        Assert.Equal(1, source2Writes[0]);
        Assert.Equal(42, person.FirstName_MaxLength);
    }

    [Fact]
    public async Task WhenSingleSourceAndLocalCommitted_ThenOnlySourceBoundChangesAreWrittenToSource()
    {
        // Arrange - two source-bound changes on one source plus a local change interleaved between
        // them; directly exercises the single-source path's source/local separation (two-pointer).
        var context = CreateContext();
        var person = new Person(context);

        var written = new List<SubjectPropertyChange>();
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.Span)
                {
                    written.Add(change);
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);
        // FirstName_MaxLength is left without a source -> local change.

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.FirstName_MaxLength = 42; // local change interleaved between source-bound changes
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - only the two source-bound changes reached the source; the local change was applied.
        Assert.Equal(2, written.Count);
        Assert.Contains(written, c => c.Property.Metadata.Name == nameof(Person.FirstName));
        Assert.Contains(written, c => c.Property.Metadata.Name == nameof(Person.LastName));
        Assert.DoesNotContain(written, c => c.Property.Metadata.Name == nameof(Person.FirstName_MaxLength));
        Assert.Equal(42, person.FirstName_MaxLength);
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenUserTokenCancelledDuringCommit_ThenCommitStillSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act - User cancellation is ignored during commit (only timeout matters)
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Commit should succeed even with cancelled token
            await transaction.CommitAsync(cts.Token);

            // Assert
            Assert.Equal("John", person.FirstName);
        }
    }

    [Fact]
    public async Task WhenSourceWriteExceedsCommitTimeout_ThenCommitFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>(async (changes, ct) =>
            {
                // Simulate slow write that will be cancelled by timeout
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return WriteResult.Success;
                }
                catch (OperationCanceledException)
                {
                    return WriteResult.Failure(changes, new OperationCanceledException(ct));
                }
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        // Act - Use a very short timeout
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            commitTimeout: TimeSpan.FromMilliseconds(50)))
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            // Assert
            Assert.Single(exception.FailedChanges);
            Assert.Single(exception.Errors);
            var sourceWriteException = Assert.IsType<SourceTransactionWriteException>(exception.Errors[0]);
            Assert.IsAssignableFrom<OperationCanceledException>(sourceWriteException.InnerException);
        }
    }

    [Fact]
    public async Task WhenSourceBoundPropertyCommitted_ThenWriteChangesAsyncIsCalledOnce()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        var capturedChanges = new List<SubjectPropertyChange>();
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.Span)
                {
                    capturedChanges.Add(change);
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(sourceMock.Object);
        lastNameProp.SetSource(sourceMock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(2, capturedChanges.Count);
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.FirstName) && c.GetNewValue<string>() == "John");
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.LastName) && c.GetNewValue<string>() == "Doe");

        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenSubjectsFromMultipleContextsCommitted_ThenCallbacksResolvePerContext()
    {
        // Arrange
        var context1 = CreateContext();
        var context2 = CreateContext();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source1Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person1, nameof(Person.FirstName)).SetSource(source1Mock.Object);
        new PropertyReference(person2, nameof(Person.FirstName)).SetSource(source2Mock.Object);

        // Act
        // Use separate transactions for each context since transactions are now context-bound
        using (var transaction1 = await context1.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person1.FirstName = "John";
            await transaction1.CommitAsync(CancellationToken.None);
        }

        using (var transaction2 = await context2.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person2.FirstName = "Jane";
            await transaction2.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task WhenSourcePartiallyFails_ThenOnlyFailedChangesAreReported()
    {
        // Arrange
        // Tests the path where WriteResult.FailedChanges contains specific failed changes
        // (not all changes in the batch failed)
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                // Simulate partial failure: only FirstName fails
                var span = changes.Span;
                var failedChanges = new List<SubjectPropertyChange>();
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i].Property.Metadata.Name == nameof(Person.FirstName))
                    {
                        failedChanges.Add(span[i]);
                    }
                }

                if (failedChanges.Count > 0)
                {
                    return new ValueTask<WriteResult>(
                        WriteResult.PartialFailure(
                            failedChanges.ToArray(),
                            new InvalidOperationException("FirstName write failed")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // Only FirstName should be in FailedChanges (partial failure)
        Assert.Single(ex.FailedChanges);
        Assert.Equal(nameof(Person.FirstName), ex.FailedChanges[0].Property.Metadata.Name);

        // LastName should have been applied (it was in successful changes)
        Assert.Equal("Doe", person.LastName);
        // FirstName should not be applied (it failed)
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task WhenSourcePartiallyFailsInRollbackMode_ThenSuccessfulChangesAreReverted()
    {
        // Arrange
        // Tests that partial failure triggers rollback for successful changes
        var context = CreateContext();
        var person = new Person(context);

        var writeCallCount = 0;
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writeCallCount++;
                if (writeCallCount == 1)
                {
                    // First call: partial failure (only FirstName fails)
                    var span = changes.Span;
                    for (var i = 0; i < span.Length; i++)
                    {
                        if (span[i].Property.Metadata.Name == nameof(Person.FirstName))
                        {
                            return new ValueTask<WriteResult>(
                                WriteResult.PartialFailure(
                                    new[] { span[i] },
                                    new InvalidOperationException("FirstName write failed")));
                        }
                    }
                }
                // Subsequent calls (rollback): succeed
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // In Rollback mode, nothing should be applied when there's any failure
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Contains("Rollback was attempted", ex.Message);
        Assert.Equal(2, writeCallCount); // Initial write + revert
    }

    [Fact]
    public async Task WhenSourceRemovedDuringWriteInRollbackMode_ThenRevertStillTargetsOriginalSource()
    {
        // Arrange - A source S is bound to a property. A local property fails to apply during commit,
        // which (in Rollback mode) triggers a revert of the already-written source change. The source's
        // WriteChangesAsync callback detaches itself from the property (RemoveSource) before returning
        // success, so a TryGetSource-based revert would silently drop the revert. With RevertState the
        // revert still targets the original source.
        var context = CreateContext();
        var person = new Person(context);
        var device = new ThrowingDevice(context)
        {
            ShouldThrow = prop => prop == nameof(ThrowingDevice.PropertyB)
        };

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));

        var writes = new List<(string Property, string? Value)>();
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.Span)
                {
                    writes.Add((change.Property.Metadata.Name!, change.GetNewValue<string?>()));
                }

                // Detach the source from the property right after the forward write: a revert that
                // re-derives the source via TryGetSource would now find nothing.
                firstNameProp.RemoveSource(sourceMock!.Object);

                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        firstNameProp.SetSource(sourceMock.Object);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John"; // source-bound, written then reverted
        device.PropertyB = true;   // local, throws during apply -> triggers rollback

        device.ThrowingEnabled = true;

        await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert - the forward write and the revert write both reached the original source, even though
        // the source was removed from the property between them.
        Assert.Equal(2, writes.Count);
        Assert.Equal((nameof(Person.FirstName), "John"), writes[0]);
        Assert.Equal((nameof(Person.FirstName), (string?)null), writes[1]); // inverse value reverted to S
    }

    [Fact]
    public async Task WhenDisposedDuringInFlightCommit_ThenLiveBufferIsNotReturnedToPool()
    {
        // Arrange - Reproduces the genuine interleaving the pool-return guard protects against:
        //   A (transaction A) parks inside its source writer with _isCommitting == true and its pending
        //   dictionary NOT yet cleared (FinishCommit runs only after the writer returns). While A is parked,
        //   A is disposed (Dispose-during-in-flight-commit) and a second transaction B is started. B rents a
        //   pending dictionary from the shared PendingChangesPool. If Dispose returned A's still live
        //   dictionary, B rents that very instance; then A's FinishCommit().Clear() would wipe B's pending
        //   change before B commits. The guard (skip the pool return while _isCommitting) prevents this by
        //   not pooling a buffer an in-flight commit still owns, so B always gets a clean buffer.
        //
        // B runs on a SEPARATE context (its own subject) on purpose: the pending-changes pool is a single
        // static pool shared across all contexts, so a separate context still exercises the guard, while
        // avoiding A's exclusive transaction lock (which a disposed-but-still-committing A now holds until
        // its commit completes - that lock is per-context, so a same-context B would correctly block here).
        //
        // With the guard: B's change survives and is applied.
        // Without the guard (Dispose returns the live buffer unconditionally): B shares A's dictionary, A's
        // FinishCommit clears it, B commits nothing and personB.LastName stays null -> the assertion fails.
        var timeout = TimeSpan.FromSeconds(10);

        var contextA = CreateContext();
        var personA = new Person(contextA);

        var contextB = CreateContext();
        var personB = new Person(contextB);

        // A's source parks inside WriteChangesAsync until the test releases it, holding A in flight.
        var writerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkingSource = new Mock<ISubjectSource>();
        parkingSource.Setup(s => s.WriteBatchSize).Returns(0);
        parkingSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                writerEntered.TrySetResult(true);
                await releaseWriter.Task.ConfigureAwait(false);
                return WriteResult.Success;
            });

        new PropertyReference(personA, nameof(Person.FirstName)).SetSource(parkingSource.Object);

        var succeedingSource = CreateSucceedingSource();
        new PropertyReference(personB, nameof(Person.LastName)).SetSource(succeedingSource.Object);

        // Act - Run A entirely on its own execution context (Task.Run) so its AsyncLocal current-transaction
        // does not flow into the test thread or into B. A parks in the writer with its dictionary alive.
        SubjectTransaction transactionA = null!;
        var commitA = Task.Run(async () =>
        {
            transactionA = await contextA.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            personA.FirstName = "John";
            await transactionA.CommitAsync(CancellationToken.None);
        });

        Assert.True(await WaitWithTimeout(writerEntered.Task, timeout),
            "Transaction A's writer was never entered.");

        // Dispose A while its commit is parked in the writer. With the bug this returns A's live dictionary
        // to the pool. A still holds _isCommitting == true, so the guard must skip the pool return.
        transactionA.Dispose();

        // Start B (separate context) and capture a change so B rents a dictionary from the shared pool.
        // Run on its own execution context so B's AsyncLocal is isolated from A.
        var commitB = Task.Run(async () =>
        {
            using var transactionB = await contextB.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            personB.LastName = "Doe"; // rents from the pool and populates the pending dictionary

            // Release A's writer only now that B has populated its buffer: if B shares A's dictionary, A's
            // FinishCommit().Clear() (after the writer returns) wipes B's pending change.
            releaseWriter.TrySetResult(true);
            Assert.True(await WaitWithTimeout(commitA, timeout), "Transaction A's commit did not finish.");

            await transactionB.CommitAsync(CancellationToken.None);
        });

        Assert.True(await WaitWithTimeout(commitB, timeout), "Transaction B's commit did not finish.");
        await commitB; // observe exceptions

        // Assert - B committed its own change. With the bug, B's change was wiped by A's FinishCommit and
        // LastName stays null; the guard keeps B's buffer separate so the change survives and is applied.
        Assert.Equal("John", personA.FirstName); // A's commit still completed after the writer was released
        Assert.Equal("Doe", personB.LastName);   // B's change survived (fails without the pool-return guard)
        succeedingSource.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenDisposedDuringInFlightExclusiveCommit_ThenLockIsReleasedAfterCommitCompletes()
    {
        // A disposed-but-still-committing transaction holds the exclusive (per-context) transaction lock
        // until its commit actually finishes; Dispose defers releasing it to EndCommit. This verifies:
        //   1. The lock is NOT released early - a second transaction on the same context cannot begin while
        //      A's commit is parked, so no other transaction can apply to the same graph concurrently.
        //   2. The lock is released exactly once when the commit completes (no leak) - a same-context B then
        //      begins and commits normally.
        var timeout = TimeSpan.FromSeconds(10);

        var context = CreateContext();
        var person = new Person(context);

        var writerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkingSource = new Mock<ISubjectSource>();
        parkingSource.Setup(s => s.WriteBatchSize).Returns(0);
        parkingSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                writerEntered.TrySetResult(true);
                await releaseWriter.Task.ConfigureAwait(false);
                return WriteResult.Success;
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(parkingSource.Object);

        // Act - A parks inside its writer, then is disposed mid-commit (it still holds the exclusive lock).
        SubjectTransaction transactionA = null!;
        var commitA = Task.Run(async () =>
        {
            transactionA = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            person.FirstName = "John";
            await transactionA.CommitAsync(CancellationToken.None);
        });

        Assert.True(await WaitWithTimeout(writerEntered.Task, timeout), "Transaction A's writer was never entered.");
        transactionA.Dispose();

        // B begins on the SAME context; it must not acquire the exclusive lock until A's deferred commit
        // completes and releases it.
        var bBegan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitB = Task.Run(async () =>
        {
            using var transactionB = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            bBegan.TrySetResult(true);
            person.LastName = "Doe";
            await transactionB.CommitAsync(CancellationToken.None);
        });

        // While A is still parked, B must remain blocked on the lock. A non-occurrence can only be observed
        // within a window; this cannot false-fail because A cannot complete (and free the lock) until the
        // writer is released below, which happens only after this check.
        Assert.False(await WaitWithTimeout(bBegan.Task, TimeSpan.FromMilliseconds(200)),
            "Transaction B acquired the exclusive lock before A's in-flight commit completed.");

        // Release A's writer -> A's commit completes -> EndCommit releases the deferred lock -> B proceeds.
        releaseWriter.TrySetResult(true);
        Assert.True(await WaitWithTimeout(commitA, timeout), "Transaction A's commit did not finish.");
        Assert.True(await WaitWithTimeout(commitB, timeout), "Transaction B never completed (exclusive lock leaked).");
        await commitB; // observe exceptions

        // Assert - both commits landed; the lock was held through A and released exactly once.
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    private static async Task<bool> WaitWithTimeout(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a source that records, in arrival order across all of its WriteChangesAsync calls,
    /// the property name of every change it receives. Used by the classifier regression tests to
    /// assert per-source grouping and order.
    /// </summary>
    private static Mock<ISubjectSource> CreateRecordingSource(List<string> recorded)
    {
        var mock = new Mock<ISubjectSource>();
        mock.Setup(s => s.WriteBatchSize).Returns(0);
        mock.Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.Span)
                {
                    recorded.Add(change.Property.Metadata.Name!);
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });
        return mock;
    }

    [Fact]
    public async Task WhenSourceReappearsAfterSwitch_ThenGroupingAndOrderArePreserved()
    {
        // Arrange - commit order A, B, A: P0 (FirstName) and P2 (FirstName_MaxLength_Unit) bind to sourceA,
        // P1 (LastName) binds to sourceB. The trailing A change must route into the EXISTING sourceA group
        // (created during the single-source prefix, before the switch to sourceB) rather than a new/duplicate
        // group, and its order relative to the earlier A change must be preserved.
        var context = CreateContext();
        var person = new Person(context);

        var sourceARecorded = new List<string>();
        var sourceA = CreateRecordingSource(sourceARecorded);

        var sourceBRecorded = new List<string>();
        var sourceB = CreateRecordingSource(sourceBRecorded);

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceA.Object);            // P0 -> A
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceB.Object);             // P1 -> B
        new PropertyReference(person, nameof(Person.FirstName_MaxLength_Unit)).SetSource(sourceA.Object); // P2 -> A

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Joh";                  // P0
            person.LastName = "Doe";                   // P1
            person.FirstName_MaxLength_Unit = "Items"; // P2
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - sourceA received exactly [P0, P2] in that order (single group, no duplication);
        // sourceB received exactly [P1]; the local model holds all three values.
        Assert.Equal(
            new[] { nameof(Person.FirstName), nameof(Person.FirstName_MaxLength_Unit) },
            sourceARecorded);
        Assert.Equal(new[] { nameof(Person.LastName) }, sourceBRecorded);
        Assert.Equal("Joh", person.FirstName);
        Assert.Equal("Doe", person.LastName);
        Assert.Equal("Items", person.FirstName_MaxLength_Unit);
    }

    [Fact]
    public async Task WhenLateSourceSwitchWithInterleavedLocals_ThenOnlySourceBoundChangesAreGrouped()
    {
        // Arrange - commit order: P0 (FirstName) -> sourceA, P1 (FirstName_MaxLength) local (no source),
        // P2 (FirstName_MaxLength_Unit) -> sourceA, P3 (LastName) -> sourceB. The interleaved local must be
        // excluded from the single-source prefix that seeds the dictionary on the late switch to sourceB,
        // and source-bound order must be preserved.
        var context = CreateContext();
        var person = new Person(context);

        var sourceARecorded = new List<string>();
        var sourceA = CreateRecordingSource(sourceARecorded);

        var sourceBRecorded = new List<string>();
        var sourceB = CreateRecordingSource(sourceBRecorded);

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceA.Object);                // P0 -> A
        // P1 (FirstName_MaxLength) intentionally left without a source -> local.
        new PropertyReference(person, nameof(Person.FirstName_MaxLength_Unit)).SetSource(sourceA.Object); // P2 -> A
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceB.Object);                 // P3 -> B

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Joh";                  // P0 (source A)
            person.FirstName_MaxLength = 42;           // P1 (local)
            person.FirstName_MaxLength_Unit = "Items"; // P2 (source A)
            person.LastName = "Doe";                   // P3 (source B)
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - sourceA received exactly [P0, P2] (local P1 excluded, order preserved); sourceB got [P3];
        // the local P1 was applied to the local model and never written to any source.
        Assert.Equal(
            new[] { nameof(Person.FirstName), nameof(Person.FirstName_MaxLength_Unit) },
            sourceARecorded);
        Assert.Equal(new[] { nameof(Person.LastName) }, sourceBRecorded);
        Assert.DoesNotContain(nameof(Person.FirstName_MaxLength), sourceARecorded);
        Assert.DoesNotContain(nameof(Person.FirstName_MaxLength), sourceBRecorded);
        Assert.Equal(42, person.FirstName_MaxLength); // local applied to the local model
        Assert.Equal("Joh", person.FirstName);
        Assert.Equal("Items", person.FirstName_MaxLength_Unit);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenSingleSourceWithTrailingLocals_ThenExactlySourceBoundChangesAreWritten()
    {
        // Arrange - P0 (FirstName) and P1 (LastName) bind to sourceA; P2 (FirstName_MaxLength) and
        // P3 (FirstName_MaxLength_Unit) are local. The single-source buffer is sized to the full change
        // count (4), but only 2 slots are filled. The source must receive EXACTLY those 2 changes via the
        // ArraySegment/AsMemory(0, count) view, never the unused (default/zeroed) buffer tail.
        var context = CreateContext();
        var person = new Person(context);

        var recorded = new List<string>();
        var sourceA = CreateRecordingSource(recorded);

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceA.Object); // P0 -> A
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceA.Object);  // P1 -> A
        // P2 (FirstName_MaxLength) and P3 (FirstName_MaxLength_Unit) left without a source -> local.

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Joh";                  // P0 (source A)
            person.LastName = "Doe";                   // P1 (source A)
            person.FirstName_MaxLength = 42;           // P2 (local, trailing)
            person.FirstName_MaxLength_Unit = "Items"; // P3 (local, trailing)
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - the source saw EXACTLY the 2 source-bound changes (not 4, no tail garbage); the trailing
        // locals were applied to the local model only.
        Assert.Equal(
            new[] { nameof(Person.FirstName), nameof(Person.LastName) },
            recorded);
        Assert.Equal(42, person.FirstName_MaxLength);        // local applied to the local model
        Assert.Equal("Items", person.FirstName_MaxLength_Unit); // local applied to the local model
        Assert.Equal("Joh", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenSourceReadsPropertyWhileWriting_ThenErrorNamesTheSourceWriteBoundary()
    {
        // Arrange - a source that builds its payload by reading the model instead of the supplied
        // changes. The built-in writer calls it on the committing flow, where reads are rejected.
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                _ = person.LastName;
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Joh";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert - the message has to point at the write boundary and its remedy, not at threading
        var error = Assert.IsType<InvalidOperationException>(Assert.Single(exception.Errors).GetBaseException());
        Assert.Contains("commit is in progress", error.Message);
        Assert.Contains("supplied changes", error.Message);
    }
}
