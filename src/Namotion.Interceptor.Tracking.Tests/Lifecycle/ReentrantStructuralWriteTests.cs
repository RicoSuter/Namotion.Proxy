using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Reconciliation reads the committed baseline, scans user values, and only then commits the new
/// baseline. Those scans run at callback depth zero, where a nested write of the same property is
/// legal, so the outer operation can commit its own baseline on top of the newer one the nested
/// write already committed.
/// </summary>
public class ReentrantStructuralWriteTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static object? GetCommittedBaseline(IInterceptorSubjectContext context, EnumerableChildrenHolder holder)
    {
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        return lifecycle.Graph.GetBaseline(
            new PropertyReference(holder, nameof(EnumerableChildrenHolder.Children)));
    }

    /// <summary>
    /// A user enumerable that re-enters the write protocol once, the first time it is scanned while
    /// <see cref="ShouldReenter"/> holds.
    ///
    /// The trigger is a condition rather than an enumeration ordinal on purpose. How many times the
    /// protocol scans a given value is an implementation detail that is expected to change, and an
    /// ordinal armed against today's count would silently stop firing when it does. The condition
    /// used by the test below instead names the phase it needs, so a change to the scan count either
    /// still lands in that phase or trips the guard.
    /// </summary>
    private sealed class ScanHookEnumerable(IEnumerable<Person> items) : IEnumerable<Person>
    {
        private bool _hasReentered;

        public int Enumerations { get; private set; }

        public bool HasReentered => _hasReentered;

        public Func<bool>? ShouldReenter { get; set; }

        public Action? OnReenter { get; set; }

        public IEnumerator<Person> GetEnumerator()
        {
            Enumerations++;
            if (!_hasReentered && ShouldReenter?.Invoke() == true)
            {
                _hasReentered = true;
                OnReenter?.Invoke();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Reproduces the finding that a reentrant write from inside a user enumerable commits a newer
    /// baseline which the outer operation then overwrites. Reproduces on a single thread, with no
    /// artificially held window: the reentrancy is the enumerable's own code running where the
    /// reconciler invokes it.
    ///
    /// The re-entry lands in the reconcile phase, specifically in the scan of the committed baseline
    /// the reconcile performs on its way in. That position is the whole point of the test and is
    /// pinned by two guards below: the terminal has already stored the outer value (so this is not
    /// the capture phase, where the protocol claims the proposed component before the terminal
    /// runs), and the outer baseline has not been committed yet (so the overwrite is still ahead).
    /// The capture phase was measured and does not reproduce this: a re-entry there commits its
    /// baseline before the outer reconcile reads it, so the outer diffs correctly and the graph
    /// stays consistent. Anyone changing which values the reconcile scans, or how often, should
    /// expect this test to fail loudly rather than quietly stop exercising anything.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheSamePropertyWhileItIsScanned_ThenTheOuterWriteDoesNotOverwriteTheNewerBaseline()
    {
        // Arrange: the committed value is a user enumerable, so the reconcile of the next write runs
        // user code after the terminal stored and before the new baseline is committed.
        var context = CreateContext();
        var holder = new EnumerableChildrenHolder(context);
        var firstChild = new Person { FirstName = "first" };
        var outerChild = new Person { FirstName = "outer" };
        var nestedChild = new Person { FirstName = "nested" };

        var committedValue = new ScanHookEnumerable([firstChild]);
        holder.Children = committedValue;

        var outerValue = new List<Person> { outerChild };
        object? fieldAtReentry = null;
        object? baselineAtReentry = null;

        committedValue.ShouldReenter = () => !ReferenceEquals(holder.Children, committedValue);
        committedValue.OnReenter = () =>
        {
            fieldAtReentry = holder.Children;
            baselineAtReentry = GetCommittedBaseline(context, holder);
            holder.Children = new List<Person> { nestedChild };
        };

        // Act
        holder.Children = outerValue;

        // Assert: the re-entry happened, and it happened in the phase this test is about. Either
        // guard failing means the instrument moved, not that the behaviour changed.
        Assert.True(committedValue.HasReentered,
            $"the reentrant write never ran; the committed value was scanned {committedValue.Enumerations} times");
        Assert.Same(outerValue, fieldAtReentry);
        Assert.Same(committedValue, baselineAtReentry);

        // The nested write is the newer one and its value is what the property holds afterwards.
        Assert.Equal([nestedChild], holder.Children!);
        Assert.Same(context, ((IInterceptorSubject)nestedChild).TryGetContext());

        // The outer write committed its own baseline over the newer one, then published an edge
        // for a value the property no longer holds.
        Assert.True(((IInterceptorSubject)outerChild).TryGetContext() is null,
            "the outer write overwrote the newer baseline committed by the reentrant write and " +
            "published an ownership edge for a value the committed property no longer holds, so " +
            $"'{outerChild.FirstName}' is attached with {((IInterceptorSubject)outerChild).GetReferenceCount()} " +
            "incoming edge(s) while unreachable from the subject graph");
    }

    /// <summary>
    /// An explicit attach claims the whole prospective component before it seeds the root, so
    /// between those two steps the root is attached to the context and not yet in its ownership
    /// graph. The seed reads the root's structural getters and scans their values at callback depth
    /// zero, which is where a user enumerable's own code runs, so a structural write can arrive in
    /// exactly that window. This is the only shape in the tree that reaches the write protocol's
    /// claimed-but-unpublished arm, and it is what that arm is for: there is no owner to reconcile
    /// against yet, and the seed that follows reads the committed value anyway.
    ///
    /// The re-entry is positioned by phase, not by an enumeration ordinal, and the guard below
    /// asserts the phase. The same enumerable is also scanned by the discovery walk that runs before
    /// the claim, where the root is still unattached, so an ordinal armed against today's scan count
    /// would fire in the wrong one.
    /// </summary>
    [Fact]
    public void WhenAUserEnumerableWritesTheRootWhileTheAttachSeedsIt_ThenTheWritePassesThroughAndTheAttachCompletes()
    {
        // Arrange: an unattached root whose structural value runs user code when it is scanned.
        var context = CreateContext();
        var seededChild = new Person { FirstName = "seeded" };
        var lateChild = new Person { FirstName = "late" };
        var holder = new EnumerableChildrenHolder();

        var initialValue = new ScanHookEnumerable([seededChild]);
        holder.Children = initialValue;

        var lateValue = new List<Person> { lateChild };
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var wasClaimedButUnpublished = false;

        initialValue.ShouldReenter = () =>
            ((IInterceptorSubject)holder).TryGetContext() is not null && !lifecycle.Graph.IsOwned(holder);

        initialValue.OnReenter = () =>
        {
            wasClaimedButUnpublished = true;
            holder.Children = lateValue;
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));

        // Assert: the re-entry happened, and it happened in the window this test is about.
        Assert.Null(exception);
        Assert.True(wasClaimedButUnpublished,
            $"the reentrant write never ran in the seeding window; the initial value was scanned " +
            $"{initialValue.Enumerations} times");

        // The write passed through to the backing field rather than being rejected or reconciled.
        Assert.Same(lateValue, holder.Children);

        // The attach still completed, with the seed's own scan result attached through its edge.
        Assert.Same(context, ((IInterceptorSubject)holder).TryGetContext());
        Assert.True(lifecycle.Graph.IsOwned(holder));
        Assert.Same(context, ((IInterceptorSubject)seededChild).TryGetContext());
        Assert.Equal(1, ((IInterceptorSubject)seededChild).GetReferenceCount());

        // Nothing claimed the value the pass-through stored: the seed had already scanned the
        // committed value, so no edge is published for this one.
        Assert.Null(((IInterceptorSubject)lateChild).TryGetContext());
    }
}
