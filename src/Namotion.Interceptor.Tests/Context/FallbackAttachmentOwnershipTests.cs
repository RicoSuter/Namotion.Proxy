using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

public class FallbackAttachmentOwnershipTests
{
    [Fact]
    public async Task WhenRemoveRunsDuringAttachCallbacks_ThenTheEdgeIsStillRemoved()
    {
        // Arrange: the attach callback parks, which is what a real attach does when the remover
        // already holds the lifecycle lock it needs. The edge is published by then, so a remover
        // arriving here finds a record whose attach has not completed.
        var parentContext = InterceptorSubjectContext.Create();
        using var attachStarted = new ManualResetEventSlim(false);
        using var releaseAttach = new ManualResetEventSlim(false);
        var interceptor = new ParkingLifecycleInterceptor(attachStarted, releaseAttach);
        parentContext.AddService<ILifecycleInterceptor>(interceptor);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        // Act
        var add = Task.Factory.StartNew(
            () => childContext.AddFallbackContext(parentContext), TaskCreationOptions.LongRunning);

        Assert.True(attachStarted.Wait(TimeSpan.FromSeconds(30)),
            "The attach callback never started, so the removal below would not race it.");

        var removed = childContext.RemoveFallbackContext(parentContext);
        releaseAttach.Set();
        var added = await add;

        // Assert: both calls report success, and the edge really is gone once everything settles.
        // The child executor owns no services, so resolving nothing means the fallback is gone.
        Assert.True(added);
        Assert.True(removed);
        await AsyncTestHelpers.WaitUntilAsync(
            () => childContext.GetServices<ILifecycleInterceptor>().IsEmpty,
            message: "The fallback edge survived a removal that raced the attach callbacks.");

        Assert.Equal(1, interceptor.AttachCount);
        Assert.Equal(1, interceptor.DetachCount);
    }

