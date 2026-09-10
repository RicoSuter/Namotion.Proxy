using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class SubjectAttachmentTests
{
    private sealed class LifecycleProbe : ILifecycleInterceptor
    {
        public int AttachCount;
        public int DetachCount;

        private readonly object _structuralWriteGate = new();

        public void EnterStructuralWriteGate() => Monitor.Enter(_structuralWriteGate);

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        // No ownership work in the probe: publishing the metadata is the whole admission.
        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        // A minimal faithful lifecycle: it applies the documented root-anchor rules through Core's
        // own helpers and counts each policy entry, which is where a real implementation does its
        // graph work.
        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            InterceptorSubjectExtensions.ApplyRootAnchor(subject, context, anchor);
            AttachCount++;
        }

        // The probe tracks no structural edges, so its detach policy is the minimal one: clear the
        // anchor, keep the attachment.
        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);
            executor.TryUpdateAttachment(revision, attachedContext, SubjectAttachmentAnchorKind.None, out _);
            DetachCount++;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }

    private static IInterceptorExecutor GetExecutor(IInterceptorSubject subject)
    {
        return subject.Executor;
    }

    private static InterceptorSubjectContext CreateContextWithProbe(out LifecycleProbe probe)
    {
        var context = InterceptorSubjectContext.Create();
        probe = new LifecycleProbe();
        context.AddService(probe);
        return context;
    }

    [Fact]
    public void WhenSubjectIsUnattached_ThenTryGetContextReturnsNull()
    {
        // Arrange
        var subject = new Car();

        // Act
        var context = subject.TryGetContext();

        // Assert
        Assert.Null(context);
    }

    [Fact]
    public void WhenSubjectIsUnattached_ThenGetContextThrows()
    {
        // Arrange
        var subject = new Car();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.GetContext());
    }

    [Fact]
    public void WhenAttachingUnattachedSubject_ThenSubjectIsExplicitlyAttachedAndLifecycleRuns()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, subject.TryGetContext());
        Assert.Same(context, subject.GetContext());
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
        Assert.Equal(1, probe.AttachCount);
    }

    [Fact]
    public void WhenAttachingSubjectAttachedWithNoneAnchorToSameContext_ThenAnchorIsPromotedToExplicit()
    {
        // Arrange
        var context = CreateContextWithProbe(out _);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAttachmentAnchorKind.None, out _));

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenAttachingSubjectAttachedWithProvisionalAnchorToSameContext_ThenAnchorIsPromotedToExplicit()
    {
        // Arrange
        var context = CreateContextWithProbe(out _);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAttachmentAnchorKind.Provisional, out _));

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenAttachingAlreadyExplicitlyAttachedSubjectToSameContext_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(context);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(context));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
        Assert.Equal(1, probe.AttachCount);
    }

    [Fact]
    public void WhenAttachingSubjectAttachedToDifferentContext_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var firstContext = CreateContextWithProbe(out var firstProbe);
        var secondContext = CreateContextWithProbe(out var secondProbe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(firstContext);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(secondContext));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(firstContext, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
        Assert.Equal(1, firstProbe.AttachCount);
        Assert.Equal(0, secondProbe.AttachCount);
    }

    [Fact]
    public void WhenDetachingExplicitlyAttachedSubject_ThenAnchorClearsAndLifecycleDetaches()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(context);
        var revisionBefore = executor.AttachmentRevision;

        // Act
        subject.DetachFromContext(context);

        // Assert: only the anchor clears in this stage; the exact context is cleared once
        // structural edges become authoritative.
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.Same(context, executor.AttachedContext);
        Assert.True(executor.AttachmentRevision > revisionBefore);
        Assert.Equal(1, probe.DetachCount);
    }

    [Fact]
    public void WhenDetachingUnattachedSubject_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.DetachFromContext(context));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Null(executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.Equal(0, probe.DetachCount);
    }

    [Fact]
    public void WhenDetachingSubjectWithProvisionalAnchor_ThenTheAnchorIsCleared()
    {
        // Arrange: a caller that created a provisional root and then failed to give it a
        // supporting edge needs a way to undo it, which is the connector failure path.
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAttachmentAnchorKind.Provisional, out _));
        var revisionBefore = executor.AttachmentRevision;

        // Act
        subject.DetachFromContext(context);

        // Assert: validation admits the call, so it reaches the lifecycle, whose policy here is to
        // clear the anchor and keep the attachment.
        Assert.Equal(1, probe.DetachCount);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.True(executor.AttachmentRevision > revisionBefore);
    }

    [Fact]
    public void WhenDetachingSubjectWhoseAnchorWasAlreadyConsumed_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange: an edge has adopted the subject, so there is no anchor left to clear and the
        // subject is held by the graph rather than by the caller.
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);
        Assert.True(executor.TryUpdateAttachment(executor.AttachmentRevision, context, SubjectAttachmentAnchorKind.None, out _));
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.DetachFromContext(context));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(0, probe.DetachCount);
    }

    [Fact]
    public void WhenDetachingExplicitlyAttachedSubjectFromDifferentContext_ThenThrowsBeforeAnyStateChange()
    {
        // Arrange
        var attachedContext = CreateContextWithProbe(out var probe);
        var otherContext = CreateContextWithProbe(out _);
        var subject = new Car();
        var executor = GetExecutor(subject);
        subject.AttachToContext(attachedContext);
        var revisionBefore = executor.AttachmentRevision;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.DetachFromContext(otherContext));
        Assert.Equal(revisionBefore, executor.AttachmentRevision);
        Assert.Same(attachedContext, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
        Assert.Equal(0, probe.DetachCount);
    }

    [Fact]
    public void WhenSubjectIsConstructedWithContext_ThenItIsProvisionallyAttached()
    {
        // Arrange & Act: the context-taking constructor routes through the context's lifecycle
        // with a provisional anchor.
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car(context);
        var executor = GetExecutor(subject);

        // Assert
        Assert.Equal(1, probe.AttachCount);
        Assert.Same(context, subject.TryGetContext());
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenAttachingSubjectConstructedWithContext_ThenAnchorIsPromotedToExplicit()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car(context);
        var executor = GetExecutor(subject);

        // Act
        subject.AttachToContext(context);

        // Assert: the explicit attach promotes the constructor's provisional anchor, entering the
        // lifecycle's policy a second time.
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
        Assert.Equal(2, probe.AttachCount);
    }

    [Fact]
    public void WhenReadingTheAttachedContext_ThenNothingIsAllocated()
    {
        // Arrange: the context read is the ownership predicate across the codebase, so it must
        // stay allocation-free for attached and unattached subjects alike.
        var unattached = new Car();
        var attached = new Car();
        attached.AttachToContext(InterceptorSubjectContext.Create());
        for (var i = 0; i < 100; i++)
        {
            unattached.TryGetContext();
            attached.TryGetContext();
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            unattached.TryGetContext();
            attached.TryGetContext();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenTheAttachmentMonitorIsHeldByACommit_ThenAttachmentReadsStillComplete()
    {
        // Arrange: the structural terminal holds the attachment monitor through the commit, so a
        // backing write delegate that blocks keeps it held for as long as the test wants. The
        // attachment reads below only complete while it is held if they take no lock, which is
        // what lets consumers call them from inside their own locks without deadlocking.
        var subject = new StructuralHolder();
        var executor = GetExecutor(subject);
        using var insideCommit = new ManualResetEventSlim(false);
        using var resumeCommit = new ManualResetEventSlim(false);
        var commitResumedInTime = false;

        var writer = new Thread(() => executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) =>
        {
            insideCommit.Set();
            commitResumedInTime = resumeCommit.Wait(TimeSpan.FromSeconds(10));
        }));
        writer.Start();
        Assert.True(insideCommit.Wait(TimeSpan.FromSeconds(10)));

        // Act: read from inside a lock the caller already holds, like consumers do.
        IInterceptorSubjectContext? attachedContext;
        SubjectAttachmentAnchorKind anchor;
        long revision;
        var callerLock = new object();
        lock (callerLock)
        {
            attachedContext = subject.TryGetContext();
            anchor = executor.AttachmentAnchor;
            revision = executor.AttachmentRevision;
        }
        resumeCommit.Set();
        writer.Join();

        // Assert: the reads completed while the monitor was held (the commit was still waiting
        // when they finished) and observed the pre-commit state.
        Assert.True(commitResumedInTime);
        Assert.Null(attachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchor);
        Assert.Equal(0, revision);
    }

    [Fact]
    public void WhenAttachingAForeignContextImplementation_ThenTheAttachThrowsBeforeAnyStateChange()
    {
        // Arrange: interceptor chains compile inside InterceptorSubjectContext, so a hand-rolled
        // implementation of the interface would attach, report itself through TryGetContext(),
        // and intercept nothing. The raw seam rejects it loudly instead.
        var subject = new Car();
        var executor = GetExecutor(subject);
        var foreignContext = new ForeignContext();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => subject.AttachToContext(foreignContext));
        Assert.Contains("InterceptorSubjectContext.Create()", exception.Message);
        Assert.Null(subject.TryGetContext());
        Assert.Equal(0, executor.AttachmentRevision);
    }

    [Fact]
    public void WhenAttachingOnALifecycleFreeContext_ThenOnlyTheRootAttachesAndDetachReleasesIt()
    {
        // Arrange: without a lifecycle the attach is root-only. The context's services become
        // resolvable from the root, but no descent runs, so a referenced child stays unattached.
        var context = InterceptorSubjectContext.Create();
        var root = new StructuralHolder { Child = new StructuralHolder() };
        var subject = (IInterceptorSubject)root;

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, subject.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, subject.Executor.AttachmentAnchor);
        Assert.Null(((IInterceptorSubject)root.Child!).TryGetContext());

        // Act: the lifecycle-free detach clears the attachment entirely, because no edge model
        // exists that could still hold the subject.
        subject.DetachFromContext(context);

        // Assert
        Assert.Null(subject.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.None, subject.Executor.AttachmentAnchor);
    }

    /// <summary>
    /// A hand-rolled context implementation, which the attach seam must reject; see
    /// WhenAttachingAForeignContextImplementation_ThenTheAttachThrowsBeforeAnyStateChange.
    /// </summary>
    private sealed class ForeignContext : IInterceptorSubjectContext
    {
        public void AddService<TService>(TService service) => throw new NotSupportedException();

        public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists) => throw new NotSupportedException();

        public TInterface? TryGetService<TInterface>() => default;

        public System.Collections.Immutable.ImmutableArray<TInterface> GetServices<TInterface>() => [];
    }

    [Fact]
    public void WhenDetachingAndReattaching_ThenLifecycleRunsAgain()
    {
        // Arrange
        var context = CreateContextWithProbe(out var probe);
        var subject = new Car();
        var executor = GetExecutor(subject);

        // Act
        subject.AttachToContext(context);
        subject.DetachFromContext(context);
        subject.AttachToContext(context);

        // Assert: the second attach promotes the leftover None anchor and re-adds the fallback.
        Assert.Equal(2, probe.AttachCount);
        Assert.Equal(1, probe.DetachCount);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
    }
}
