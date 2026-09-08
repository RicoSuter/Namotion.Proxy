using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class AdversarialRollbackTests
{
    [Fact]
    public void WhenAnAttachCallbackFailsAfterABackEdgeAttachedTheRoot_ThenTheCommittedComponentRemainsDetachable()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

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

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses childB", exception.Message);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        SupportContractAssertions.Settled(context, [root], root, childA, childB);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, childA, childB);
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
    public void WhenAnAttachCallbackFailsAfterConsumingAProvisionalAnchor_ThenTheAdoptedSubtreeRemainsCommitted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

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

        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Same(context, grandchildSubject.TryGetContext());

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorAtRefusal);
        Assert.Equal(SubjectAttachmentAnchorKind.None, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Empty(detached);
        SupportContractAssertions.Settled(context, [root], root, provisionalRoot, grandchild, refused);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, provisionalRoot, grandchild, refused);
    }

    [Fact]
    public void WhenAnAttachCallbackFailsAfterConsumingSeveralProvisionalAnchors_ThenEverySubtreeRemainsCommitted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

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
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.Equal([SubjectAttachmentAnchorKind.None, SubjectAttachmentAnchorKind.None], anchorsAtRefusal);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)first).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)second).Executor.AttachmentAnchor);
        IInterceptorSubject[] subjects = [root, first, second, first.Father!, second.Father!, refused];
        SupportContractAssertions.Settled(context, [root], subjects);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], subjects);
    }

    [Fact]
    public void WhenANestedAttachCallbackFails_ThenBothRootsCommitBeforeTheOuterDrainThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

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

        // Assert
        Assert.Null(innerException);
        Assert.IsType<InvalidOperationException>(outerException);
        Assert.Equal("callback refuses X", outerException.Message);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)innerRoot).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)outerRoot).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)innerProvisional).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)outerProvisional).Executor.AttachmentAnchor);
        IInterceptorSubject[] subjects = [innerRoot, outerRoot, innerProvisional, outerProvisional, innerProvisional.Father!, refused];
        SupportContractAssertions.Settled(context, [innerRoot, outerRoot], subjects);
        innerRoot.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [outerRoot], subjects);
        outerRoot.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], subjects);
    }

    [Fact]
    public void WhenAnAttachCallbackFailsWithOutsideSupport_ThenBothIncomingEdgesRemainCommitted()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

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

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.Equal([SubjectAttachmentAnchorKind.Provisional, SubjectAttachmentAnchorKind.None], anchorsAroundTheOutsideEdge);
        Assert.Equal(SubjectAttachmentAnchorKind.None, provisionalSubject.Executor.AttachmentAnchor);
        Assert.Equal(2, provisionalSubject.GetReferenceCount());
        SupportContractAssertions.Settled(context, [root, outside], root, outside, provisional, refused);
        root.DetachFromContext(context);
        Assert.Equal(1, provisionalSubject.GetReferenceCount());
        SupportContractAssertions.Settled(context, [outside], root, outside, provisional, refused);
        outside.Father = null;
        SupportContractAssertions.Settled(context, [outside], root, outside, provisional, refused);
        outside.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], root, outside, provisional, refused);
    }

    [Fact]
    public void WhenAnAttachCallbackPromotesAProvisionalRootBeforeFailing_ThenTheExplicitAnchorSurvivesParentRemoval()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var provisional = new Person(context) { FirstName = "P", Father = new Person { FirstName = "G" } };
        var refused = new Person { FirstName = "X" };
        var promotionReturned = false;

        var root = new EnumerableChildrenHolder();
        root.Children = new Person[] { provisional, refused };

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, refused))
            {
                provisional.AttachToContext(context);
                promotionReturned = true;
                throw new InvalidOperationException("callback refuses X");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback refuses X", exception.Message);
        Assert.True(promotionReturned);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        IInterceptorSubject[] subjects = [root, provisional, provisional.Father!, refused];
        SupportContractAssertions.Settled(context, [root, provisional], subjects);
        root.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [provisional], subjects);
        Assert.Equal(0, provisional.GetReferenceCount());
        provisional.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], subjects);
    }

    /// <summary>
    /// A user enumerable that runs one action the first time it is scanned while the condition
    /// holds. The condition names the phase the test needs (the seed's scan once the root is
    /// claimed, or a callback after the attachment) rather than an enumeration ordinal,
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
}