    [Fact]
    public void WhenInterceptorIsRegisteredAfterAttach_ThenItIsNotNotifiedOnDetach()
    {
        // Arrange: detach must replay what attach resolved, not what the parent happens to hold
        // when the removal runs.
        var parentContext = InterceptorSubjectContext.Create();
        var atAttachTime = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(atAttachTime);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));

        var afterAttach = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(afterAttach);

        // Act
        Assert.True(childContext.RemoveFallbackContext(parentContext));

        // Assert
        Assert.Equal(1, atAttachTime.DetachCount);
        Assert.Equal(0, afterAttach.DetachCount);
    }

    [Fact]
    public void WhenDetachInterceptorThrows_ThenLaterInterceptorsStillReceiveDetach()
    {
        // Arrange
        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService<ILifecycleInterceptor>(new ThrowingDetachInterceptor());
        var following = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(following);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));

        // Act & Assert: the failure surfaces, the interceptor behind it still heard about the
        // detach, and the edge came out regardless.
        var exception = Assert.Throws<InvalidOperationException>(
            () => childContext.RemoveFallbackContext(parentContext));

        Assert.Equal("Detach failed.", exception.Message);
        Assert.Equal(1, following.DetachCount);
        Assert.True(childContext.GetServices<ILifecycleInterceptor>().IsEmpty);
    }

    [Fact]
    public void WhenAttachInterceptorThrows_ThenOnlyTheInvokedPrefixIsDetached()
    {
        // Arrange: the first interceptor throws on attach, so the second never receives one and
        // must not receive a detach either. The thrower itself does: it may have mutated itself
        // before raising, so the prefix is inclusive of it.
        var parentContext = InterceptorSubjectContext.Create();
        var thrower = new ThrowingAttachInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(thrower);
        var neverAttached = new CountingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(neverAttached);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.Throws<InvalidOperationException>(() => childContext.AddFallbackContext(parentContext));

        // Act
        Assert.True(childContext.RemoveFallbackContext(parentContext));

        // Assert
        Assert.Equal(1, thrower.DetachCount);
        Assert.Equal(0, neverAttached.DetachCount);
    }

    [Fact]
    public void WhenChainBecomesCyclicAfterAttach_ThenTheEdgeIsRemovedAndTheCallThrows()
    {
        // Arrange: the record has to be captured non-empty and the chain has to turn cyclic
        // afterwards. On a pure two-executor cycle both records are empty, the detach loop runs
        // zero times, and the removal succeeds silently.
        //
        // The recorded interceptor has to resolve through the subject's own context on detach,
        // which is what a real one does to reach its handlers. Nothing on the removal path
        // resolves any more, so an inert interceptor could not raise here at all.
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService<ILifecycleInterceptor>(new ResolvingLifecycleInterceptor());

        var first = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var second = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        Assert.True(second.AddFallbackContext(rootContext));
        Assert.True(first.AddFallbackContext(second));
        Assert.True(second.RemoveFallbackContext(rootContext));
        Assert.True(second.AddFallbackContext(first));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => first.RemoveFallbackContext(second));
        Assert.False(HasFallback(first, second));
    }

    [Fact]
    public void WhenAttachResolveThrows_ThenNoEdgeIsRegistered()
    {
        // Arrange: a pure cycle between two contexts, so resolving through it raises.
        var first = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var second = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var third = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        Assert.True(first.AddFallbackContext(second));
        Assert.True(second.AddFallbackContext(first));

        // Act & Assert: third's add resolves through the existing cycle and must commit nothing.
        Assert.Throws<InvalidOperationException>(() => third.AddFallbackContext(first));
        Assert.False(HasFallback(third, first));
    }

    [Fact]
    public void WhenAddClosesADelegationCycle_ThenTheEdgeIsRegisteredAndNothingThrows()
    {
        // Arrange: closing the circle needs both ends empty, so the resolve returns nothing and
        // the callback loop has no iterations. This raised before the resolve moved ahead of the
        // publish, so it is a declared behaviour change.
        var first = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        var second = ((IInterceptorSubject)new ContextProbeSubject()).Context;
        Assert.True(second.AddFallbackContext(first));

        // Act
        var added = first.AddFallbackContext(second);

        // Assert
        Assert.True(added);
        Assert.True(HasFallback(first, second));
    }

    [Fact]
    public void WhenFallbackIsMutated_ThenBothCallbackSetsSeeTheEdge()
    {
        // Arrange: both orderings are forced, not chosen. The callbacks resolve their handlers
        // through the subject's own context, which finds nothing unless the edge is in place, so
        // attach must run after the publish and detach before the removal. Inverting either one
        // silently drops every lifecycle event for the subtree.
        var parentContext = InterceptorSubjectContext.Create();
        var interceptor = new EdgeObservingLifecycleInterceptor();
        parentContext.AddService<ILifecycleInterceptor>(interceptor);

        var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;

        // Act
        Assert.True(childContext.AddFallbackContext(parentContext));
        Assert.True(childContext.RemoveFallbackContext(parentContext));

        // Assert
        Assert.True(interceptor.SawEdgeOnAttach, "The attach callback ran before the edge was published.");
        Assert.True(interceptor.SawEdgeOnDetach, "The detach callback ran after the edge was removed.");
    }

    [Fact]
    public void WhenAcyclicFallbackIsRemoved_ThenTheChildIsNoLongerRetained()
    {
        // Arrange
        var parentContext = InterceptorSubjectContext.Create();
        parentContext.AddService<ILifecycleInterceptor>(new CountingLifecycleInterceptor());

        // Act: in its own frame so no local keeps the subject alive for the probe below.
        var probe = AttachAndDetach(parentContext);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        Assert.False(probe.IsAlive, "The parent context still retains the detached child.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task WhenSameFallbackIsRemovedConcurrently_ThenDetachCallbacksRunOnce(int removerCount)
    {
        for (var round = 0; round < 200; round++)
        {
            // Arrange: every remover starts from a rendezvous so they contend on the same record.
            // Without a single arbiter both would pass a check and both would detach.
            var parentContext = InterceptorSubjectContext.Create();
            var interceptor = new CountingLifecycleInterceptor();
            parentContext.AddService<ILifecycleInterceptor>(interceptor);

            var childContext = ((IInterceptorSubject)new ContextProbeSubject()).Context;
            Assert.True(childContext.AddFallbackContext(parentContext));

            var results = new bool[removerCount];
            using var ready = new CountdownEvent(removerCount);
            using var start = new ManualResetEventSlim(false);

            // Act
            var removers = Enumerable
                .Range(0, removerCount)
                .Select(index => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    start.Wait();
                    results[index] = childContext.RemoveFallbackContext(parentContext);
                }, TaskCreationOptions.LongRunning))
                .ToArray();

            ready.Wait();
            start.Set();
            await Task.WhenAll(removers);

            // Assert
            Assert.Equal(1, results.Count(removed => removed));
            Assert.Equal(1, interceptor.DetachCount);
            Assert.True(childContext.GetServices<ILifecycleInterceptor>().IsEmpty);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDetach(InterceptorSubjectContext parentContext)
    {
        var child = new ContextProbeSubject();
        var childContext = ((IInterceptorSubject)child).Context;
        Assert.True(childContext.AddFallbackContext(parentContext));
        Assert.True(childContext.RemoveFallbackContext(parentContext));
        return new WeakReference(child);
    }

    /// <summary>
    /// Stands in for a real lifecycle interceptor, which reaches its handlers by resolving them
    /// through the context of the subject it was handed.
    /// </summary>
    private sealed class ResolvingLifecycleInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            _ = subject.Context.GetServices<ILifecycleInterceptor>();
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            _ = subject.Context.GetServices<ILifecycleInterceptor>();
        }
    }

    private sealed class EdgeObservingLifecycleInterceptor : ILifecycleInterceptor
    {
        internal bool SawEdgeOnAttach;

        internal bool SawEdgeOnDetach;

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            SawEdgeOnAttach = !subject.Context.GetServices<ILifecycleInterceptor>().IsEmpty;
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            SawEdgeOnDetach = !subject.Context.GetServices<ILifecycleInterceptor>().IsEmpty;
        }
    }

    private static bool HasFallback(IInterceptorSubjectContext context, IInterceptorSubjectContext fallback)
    {
        return ContextStateReflection.HasFallbackContext(
            (InterceptorSubjectContext)context,
            (InterceptorSubjectContext)fallback);
    }

    private sealed class ThrowingDetachInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            throw new InvalidOperationException("Detach failed.");
        }
    }

    private sealed class ThrowingAttachInterceptor : ILifecycleInterceptor
    {
        private int _detachCount;

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            throw new InvalidOperationException("Attach failed.");
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
        }
    }

    private sealed class ParkingLifecycleInterceptor(
        ManualResetEventSlim attachStarted,
        ManualResetEventSlim releaseAttach) : ILifecycleInterceptor
    {
        private int _attachCount;
        private int _detachCount;

        public int AttachCount => Volatile.Read(ref _attachCount);

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _attachCount);
            attachStarted.Set();
            releaseAttach.Wait();
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
        }
    }

    private sealed class CountingLifecycleInterceptor : ILifecycleInterceptor
    {
        private int _detachCount;

        public int DetachCount => Volatile.Read(ref _detachCount);

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            Interlocked.Increment(ref _detachCount);
        }
    }
}
