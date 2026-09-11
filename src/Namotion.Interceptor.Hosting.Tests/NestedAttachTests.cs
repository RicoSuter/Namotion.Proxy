using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// A container that gives itself a default child from a lifecycle handler running during its own
/// context attach, where nothing ever registers that child with dependency injection and the handler
/// is the only thing that starts it.
/// </summary>
/// <remarks>
/// The child's own context attach is raised while the container's attach event is still being
/// dispatched, but it is <b>not</b> raised inside <c>HostedServiceHandler.AttachSubject</c>:
/// <c>LifecycleInterceptor</c> invokes the handler array one entry at a time, so the hosting handler's
/// call for the container has either already returned or has not started when the child's event
/// arrives. The child is therefore an ordinary attach carrying its own liveness write, its own
/// ownership take and its own pair of gate reads, and the outcome does not depend on where the
/// creating handler sits in that array. The one shape that does re-enter <c>AttachSubject</c> is a
/// startup completion deferrer, which the handler calls synchronously from inside the outer call, and
/// the last two tests drive that one.
/// </remarks>
public class NestedAttachTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenAnAttachHandlerCreatesTheContainersChild_ThenBothStartOnceAndEachOwnsItsOwnTarget(
        bool initializerRunsAheadOfTheHostingHandler)
    {
        // Arrange - both handler orders, because the creating handler interleaves with the hosting
        // handler differently in each: ahead of it the child's attach is fully handled before the
        // container's is, behind it the container's is fully handled first. Neither is a re-entrant
        // call, so the two orders have to reach the same state.
        var (host, context, initializer) = BuildHost(initializerRunsAheadOfTheHostingHandler);
        await host.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var holder = new ContainerHolder(context);
            var container = new HostedContainer();

            // Act
            holder.Container = container;

            // Assert
            var child = container.Child;
            Assert.NotNull(child);
            Assert.Equal(1, initializer.Created);

            var containerTarget = ((IInterceptorSubject)container).TryGetSubjectTarget()!;
            var childTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;

            // Empty transitions on both chains drain what the two attaches appended.
            await containerTarget.DrainAsync();
            await childTarget.DrainAsync();

            Assert.Equal(1, container.StartCount);
            Assert.Equal(1, child.StartCount);

            Assert.NotSame(containerTarget, childTarget);
            Assert.Same(handler, containerTarget.Owner);
            Assert.Same(handler, childTarget.Owner);

            Assert.True(handler.IsLive(container));
            Assert.True(handler.IsLive(child));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenAContainerWhoseChildAnAttachHandlerCreatedLeavesTheGraph_ThenBothStopAndTheGraphCanRunThemAgain(
        bool initializerRunsAheadOfTheHostingHandler)
    {
        // Arrange - the child exists only because the handler put it there, so nothing an explicit
        // AttachHostedService left behind can stop it. The detach cascade has to reach it through the
        // container's property, and both ownerships have to be released, or the re-attach below finds
        // targets no handler can ever claim again.
        var (host, context, initializer) = BuildHost(initializerRunsAheadOfTheHostingHandler);
        await host.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var holder = new ContainerHolder(context);
            var container = new HostedContainer();
            holder.Container = container;

            var child = container.Child!;
            var containerTarget = ((IInterceptorSubject)container).TryGetSubjectTarget()!;
            var childTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;
            await AsyncTestHelpers.WaitUntilAsync(() => container.StartCount == 1 && child.StartCount == 1);

            // Act
            holder.Container = null;

            await containerTarget.DrainAsync();
            await childTarget.DrainAsync();

            // Assert
            Assert.Equal(1, container.StopCount);
            Assert.Equal(1, child.StopCount);
            Assert.Null(containerTarget.Owner);
            Assert.Null(childTarget.Owner);
            Assert.False(handler.IsLive(container));
            Assert.False(handler.IsLive(child));

            // Back into the graph: the container keeps the child it was given, so the handler starts
            // the same two subjects again rather than a third one appearing.
            holder.Container = container;

            await containerTarget.DrainAsync();
            await childTarget.DrainAsync();

            Assert.Equal(1, initializer.Created);
            Assert.Same(child, container.Child);
            Assert.Equal(2, container.StartCount);
            Assert.Equal(2, child.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenAnAttachHandlerCreatesAChildAfterTheDrainClearedLiveness_ThenNeitherSubjectIsLeftLive(
        bool initializerRunsAheadOfTheHostingHandler)
    {
        // Arrange - the drain is parked inside a stop body, which is past BeginDraining and past the
        // liveness clear, so a liveness entry written from here is one nothing ever removes again and
        // the subject is rooted on a dead handler for the rest of that handler's life. Parking on a
        // stop rather than on DrainGate is what makes that observable: an entry written while the drain
        // is held at DrainGate is swept up by the clear that follows it, so the damage would heal
        // itself. Both halves of the container attach have to refuse, the container's own and the
        // child the handler creates while that attach is being dispatched.
        var (host, context, _) = BuildHost(initializerRunsAheadOfTheHostingHandler);
        await host.StartAsync();

        var handler = context.TryGetService<HostedServiceHandler>()!;

        var runningHolder = new ContainerHolder(context);
        var running = new HostedContainer();
        runningHolder.Container = running;
        await AsyncTestHelpers.WaitUntilAsync(() => running.StartCount == 1);

        // Attached before the drain, because a holder attaching during it is refused as well and the
        // container would never reach the handler at all.
        var holder = new ContainerHolder(context);

        using var subjectStop = ((IInterceptorSubject)running).TryGetSubjectTarget()!.HoldAtTransition();

        var stopping = host.StopAsync();
        await subjectStop.WaitUntilReachedAsync();

        // Act
        var container = new HostedContainer();
        holder.Container = container;

        // Assert - read while the drain is still parked, and again once it has finished, because the
        // drain clears liveness only once and never revisits it.
        var child = container.Child;
        Assert.NotNull(child);
        Assert.False(handler.IsLive(container));
        Assert.False(handler.IsLive(child));

        subjectStop.Release();
        await stopping;

        Assert.False(handler.IsLive(container));
        Assert.False(handler.IsLive(child));

        Assert.Null(((IInterceptorSubject)container).TryGetSubjectTarget()?.Owner);
        Assert.Null(((IInterceptorSubject)child).TryGetSubjectTarget()?.Owner);
        Assert.Equal(0, container.StartCount);
        Assert.Equal(0, child.StartCount);
    }

    [Fact]
    public async Task WhenADeferrerCreatesTheChildWhileTheContainersOwnAttachIsStillRunning_ThenBothStartOnceAndEveryHoldIsReleased()
    {
        // Arrange - the one shape that really does re-enter AttachSubject. The handler calls
        // DeferCompletion synchronously from inside TryTakeOwnershipAndStart, which is inside the
        // container's own AttachSubject, so a deferrer that assigns the child raises the child's
        // context attach from there. The child's whole attach, its liveness write, its ownership take
        // and its appended start, therefore runs before the container has taken its own target.
        var (host, context, deferrer) = BuildHostWithDeferrer();
        await host.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var holder = new ContainerHolder(context);
            var container = new HostedContainer();

            var containerWasOwnedWhenTheChildWasCreated = true;
            deferrer.OnDefer = () =>
            {
                if (container.Child is not null)
                {
                    return;
                }

                // Read before the assignment, so it reports the state the nested attach starts from
                // rather than anything that attach leaves behind.
                containerWasOwnedWhenTheChildWasCreated =
                    ((IInterceptorSubject)container).TryGetSubjectTarget()?.Owner is not null;

                container.Child = new CountingHostedSubject();
            };

            // Act
            holder.Container = container;

            // Assert
            var child = container.Child;
            Assert.NotNull(child);
            Assert.False(
                containerWasOwnedWhenTheChildWasCreated,
                "The container had already taken its own target, so the child's attach did not run inside the container's.");

            var containerTarget = ((IInterceptorSubject)container).TryGetSubjectTarget()!;
            var childTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;
            await containerTarget.DrainAsync();
            await childTarget.DrainAsync();

            Assert.Equal(1, container.StartCount);
            Assert.Equal(1, child.StartCount);
            Assert.Same(handler, containerTarget.Owner);
            Assert.Same(handler, childTarget.Owner);

            // The counted holds are what let a nested attach take its own while the outer one is still
            // outstanding, and the inner one has to be released as reliably as the outer.
            Assert.Equal(2, deferrer.Taken);
            Assert.Equal(0, deferrer.Outstanding);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheDrainBeginsWhileANestedAttachHoldsTheOuterOne_ThenNeitherSubjectStaysLiveOnTheDrainingHandler()
    {
        // Arrange - the interleaving the outer attach cannot see for itself: it read a running gate on
        // entry, wrote its liveness entry, and the drain begins while the nested attach it triggered is
        // still on the stack. Both calls then have to notice on the way out, or a subject that never
        // starts is left rooted on a handler that is about to die. The deferrer is the seam, because it
        // is the one piece of user code the attach path runs between the liveness write and the re-read
        // that follows the takes: the first hold creates the child, and the hold the nested attach takes
        // for that child starts the drain and waits for it to reach DrainGate.
        var (host, context, deferrer) = BuildHostWithDeferrer();
        await host.StartAsync();

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        var holder = new ContainerHolder(context);
        var container = new HostedContainer();

        Task? stopping = null;
        deferrer.OnDefer = () =>
        {
            if (container.Child is null)
            {
                container.Child = new CountingHostedSubject();
                return;
            }

            if (stopping is null)
            {
                stopping = host.StopAsync();
                drain.WaitUntilReached();
            }
        };

        // Act
        holder.Container = container;

        // Assert - read while the drain is still held at DrainGate, which is ahead of the liveness
        // clear, so an entry either call left behind is still there to be seen. Once the drain is let
        // go the clear removes both for an unrelated reason and the window is unobservable.
        var child = container.Child;
        Assert.NotNull(child);
        Assert.True(drain.WasReached, "The attach did not land inside the drain window.");
        Assert.False(handler.IsLive(container));
        Assert.False(handler.IsLive(child));

        Assert.Null(((IInterceptorSubject)container).TryGetSubjectTarget()!.Owner);
        Assert.Null(((IInterceptorSubject)child).TryGetSubjectTarget()!.Owner);

        drain.Release();
        await stopping!;

        Assert.Equal(0, container.StartCount);
        Assert.Equal(0, child.StartCount);
        Assert.Equal(0, deferrer.Outstanding);
    }

    /// <summary>
    /// Builds a host whose context carries the child creating handler either ahead of or behind the
    /// hosting handler. Both sit behind <c>ContextInheritanceHandler</c>, which is what lets a child
    /// created here resolve the hosting handler through its parent's context.
    /// </summary>
    private static (IHost Host, IInterceptorSubjectContext Context, ChildCreatingLifecycleHandler Initializer)
        BuildHost(bool initializerRunsAheadOfTheHostingHandler)
    {
        var builder = HostingTestHost.CreateBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var initializer = new ChildCreatingLifecycleHandler();

        if (initializerRunsAheadOfTheHostingHandler)
        {
            context.AddService<ILifecycleHandler>(initializer);
            context.WithHostedServices(builder.Services);
        }
        else
        {
            context.WithHostedServices(builder.Services);
            context.AddService<ILifecycleHandler>(initializer);
        }

        return (builder.Build(), context, initializer);
    }

    private static (IHost Host, IInterceptorSubjectContext Context, CallbackStartupDeferrer Deferrer)
        BuildHostWithDeferrer()
    {
        var builder = HostingTestHost.CreateBuilder();
        var context = HostingTestHost.CreateContext(builder);

        var deferrer = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(deferrer);

        return (builder.Build(), context, deferrer);
    }
}
