using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathTransactionTests
{
    public PathTransactionTests() => PropertyChangeSubscriptions.ResetForTests();

    private static IInterceptorSubjectContext CreateTransactionContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();

    // A: subscribing inside an active (not-yet-committing) transaction is refused, because the chain would
    // bind to the transaction's speculative staged subjects, which a rollback would strand.
    [Fact]
    public async Task WhenSubscribingInsideActiveTransaction_ThenThrowsInvalidOperationException()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };

        // Act & Assert
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full));
        Assert.Contains("active transaction", exception.Message);
    }

    // B: a subscription created OUTSIDE a transaction survives a transaction that stages then rolls back a
    // watched intermediate. Staging never dispatches, so no walk runs and no phantom chain is installed on
    // the speculative subject; the chain stays on the committed subject and a later real write still delivers.
    [Fact]
    public async Task WhenTransactionStagesThenRollsBackWatchedIntermediate_ThenChainStaysOnCommittedSubject()
    {
        // Arrange
        var context = CreateTransactionContext();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Act: stage a reassignment of the watched intermediate, then dispose without commit (rollback).
        var stagedFather = new Person { FirstName = "Staged" };
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.Father = stagedFather; // staged (non-derived); never dispatched, so no walk runs
            Assert.Empty(events);
        }

        // Assert: nothing was applied, no listener leaked on the staged subject, and the committed chain stands.
        Assert.Same(father, person.Father);
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.False(new PropertyReference(stagedFather, nameof(Person.FirstName))
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _));

        // Act: a real write to the committed father's leaf still delivers.
        father.FirstName = "Jack";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Joe", change.OldState.GetValueOrDefault());
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());
    }

    // C: a watched leaf write staged in a transaction is not delivered until commit; the commit replay
    // applies it and delivers the ValueChange.
    [Fact]
    public async Task WhenWatchedLeafStagedThenCommitted_ThenValueChangeDeliveredAtCommit()
    {
        // Arrange
        var context = CreateTransactionContext();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            father.FirstName = "Jack";
            Assert.Empty(events); // staged, nothing delivered yet
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert: the commit replay applied the leaf write and delivered the ValueChange.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Joe", change.OldState.GetValueOrDefault());
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());
    }

    // D: a watched leaf write staged in a transaction disposed without commit delivers nothing and the model
    // is unchanged.
    [Fact]
    public async Task WhenWatchedLeafStagedThenRolledBack_ThenNothingDelivered()
    {
        // Arrange
        var context = CreateTransactionContext();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: stage a leaf write, then dispose without commit (rollback).
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            father.FirstName = "Jack";
        }

        // Assert
        Assert.Empty(events);
        Assert.Equal("Joe", father.FirstName);
    }

    // E: a commit that fails during apply under Rollback reverts the already-applied watched change, so the
    // subscriber sees the apply-and-revert pair and the observed state converges to the pre-transaction value.
    [Fact]
    public async Task WhenCommitFailsUnderRollback_ThenAppliedWatchedChangeIsRevertedAndConverges()
    {
        // Arrange
        var context = CreateTransactionContext();
        var node = new RollbackNode(context) { Accept = "Original" };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = node.SubscribeToPath(x => x.Accept,
            (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: stage a good change to the watched Accept and a poison change to Reject; commit under Rollback.
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            node.Accept = "Applied";
            node.Reject = "poison";
            var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains(exception.Errors, e => e is InvalidOperationException);
        }

        // Assert: Accept was applied (Original->Applied) then reverted (Applied->Original); model converged.
        Assert.Equal(2, events.Count);
        Assert.Equal("Original", events[0].OldState.GetValueOrDefault());
        Assert.Equal("Applied", events[0].NewState.GetValueOrDefault());
        Assert.Equal("Applied", events[1].OldState.GetValueOrDefault());
        Assert.Equal("Original", events[1].NewState.GetValueOrDefault());
        Assert.Equal("Original", node.Accept);
        Assert.Equal("Original", subscription.Current.GetValueOrDefault());
    }

    // F: the retrack-stranding regression. A real [Derived]-with-setter write on the resolved child bypasses
    // staging and dispatches immediately on the transaction flow, driving the event walk while a staged
    // reassignment of the watched Child intermediate is pending. The walk must read COMMITTED state (child A),
    // not the transaction's staged read-your-writes view (child B); otherwise it retracks onto the speculative
    // B, disposes A's Value listener, and a rollback strands the chain on a subject that no longer exists on
    // the path. Without the ambient-suppression fix this fails at the first in-transaction assertion.
    [Fact]
    public async Task WhenDerivedWriteDrivesEventWalkOnTransactionFlow_ThenWalkReadsCommittedStateNoStranding()
    {
        // Arrange
        var context = CreateTransactionContext();
        var childA = new TransactionNode { Value = "A0" };
        var root = new TransactionNode(context) { Child = childA };
        var childB = new TransactionNode { Value = "B0" };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Value,
            (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // White-box: A.Value is watched, B.Value is not.
        Assert.True(HasListener(childA, nameof(TransactionNode.Value)));
        Assert.False(HasListener(childB, nameof(TransactionNode.Value)));

        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // Stage a reassignment of the watched intermediate: read-your-writes now resolves Child to B.
            root.Child = childB;

            // A real derived write on A: IsDerived bypasses staging, dispatches immediately, and runs the
            // event walk ON the transaction flow.
            childA.Value = "A1";

            // Assert (inside the transaction): the walk stayed on committed A. A's Value listener is intact
            // and no B listener was installed, so the chain never retracked onto the speculative staged B.
            Assert.True(HasListener(childA, nameof(TransactionNode.Value)));
            Assert.False(HasListener(childB, nameof(TransactionNode.Value)));
            Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());

            // The derived write itself is a leaf ValueChange on the committed chain.
            var inTransaction = Assert.Single(events);
            Assert.Equal(SubjectPathChangeKind.ValueChange, inTransaction.Kind);
            Assert.Equal("A0", inTransaction.OldState.GetValueOrDefault());
            Assert.Equal("A1", inTransaction.NewState.GetValueOrDefault());
        } // rollback: the staged Child=B is discarded

        // Assert: no stranding. B never got a listener; the count is unchanged; the chain is still on A.
        Assert.False(HasListener(childB, nameof(TransactionNode.Value)));
        Assert.True(HasListener(childA, nameof(TransactionNode.Value)));
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Same(childA, root.Child);

        // Act: a subsequent write to A.Value still delivers (the chain was never stranded onto B).
        events.Clear();
        childA.Value = "A2";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("A1", change.OldState.GetValueOrDefault());
        Assert.Equal("A2", change.NewState.GetValueOrDefault());
    }

    // G: a callback that throws during commit replay is caught by the transaction apply loop
    // (SubjectPropertyChangeOperations.TryApplyLocalChange) and surfaced as SubjectTransactionException; it
    // does not escape to the writer. Outside a transaction the same throwing callback propagates straight to
    // the drainer, and thus to the writing statement.
    [Fact]
    public async Task WhenCallbackThrows_ThenCommitReplaySurfacesTransactionException_ButDirectWritePropagates()
    {
        // Arrange
        var context = CreateTransactionContext();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var deliveries = 0;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) =>
        {
            deliveries++;
            throw new InvalidOperationException("callback boom");
        }, SubjectPathValidation.Full);

        // Act & Assert: inside a transaction the throwing callback runs in the commit apply loop, which
        // catches it and surfaces a SubjectTransactionException rather than letting it escape to the writer.
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            father.FirstName = "Jack";
            Assert.Equal(0, deliveries); // staged; not delivered yet
            var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains(exception.Errors, e => e is InvalidOperationException { Message: "callback boom" });
        }
        Assert.Equal(1, deliveries);

        // Act & Assert: outside a transaction the same throwing callback propagates to the writing statement.
        var direct = Assert.Throws<InvalidOperationException>(() => father.FirstName = "Max");
        Assert.Equal("callback boom", direct.Message);
        Assert.Equal(2, deliveries);
    }

    private static bool HasListener(IInterceptorSubject subject, string propertyName) =>
        new PropertyReference(subject, propertyName)
            .TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out _);
}
