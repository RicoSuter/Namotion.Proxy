using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Adversarial review probe for RollbackRejectedAttach: it removes the root's committed edges while
/// the root still carries its anchor, so a back edge that already attached the root reports the root
/// as "still held" and survives the drain. The anchor is only cleared afterwards, and the final
/// claim hand-back skips anything the graph still owns.
/// </summary>
public class AdversarialRollbackTests
{
    [Fact]
    public void WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootIsFullyRolledBack()
    {
        // Arrange: root -> childA -> root is the everyday back reference, and childB is what the
        // attach callback refuses. The refusal happens after the back edge already published the
        // root into the graph.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person { FirstName = "root" };
        var childA = new Person { FirstName = "A" };
        var childB = new Person { FirstName = "B" };

        root.Father = childA;
        childA.Father = root;
        root.Mother = childB;

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, childB))
            {
                throw new InvalidOperationException("callback refuses childB");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert: the attach was rejected...
        Assert.NotNull(exception);

        // ...so nothing it touched may stay behind. The root is the one the rollback exists for.
        var graph = ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!).Graph;
        var rootSubject = (IInterceptorSubject)root;

        Assert.False(graph.IsOwned(root),
            "the root is still owned by the graph after a rejected attach");
        Assert.Null(rootSubject.TryGetContext());
        Assert.Null(((IInterceptorSubject)childA).TryGetContext());
        Assert.Null(((IInterceptorSubject)childB).TryGetContext());
    }

    [Fact]
    public void WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootCanStillBeDetached()
    {
        // Arrange: same shape, but this asserts the escape hatch the rollback documentation
        // promises ("the root is still attached and detachable rather than stripped").
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person { FirstName = "root" };
        var childA = new Person { FirstName = "A" };
        var childB = new Person { FirstName = "B" };

        root.Father = childA;
        childA.Father = root;
        root.Mother = childB;

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, childB))
            {
                throw new InvalidOperationException("callback refuses childB");
            }
        };

        Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Act & Assert: either the root is gone, or it must be detachable. Anything else is a
        // subject stuck in the context forever.
        var rootSubject = (IInterceptorSubject)root;
        if (rootSubject.TryGetContext() is null)
        {
            return;
        }

        var detachException = Record.Exception(() => rootSubject.DetachFromContext(context));
        Assert.Null(detachException);
        Assert.Null(rootSubject.TryGetContext());
    }

    [Fact]
    public void WhenARejectedAttachConsumedAProvisionalAnchor_ThenTheAnchoredSubjectAndItsSubtreeStayAttached()
    {
        // Arrange: a subject constructed with the context is a provisional root, and it holds a
        // grandchild through an ordinary edge. A separate graph references that root through a
        // property seeded ahead of the one holding the subject the attach callback refuses, so the
        // attach consumes the provisional anchor first and is rejected afterwards.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var provisionalRoot = new Person(context) { FirstName = "P" };
        var grandchild = new Person { FirstName = "G" };
        provisionalRoot.Father = grandchild;

        var refused = new Person { FirstName = "X" };
        var root = new Person { FirstName = "R", Father = provisionalRoot, Mother = refused };

        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var provisionalSubject = (IInterceptorSubject)provisionalRoot;
        var grandchildSubject = (IInterceptorSubject)grandchild;
        var detached = new List<IInterceptorSubject>();
        SubjectAttachmentAnchorKind? anchorAtRefusal = null;
        lifecycle.SubjectDetaching += change => detached.Add(change.Subject);
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                anchorAtRefusal = provisionalSubject.Executor.AttachmentAnchor;
                throw new InvalidOperationException("callback refuses X");
            }
        };

        var graph = lifecycle.Graph;
        var before = Describe(graph, provisionalSubject) + "; grandchild " + Describe(graph, grandchildSubject);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Same(context, grandchildSubject.TryGetContext());

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert: the attach was rejected after it had consumed the anchor, and what was attached
        // before it began is exactly as it was. The provisional root keeps its anchor and its
        // ownership record, the grandchild is still held through it, and neither saw a detach.
        Assert.NotNull(exception);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorAtRefusal);
        var after = Describe(graph, provisionalSubject) + "; grandchild " + Describe(graph, grandchildSubject);
        Assert.True(ReferenceEquals(context, provisionalSubject.TryGetContext()),
            $"the rejected attach evicted the provisional root: before [{before}], after [{after}]");
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, provisionalSubject.Executor.AttachmentAnchor);
        Assert.True(graph.IsOwned(provisionalRoot));
        Assert.Equal(0, provisionalSubject.GetReferenceCount());
        Assert.DoesNotContain(provisionalSubject, detached);

        Assert.True(ReferenceEquals(context, grandchildSubject.TryGetContext()),
            $"the eviction cascaded to the grandchild: before [{before}], after [{after}]");
        Assert.True(graph.IsOwned(grandchild));
        Assert.Equal(1, grandchildSubject.GetReferenceCount());
        Assert.DoesNotContain(grandchildSubject, detached);

        Assert.Null(((IInterceptorSubject)root).TryGetContext());
        Assert.Null(((IInterceptorSubject)refused).TryGetContext());
    }

    [Fact]
    public void WhenARejectedAttachConsumedSeveralProvisionalAnchors_ThenEveryOneIsHandedBack()
    {
        // Arrange: two provisional roots, each holding a grandchild, are referenced ahead of the
        // subject the callback refuses, so the attach consumes both anchors before it is rejected.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var first = new Person(context) { FirstName = "P1", Father = new Person { FirstName = "G1" } };
        var second = new Person(context) { FirstName = "P2", Father = new Person { FirstName = "G2" } };
        var refused = new Person { FirstName = "X" };
        var root = new Person { FirstName = "R", Father = first, Mother = second, Children = [refused] };

        var anchorsAtRefusal = new List<SubjectAttachmentAnchorKind>();
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                anchorsAtRefusal.Add(((IInterceptorSubject)first).Executor.AttachmentAnchor);
                anchorsAtRefusal.Add(((IInterceptorSubject)second).Executor.AttachmentAnchor);
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.NotNull(exception);
        Assert.Equal([SubjectAttachmentAnchorKind.None, SubjectAttachmentAnchorKind.None], anchorsAtRefusal);
        foreach (var provisionalRoot in new[] { first, second })
        {
            var subject = (IInterceptorSubject)provisionalRoot;
            Assert.Same(context, subject.TryGetContext());
            Assert.Equal(SubjectAttachmentAnchorKind.Provisional, subject.Executor.AttachmentAnchor);
            Assert.Equal(0, subject.GetReferenceCount());
            Assert.Same(context, ((IInterceptorSubject)provisionalRoot.Father!).TryGetContext());
        }

        Assert.Null(((IInterceptorSubject)root).TryGetContext());
        Assert.Null(((IInterceptorSubject)refused).TryGetContext());
    }

    [Fact]
    public void WhenARejectedAttachNestsInsideAnAcceptedOne_ThenEachHandsBackOnlyItsOwnAnchors()
    {
        // Arrange: the outer root's user collection attaches a second root while the outer seed
        // scans it, which is the callback-depth-zero window an explicit attach has. The inner
        // attach consumes one provisional anchor and is refused; the outer consumes another and
        // is accepted.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var innerProvisional = new Person(context) { FirstName = "PI", Father = new Person { FirstName = "GI" } };
        var outerProvisional = new Person(context) { FirstName = "PO" };
        var refused = new Person { FirstName = "X" };
        var innerRoot = new Person { FirstName = "RI", Father = innerProvisional, Mother = refused };

        var outerRoot = new EnumerableChildrenHolder();
        Exception? innerException = null;
        outerRoot.Children = new PhaseHookEnumerable(
            [outerProvisional],
            shouldRun: () => ((IInterceptorSubject)outerRoot).TryGetContext() is not null,
            onRun: () => innerException = Record.Exception(() => ((IInterceptorSubject)innerRoot).AttachToContext(context)));

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var outerException = Record.Exception(() => ((IInterceptorSubject)outerRoot).AttachToContext(context));

        // Assert: the inner attach was the one refused and the outer one landed
        Assert.Null(outerException);
        Assert.NotNull(innerException);

        var innerSubject = (IInterceptorSubject)innerProvisional;
        Assert.Same(context, innerSubject.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, innerSubject.Executor.AttachmentAnchor);
        Assert.Equal(0, innerSubject.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)innerProvisional.Father!).TryGetContext());
        Assert.Null(((IInterceptorSubject)innerRoot).TryGetContext());
        Assert.Null(((IInterceptorSubject)refused).TryGetContext());

        var outerSubject = (IInterceptorSubject)outerProvisional;
        Assert.Same(context, outerSubject.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.None, outerSubject.Executor.AttachmentAnchor);
        Assert.Equal(1, outerSubject.GetReferenceCount());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)outerRoot).Executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenAnEdgeFromOutsideTheRejectedComponentConsumedTheAnchor_ThenTheAnchorStaysConsumed()
    {
        // Arrange: while the root's seed scans its user collection, that collection gives an
        // explicit root of its own an edge to the provisional subject. That edge is not part of
        // the rejected component, so it survives the rollback and supports the subject exactly as
        // the consuming edge would have; an anchor handed back over it would make the subject a
        // root that the explicit root's own edge removal never releases.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var provisional = new Person(context) { FirstName = "P" };
        var outside = new Person { FirstName = "E" };
        ((IInterceptorSubject)outside).AttachToContext(context);
        var refused = new Person { FirstName = "X" };

        var provisionalSubject = (IInterceptorSubject)provisional;
        var anchorsAroundTheOutsideEdge = new List<SubjectAttachmentAnchorKind>();
        var root = new EnumerableChildrenHolder();
        root.Children = new PhaseHookEnumerable(
            [provisional, refused],
            shouldRun: () => ((IInterceptorSubject)root).TryGetContext() is not null,
            onRun: () =>
            {
                anchorsAroundTheOutsideEdge.Add(provisionalSubject.Executor.AttachmentAnchor);
                outside.Father = provisional;
                anchorsAroundTheOutsideEdge.Add(provisionalSubject.Executor.AttachmentAnchor);
            });

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert: the outside edge consumed the anchor mid-attach, and after the rollback the
        // subject is held by that edge alone
        Assert.NotNull(exception);
        Assert.Equal([SubjectAttachmentAnchorKind.Provisional, SubjectAttachmentAnchorKind.None], anchorsAroundTheOutsideEdge);
        Assert.Same(context, provisionalSubject.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.None, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Equal(1, provisionalSubject.GetReferenceCount());
        Assert.Null(((IInterceptorSubject)root).TryGetContext());
        Assert.Null(((IInterceptorSubject)refused).TryGetContext());

        // ...so removing that edge releases it, as it would have without the rejected attach
        outside.Father = null;
        Assert.Null(provisionalSubject.TryGetContext());
    }

    [Fact]
    public void WhenTheProvisionalRootIsPromotedWhileTheRollbackDrains_ThenTheExplicitAnchorSurvives()
    {
        // Arrange: the root's user collection promotes the provisional subject to an explicit root
        // the first time it is scanned after the refusal, which is the rollback's own scan of the
        // committed baselines. The rollback hands the provisional anchor back before that scan,
        // and an anchor that turned explicit afterwards must stay explicit.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var provisional = new Person(context) { FirstName = "P", Father = new Person { FirstName = "G" } };
        var refused = new Person { FirstName = "X" };
        var refusalHappened = false;

        var root = new EnumerableChildrenHolder();
        root.Children = new PhaseHookEnumerable(
            [provisional, refused],
            shouldRun: () => refusalHappened,
            onRun: () => ((IInterceptorSubject)provisional).AttachToContext(context));

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                refusalHappened = true;
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.NotNull(exception);
        var provisionalSubject = (IInterceptorSubject)provisional;
        Assert.Same(context, provisionalSubject.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Equal(0, provisionalSubject.GetReferenceCount());
        Assert.Same(context, ((IInterceptorSubject)provisional.Father!).TryGetContext());
        Assert.Null(((IInterceptorSubject)root).TryGetContext());
    }

    /// <summary>
    /// A user enumerable that runs one action the first time it is scanned while the condition
    /// holds. The condition names the phase the test needs (the seed's scan once the root is
    /// claimed, or the rollback's scan after the refusal) rather than an enumeration ordinal,
    /// because how often a value is scanned is an implementation detail.
    /// </summary>
    private sealed class PhaseHookEnumerable(Person[] items, Func<bool> shouldRun, Action onRun) : IEnumerable<Person>
    {
        private bool _hasRun;

        public IEnumerator<Person> GetEnumerator()
        {
            if (!_hasRun && shouldRun())
            {
                _hasRun = true;
                onRun();
            }

            return ((IEnumerable<Person>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string Describe(OwnershipGraph graph, IInterceptorSubject subject)
    {
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        return $"attached={attachedContext is not null}, anchor={anchor}, owned={graph.IsOwned(subject)}, " +
               $"referenceCount={subject.GetReferenceCount()}";
    }
}
