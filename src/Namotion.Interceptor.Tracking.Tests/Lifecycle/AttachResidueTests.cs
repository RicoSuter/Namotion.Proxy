using Namotion.Interceptor.Registry;
using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// An explicit attach discovers the prospective component, claims it, and only then seeds and
/// publishes it. Discovery reads user values, so a concurrent write can change a property after it
/// was scanned and before the root is claimed. The seed then rejects the newly installed child,
/// and the claim and the root anchor taken in between must not survive that rejection.
/// </summary>
public class AttachResidueTests
{
    private static LifecycleInterceptor GetLifecycle(IInterceptorSubjectContext context)
    {
        return (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// A user enumerable that runs a callback when its enumeration starts, which is where the
    /// discovery scan invokes user code.
    /// </summary>
    /// <summary>
    /// Reproduces the finding that a rejected explicit attach leaves residue behind. The rollback
    /// contract is that a rejected attach publishes nothing at all, so this asserts each kind of
    /// state the attach would have written separately rather than probing one of them and inferring
    /// the rest: no committed baseline for the root's structural property, no ownership record, no
    /// root anchor, and no claim on the context. Asserting them one by one is what makes a partial
    /// rollback report which part leaked instead of failing on whichever probe happened to be used.
    ///
    /// The window between the discovery scan and the claim is held open artificially, by a user
    /// enumerable that parks inside the discovery scan until a second thread has installed a
    /// foreign-context child. The window itself is real; only its width is manufactured. The park is
    /// positioned by phase and the guard below asserts it: the root is still unattached when the
    /// park runs, which is what makes this the scan and not something after the claim.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenSeedingRejectsAChildInstalledAfterDiscovery_ThenTheAttachLeavesNoResidue()
    {
        // Arrange: the root is scanned while it still holds an empty value, then a second thread
        // stores a child owned by another context before the claim publishes the root anchor.
        var context = CreateContext();
        var otherContext = CreateContext();
        var foreignChild = new Person { FirstName = "F" };
        ((IInterceptorSubject)foreignChild).AttachToContext(otherContext);

        var discoveryReachedTheValue = new ManualResetEventSlim(false);
        var mutationApplied = new ManualResetEventSlim(false);
        var mutationObserved = false;
        var rootWasUnattachedAtScan = false;
        EnumerableChildrenHolder? holder = null;
        holder = new EnumerableChildrenHolder
        {
            Children = new ParkingEnumerable(() =>
            {
                rootWasUnattachedAtScan = ((IInterceptorSubject)holder!).TryGetContext() is null;
                discoveryReachedTheValue.Set();
                mutationObserved = mutationApplied.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            })
        };

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            if (!discoveryReachedTheValue.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            mutationException = Record.Exception(() => holder!.Children = new List<Person> { foreignChild });
            mutationApplied.Set();
        })
        {
            IsBackground = true
        };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));
        var mutatorCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert: the race actually happened, so the repro cannot pass without it.
        Assert.True(mutatorCompleted, "the mutating thread never finished");
        Assert.True(mutationObserved, "the discovery scan never observed the concurrent mutation");
        Assert.True(rootWasUnattachedAtScan, "the scan ran after the root was already claimed");
        Assert.Null(mutationException);
        Assert.NotNull(attachException);

        // The attach was rejected, so every kind of state it would have written must be absent.
        var graph = GetLifecycle(context).Graph;
        Assert.False(graph.HasBaseline(new PropertyReference(holder, nameof(EnumerableChildrenHolder.Children))),
            "the rejected attach left a committed baseline for the root's structural property");
        Assert.False(graph.IsOwned(holder),
            "the rejected attach left an ownership record for the root");
        Assert.False(((IInterceptorSubject)holder).Executor.AttachmentAnchor != SubjectAttachmentAnchorKind.None,
            "the rejected attach left the explicit root anchor published");
        Assert.True(((IInterceptorSubject)holder).TryGetContext() is null,
            "the rejected attach left the root claimed by the context it was rejected from; " +
            $"seeding failed with {attachException.GetType().Name}: {attachException.Message}");
    }

    /// <summary>
    /// The same rejection, with a legal sibling ahead of the refused child in the same collection so
    /// that seeding publishes one subject before it throws on the next. Both children come from one
    /// property value, so the order the seed attaches them in is the order of that list and not the
    /// order the subject happens to enumerate its properties in. The published sibling is the part
    /// of the residue a claim-only rollback cannot reach: it is owned, not merely claimed, and it is
    /// asserted separately from the root for the same reason the test above splits its assertions.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenSeedingRejectsAChildAfterPublishingAnEarlierSibling_ThenThePublishedSiblingIsReleasedToo()
    {
        // Arrange
        var context = CreateContext();
        var otherContext = CreateContext();
        var legalSibling = new Person { FirstName = "L" };
        var foreignChild = new Person { FirstName = "F" };
        ((IInterceptorSubject)foreignChild).AttachToContext(otherContext);

        var discoveryReachedTheValue = new ManualResetEventSlim(false);
        var mutationApplied = new ManualResetEventSlim(false);
        var mutationObserved = false;
        EnumerableChildrenHolder? holder = null;
        holder = new EnumerableChildrenHolder
        {
            Children = new ParkingEnumerable(() =>
            {
                discoveryReachedTheValue.Set();
                mutationObserved = mutationApplied.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            })
        };

        var mutator = new Thread(() =>
        {
            if (!discoveryReachedTheValue.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            holder!.Children = new List<Person> { legalSibling, foreignChild };
            mutationApplied.Set();
        })
        {
            IsBackground = true
        };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));
        var mutatorCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert: the race actually happened.
        Assert.True(mutatorCompleted, "the mutating thread never finished");
        Assert.True(mutationObserved, "the discovery scan never observed the concurrent mutation");
        Assert.NotNull(attachException);

        // The sibling was published by the seed that then threw, so the rejection must take it back.
        var graph = GetLifecycle(context).Graph;
        Assert.False(graph.IsOwned(legalSibling),
            "the rejected attach left an ownership record for the sibling it published before it threw");
        Assert.True(((IInterceptorSubject)legalSibling).TryGetContext() is null,
            "the rejected attach left the published sibling attached to the context it was rejected from");
        Assert.True(((IInterceptorSubject)holder).TryGetContext() is null,
            "the rejected attach left the root claimed by the context it was rejected from");
    }

    /// <summary>
    /// External callback failures are reported after each operation finishes its graph and Registry maintenance.
    /// </summary>
    [Fact]
    public void WhenAttachAndDetachCallbacksThrow_ThenEachOperationReportsItsOwnFailureAfterSettling()
    {
        // Arrange
        var child = new Person { FirstName = "C" };
        var attachFailure = new InvalidOperationException("attach callback failed");
        var detachFailure = new InvalidOperationException("detach callback failed");
        var context = CreateContext().WithRegistry()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (!ReferenceEquals(change.Subject, child))
                {
                    return;
                }

                if (change.IsContextAttach)
                {
                    throw attachFailure;
                }

                if (change.IsContextDetach)
                {
                    throw detachFailure;
                }
            }), _ => false);

        var holder = new EnumerableChildrenHolder { Children = new List<Person> { child } };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert
        Assert.Same(attachFailure, exception);
        SupportContractAssertions.Settled(context, [holder], holder, child);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)holder).Executor.AttachmentAnchor);

        // Act
        var detachException = Record.Exception(() => holder.DetachFromContext(context));

        // Assert
        Assert.Same(detachFailure, detachException);
        SupportContractAssertions.Settled(context, [], holder, child);
    }
}
