using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// The ordering and race guarantees of the handler. Every test here drives the interleaving through a
/// seam (<c>HostedServiceTarget.TransitionGate</c>, <c>HostedServiceHandler.DrainGate</c> or the
/// startup gate) rather than through delays, so the interleaving under test provably happens.
/// </summary>
/// <remarks>
/// An assertion that has to observe a queued transition appends an empty transition to the same chain
/// and awaits it. Appending never runs a body, so that completes only once everything already on the
/// chain has run, which is what makes those reads deterministic rather than timed. The same idiom is
/// used in the other hosting test classes.
/// </remarks>
public class HostedServiceHandlerRaceTests
{
    /// <summary>
    /// The chain lock round is decided by the lock rather than by timing, so one round already
    /// discriminates. Repeated a few times because the thread state read the round uses to release its
    /// seam can in principle observe a block that is not the chain lock, and an early release only
    /// ever hides the defect, never invents one.
    /// </summary>
    private const int ChainLockRaceRounds = 4;

    /// <summary>
    /// How long a drain is watched for a return it must not make. "Did not happen" has no event to
    /// wait on, so this is the one timed observation in the suite; too short only weakens it, and a
    /// drain held on a lock cannot return however long it is watched.
    /// </summary>
    private static readonly TimeSpan DrainMustNotReturnWithin = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task WhenADetachReleasesOwnershipBeforeAnAttachTakesIt_ThenTheAttachUndoesItsOwnTake()
    {
        // Arrange - the take reads liveness and installs the owner inside the chain lock, while a
        // context detach clears liveness, reads Owner and releases outside it. A take that lands after
        // the detach read Owner as null is one nothing releases: the target stays owned by this handler
        // and stays in its owned set, which roots the detached subject until shutdown and makes the
        // next handler over that subject lose the compare and exchange for good.
        await HostingTestHost.RunAsync(async context =>
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());
            await attachment.DrainAsync();

            // A second attachment, so the act below has a target whose ownership is still unclaimed at
            // the moment the detach runs. The first one is what keeps the subject live until then.
            var created = 0;

            // Armed on the handler rather than on the target, because the target the act creates does
            // not exist until the act runs. The first attachment above already took its ownership, so
            // the next take to reach this seam is the one under test. It holds the second target's
            // chain lock, which the detach below never needs: that target has no owner yet, so the
            // detach appends no stop for it. A test whose detach must append on the held target would
            // deadlock here.
            using var take = handler.HoldAtLivenessRead();

            HostedServiceTarget? secondTarget = null;
            var attaching = Task.Run(() =>
            {
                var second = child.AttachHostedService(() =>
                {
                    Interlocked.Increment(ref created);
                    return new TrackedBackgroundService();
                });

                secondTarget = ((IHostedServiceAttachmentTarget)second).Target;
                return second;
            });

            await take.WaitUntilReachedAsync();

            // Act - the whole detach runs while the take is held before its compare and exchange, so
            // it clears liveness, reads Owner as null and releases nothing.
            parent.Child = null;
            take.Release();
            await attaching.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            Assert.False(handler.IsLive(child));
            Assert.Null(secondTarget!.Owner);
            Assert.False(handler.IsOwned(secondTarget!));

            // Not discriminating on its own: the start body refuses on liveness whether or not the
            // take was undone. Asserted because a leaked ownership that also started something is the
            // worse of the two failures, and this separates them.
            Assert.Equal(0, Volatile.Read(ref created));
        });
    }

    [Fact]
    public async Task WhenAReAttachLandsWhileTheSubjectStopIsHeld_ThenAFreshInstanceRunsAndTheOldOneIsDisposed()
    {
        // Arrange - holding the subject's stop is what makes the re-attach provably land mid-stop.
        // Without the hold the test passes while the move is broken.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new HostedParent(context);
            var child = new CountingHostedSubject();
            var created = new ConcurrentQueue<TrackedBackgroundService>();

            child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Enqueue(instance);
                return instance;
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(
                () => child.StartCount == 1 && created.ToArray() is [{ IsStarted: true }]);

            using var subjectStop = ((IInterceptorSubject)child).TryGetSubjectTarget()!.HoldAtTransition();

            // Act - both graph moves are made while the subject's stop is held, so the re-attach's
            // create-and-start is queued behind the detach's stop on the attachment's chain.
            parent.Child = null;
            parent.Child = child;
            subjectStop.Release();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => created.ToArray() is [_, { IsStarted: true }],
                message: "The re-attach did not create a second instance.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => created.ToArray()[0].IsDisposed,
                message: "The pre-detach instance was never disposed.");

            var instances = created.ToArray();
            Assert.False(instances[1].IsDisposed);
            Assert.Equal(2, child.StartCount);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WhenAnAttachmentIsDetachedBeforeItsStartIsAppended_ThenNothingIsStarted(
        bool attachIsAwaited, bool detachIsAwaited)
    {
        // Arrange - the window between publishing the attachment and appending its start. A detach
        // that lands inside it removes the attachment from the subject, so the start it leaves
        // running is reachable from nothing: a later context detach enumerates no attachment for it
        // and never stops it. Taking a startup hold is the only user code the attach path runs inside
        // that window, so the deferrer drives the interleaving rather than a delay.
        //
        // One case per overload that reaches the window, because each attach overload appends through
        // its own call and each detach overload marks the target from its own code: deleting the mark
        // from DetachHostedServiceAsync leaves the two synchronous detach cases green.
        await RunWithDeferrerAsync(async (context, detacher) =>
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var created = 0;
            var detaches = new ConcurrentQueue<Task<bool>>();
            detacher.OnDefer = () =>
            {
                foreach (var published in child.GetHostedServiceAttachments())
                {
                    if (detachIsAwaited)
                    {
                        // Not awaited here: the detach runs synchronously up to and past its append,
                        // which is the whole window, and awaiting it from inside the hold would park
                        // the attach that is taking the hold. The tasks are awaited below instead.
                        detaches.Enqueue(child.DetachHostedServiceAsync(published, CancellationToken.None));
                    }
                    else
                    {
                        child.DetachHostedService(published);
                    }
                }
            };

            TrackedBackgroundService Factory()
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            }

            // Act
            var attachment = attachIsAwaited
                ? await child.AttachHostedServiceAsync(Factory, CancellationToken.None)
                : child.AttachHostedService(Factory);

            // Assert
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            await target.DrainAsync();

            Assert.All(await Task.WhenAll(detaches), Assert.True);
            Assert.Equal(1, detacher.Taken);
            Assert.Empty(child.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
            Assert.Null(target.Owner);
        });
    }

    [Fact]
    public async Task WhenAnExplicitDetachRacesTheHostDrain_ThenTheInstanceIsDisposedOnce()
    {
        // Arrange - two stops reach the same instance, so stop and dispose have to be idempotent per
        // target. The seam holds the drain's stop inside its body, so the explicit detach's stop is
        // provably queued behind it rather than merely near it.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        using var drainStop = target.HoldAtTransition();

        // Act
        var stopping = host.StopAsync();
        await drainStop.WaitUntilReachedAsync();

        // The subject is still in the graph, so the explicit detach still resolves the handler.
        var detached = child.DetachHostedServiceAsync(attachment, CancellationToken.None);
        drainStop.Release();

        // Assert
        await stopping;
        Assert.True(await detached);
        Assert.True(instance.IsDisposed);
        Assert.Equal(1, instance.DisposeCount);
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedDuringTheDrain_ThenNothingIsStarted()
    {
        // Arrange - the drain is held between BeginDraining and the liveness clear, so the attach
        // provably lands inside the drain window rather than near it: the gate is already draining
        // while the subject is still live, which is the interleaving the liveness check alone cannot
        // reject. A start that reaches the chain anyway is caught a second time by the gate re-read in
        // the start body, which is what a start queued before the drain depends on.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        // Act
        var stopping = host.StopAsync();
        await drain.WaitUntilReachedAsync();

        var created = 0;
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        // An empty transition behind the start on the same chain, awaited before the drain is let
        // go: this is what pins the start body inside the window rather than merely near it.
        await attachment.DrainAsync();

        drain.Release();

        // Assert - the drain awaits the stop it appends for the new target, and that stop is queued
        // behind the new target's start, so awaiting the drain is a full quiesce of that chain.
        await stopping;
        Assert.Equal(0, Volatile.Read(ref created));
        Assert.Null(attachment.Current);
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedDuringTheDrain_ThenTheDrainingHandlerTakesNoOwnership()
    {
        // Arrange - the same drain window, read for the other half of the damage. Nothing a draining
        // handler owns can ever start, and its release loop covers only the targets its own snapshot
        // held, so a target taken past that point stays owned by a dead handler and no later handler
        // can ever win the compare and exchange for it. The public attach paths have to reject the
        // window themselves: the liveness flag is still set here.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        MakeLive(child);

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        var stopping = host.StopAsync();
        await drain.WaitUntilReachedAsync();
        Assert.True(handler.IsLive(child), "The drain cleared liveness early, so the window under test is unreachable.");

        // Act
        var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

        // Assert - read while the drain is still held. Once it is let go it happens to release this
        // target too, because a take this early is still inside the snapshot it takes next, so the
        // ownership is only observable here.
        Assert.Null(((IHostedServiceAttachmentTarget)attachment).Target.Owner);

        drain.Release();
        await stopping;

        Assert.Null(attachment.Current);
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedAfterTheSubjectDetached_ThenNothingIsStarted()
    {
        // Arrange - liveness is per subject, which is what makes this case fail closed. Keyed per
        // target, or read as target ownership, the attach would pass its own check: it takes the
        // ownership of the fresh target itself. The subject is constructed with the context, so its
        // own context keeps resolving the handler after the graph detach and the attach really does
        // reach the handler.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person(context);
            parent.Child = child;
            parent.Child = null;

            var created = 0;

            // Act
            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            // Assert
            await attachment.DrainAsync();

            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
        });
    }

    [Fact]
    public async Task WhenAQueuedStartRunsAfterTheSubjectDetached_ThenNothingIsStarted()
    {
        // Arrange - the host is not started, so the startup gate holds every start body at a known
        // point. That is what lets the detach provably overtake a start that is already queued.
        var builder = HostingTestHost.CreateBuilder();

        var context = HostingTestHost.CreateContext(builder);

        var host = builder.Build();

        var parent = new Parent(context);
        var child = new Person(context);
        parent.Child = child;

        var created = 0;
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        // Act
        parent.Child = null;
        await host.StartAsync();

        try
        {
            // Assert
            await attachment.DrainAsync();

            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAStopIsAppendedInsideTheDrainWindow_ThenTheDrainDoesNotReturnUntilItHasRun()
    {
        // Arrange - the whole detach completes inside the drain window, so both of its writes land
        // ahead of both of the drain's reads and the barrier holds under either write order. What this
        // covers is the plain case: a stop queued before the snapshots is waited for. The two tests
        // that pin the write order itself, where a read falls between the writes, are below.
        //
        // This replaced two tests that pinned the opposite: that a stop escaping the drain still ran at
        // Drained. That state was reachable only through the defect the barrier closes.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new HostedParent(context);
        var child = new CountingHostedSubject();
        var created = new ConcurrentQueue<TrackedBackgroundService>();

        child.AttachHostedService(() =>
        {
            var instance = new TrackedBackgroundService();
            created.Enqueue(instance);
            return instance;
        });

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(
            () => child.StartCount == 1 && created.ToArray() is [{ IsStarted: true }]);

        var subjectTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;
        using var subjectStop = subjectTarget.HoldAtTransition();

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        var stopping = host.StopAsync();
        await drain.WaitUntilReachedAsync();

        // Act
        parent.Child = null;
        drain.Release();

        // Assert - the stop is held, so a drain that does not wait for it returns here.
        var returnedEarly = await Task.WhenAny(stopping, Task.Delay(DrainMustNotReturnWithin)) == stopping;
        Assert.False(
            returnedEarly,
            "StopAsync returned while a stop appended inside the drain window was still held, so the "
            + "barrier missed it and that stop would run against a disposed service provider.");

        subjectStop.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, child.StopCount);
        Assert.True(created.ToArray() is [{ IsStopped: true, IsDisposed: true }]);
    }

    [Fact]
    public async Task WhenAStartIsQueuedAndUnrunAsTheDrainRuns_ThenTheDrainStillWaitsForIt()
    {
        // Arrange - a start that is appended and has not run. The drain waits for it because appending
        // counted it, not because its target was in the snapshot, and the host disposes the service
        // provider the moment the drain returns. Held on the chain lock seam, which is the only way to
        // have a start provably queued and unrun while a drain is running: the take holds the target's
        // chain lock, so the drain's own append for that target queues behind it rather than racing it.
        var (host, context) = await HostingTestHost.StartAsync();

        var created = new ConcurrentQueue<TrackedBackgroundService>();
        var child = new Person();

        // Attached before the subject enters the graph, so the target exists to be armed and nothing
        // has started yet: an attachment on a subject with no context resolves no handler.
        var attachment = child.AttachHostedService(() =>
        {
            var instance = new TrackedBackgroundService();
            created.Enqueue(instance);
            return instance;
        });

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        using var take = target.HoldAtChainLock();

        // Act
        var parent = new Parent(context);
        var attaching = Task.Run(() => { parent.Child = child; });
        await take.WaitUntilReachedAsync();

        var stopping = Task.Run(() => host.StopAsync());

        // Assert - a drain whose snapshot missed this target has nothing to append and returns here.
        var returnedEarly = await Task.WhenAny(stopping, Task.Delay(DrainMustNotReturnWithin)) == stopping;

        take.Release();
        await attaching.WaitAsync(TimeSpan.FromSeconds(30));
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(
            returnedEarly,
            "StopAsync returned while a start was queued on a target its snapshot never saw, so the "
            + "instance that start creates is stopped by nobody.");

        // The start the drain waited for refuses itself on the gate re-read, so nothing was created.
        // What the assertion above pins is that the drain waited for it to reach that point.
        Assert.Empty(created);
        Assert.Null(target.Current);
    }

    [Fact]
    public async Task WhenAQueuedStartIsSkippedByTheDrain_ThenAnEarlierFaultSurvives()
    {
        // Arrange - a start that never creates anything must not clear the fault a caller has not
        // read yet. The drain skips it through whichever guard it reaches first, the gate re-read or
        // the cleared liveness, and the fault has to survive either way. Two seams: the transition
        // seam queues the start, and the drain seam proves the drain has begun when it runs.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        var shouldThrow = true;

        var attachment = child.AttachHostedService(() =>
        {
            if (shouldThrow)
            {
                shouldThrow = false;
                throw new InvalidOperationException("first attempt fails");
            }

            return new TrackedBackgroundService();
        });

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => attachment.Fault is not null);

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        using var start = target.HoldAtTransition();

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        // Act - the re-attach's start is queued behind the held stop and runs once draining began.
        parent.Child = null;
        parent.Child = child;

        var stopping = host.StopAsync();
        await drain.WaitUntilReachedAsync();

        start.Release();
        drain.Release();

        // Assert - the drain appends its own stop behind that start and awaits it, so the start has
        // provably run by the time the shutdown returns.
        await stopping;
        Assert.NotNull(attachment.Fault);
    }

    [Fact]
    public async Task WhenASubjectLeavesTheGraph_ThenItStopsBeforeItsAttachmentIsDisposed()
    {
        // Arrange - the context detach half of the ordering. A hosted subject's stop is slow, because
        // BackgroundService.StopAsync awaits its execute task, and the attachments it uses must not be
        // disposed underneath it while it unwinds. The shutdown path builds the same shape from its own
        // code, so it pins nothing here: dropping the wait DetachSubject passes leaves the drain test
        // below green. The hold makes the window the subject is inside observable rather than timed.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new HostedParent(context);
            var child = new CountingHostedSubject();
            var instance = new TrackedBackgroundService();
            var attachment = child.AttachHostedService(() => instance);

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => child.StartCount == 1 && instance.IsStarted);

            using var subjectStop = child.HoldAtStop();

            // Act
            parent.Child = null;
            await subjectStop.WaitUntilReachedAsync();

            // Assert - an unordered detach clears Current at the top of the attachment's stop body,
            // which runs the moment that stop is appended, a whole transition delay before the
            // subject's own StopAsync is entered.
            Assert.NotNull(attachment.Current);
            Assert.False(instance.IsStopped);
            Assert.False(instance.IsDisposed);

            subjectStop.Release();

            await AsyncTestHelpers.WaitUntilAsync(
                () => instance.IsStopped && instance.IsDisposed,
                message: "The attachment was never stopped and disposed after the subject's stop returned.");

            Assert.Equal(1, child.StopCount);
        });
    }

    [Fact]
    public async Task WhenTheHostDrains_ThenASubjectStopsBeforeItsAttachmentIsDisposed()
    {
        // Arrange - shutdown shares the ordering hazard of a context detach: a hosted subject's stop
        // is slow, and the attachments it uses must not be disposed underneath it while it unwinds.
        // The hold makes the window the subject is inside observable rather than timed.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new HostedParent(context);
        var child = new CountingHostedSubject();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => child.StartCount == 1 && instance.IsStarted);

        using var subjectStop = child.HoldAtStop();

        // Act
        var stopping = host.StopAsync();
        await subjectStop.WaitUntilReachedAsync();

        // Assert - an unordered drain clears Current at the top of the attachment's stop body, which
        // runs the moment that stop is appended, a whole transition delay before the subject's own
        // StopAsync is entered.
        Assert.NotNull(attachment.Current);
        Assert.False(instance.IsStopped);
        Assert.False(instance.IsDisposed);

        subjectStop.Release();
        await stopping;

        Assert.True(instance.IsStopped);
        Assert.True(instance.IsDisposed);
    }

    [Fact]
    public async Task WhenAStopIsInFlightWhenTheHostDrains_ThenTheDrainWaitsForIt()
    {
        // Arrange - a stop queued before the drain, whose target the detach released, so the drain's
        // own snapshot cannot see it, and the host disposes the service provider as soon as the drain
        // returns. Only the count carries it. The second subject is what makes the ordering
        // observable: the drain releases the ownership of the targets it snapshotted only once it has
        // waited for everything, so that owner is still set when the queued stop finally runs.
        var (host, context) = await HostingTestHost.StartAsync();

        var detachingParent = new HostedParent(context);
        var detaching = new CountingHostedSubject();
        detachingParent.Child = detaching;
        await AsyncTestHelpers.WaitUntilAsync(() => detaching.StartCount == 1);

        var remainingParent = new Parent(context);
        var remaining = new Person();
        var remainingInstance = new TrackedBackgroundService();
        var remainingAttachment = remaining.AttachHostedService(() => remainingInstance);
        remainingParent.Child = remaining;
        await AsyncTestHelpers.WaitUntilAsync(() => remainingInstance.IsStarted);

        var remainingTarget = ((IHostedServiceAttachmentTarget)remainingAttachment).Target;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerWhenTheQueuedStopRan =
            new TaskCompletionSource<HostedServiceHandler?>(TaskCreationOptions.RunContinuationsAsynchronously);

        ((IInterceptorSubject)detaching).TryGetSubjectTarget()!.TransitionGate = async () =>
        {
            await release.Task;
            ownerWhenTheQueuedStopRan.TrySetResult(remainingTarget.Owner);
        };

        detachingParent.Child = null;

        // Act
        var stopping = host.StopAsync();
        await AsyncTestHelpers.WaitUntilAsync(
            () => remainingInstance.IsDisposed,
            message: "The drain never ran the stops it snapshotted itself.");

        release.SetResult();
        await stopping;

        // Assert
        var owner = await ownerWhenTheQueuedStopRan.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(owner);
        Assert.Equal(1, detaching.StopCount);
    }

    [Fact]
    public async Task WhenASubjectEntersTheGraph_ThenItsStartupHoldIsTakenBeforeTheGraphWriteReturns()
    {
        // Arrange - the hold closes the window in which "the graph has finished starting" can be
        // reached while a start is still queued, so it has to exist by the time the graph write
        // returns. That is the constraint on where the hold may be taken, and it is why the take is
        // still inside the lifecycle lock: the event that appends the start arrives already inside
        // that lock, so taking the hold anywhere later reopens the window.
        await RunWithDeferrerAsync(async (context, deferrer) =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            using var start = target.HoldAtTransition();

            // Act - the start is appended while the graph write runs, and its body is held at the
            // seam, so the hold is read while the start it belongs to is provably still pending.
            parent.Child = child;

            // Assert
            Assert.Equal(1, deferrer.Taken);
            Assert.Equal(1, deferrer.Outstanding);

            start.Release();
            await target.DrainAsync();

            Assert.Equal(0, deferrer.Outstanding);
            Assert.NotNull(attachment.Current);
        });
    }

    [Fact]
    public async Task WhenAQueuedStartIsSkippedByTheDrain_ThenItsStartupHoldIsReleased()
    {
        // Arrange - a hold that outlives the start it belongs to hangs every synchronization wait on
        // that tree forever, which is worse than never having taken it, so every way out of the start
        // body has to release. This is the drain's way out: the start is appended while the gate is
        // Running and its body runs once draining has begun. Two seams, so both halves are pinned
        // rather than timed.
        var (host, context, deferrer) = await StartHostWithDeferrerAsync();

        var parent = new Parent(context);
        var child = new Person();
        var created = 0;

        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        using var start = ((IHostedServiceAttachmentTarget)attachment).Target.HoldAtTransition();

        parent.Child = child;
        Assert.Equal(1, deferrer.Outstanding);

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        // Act
        var stopping = host.StopAsync();
        await drain.WaitUntilReachedAsync();

        start.Release();
        drain.Release();

        // Assert - the drain appends its own stop behind that start and awaits it, so the start body
        // has provably run by the time the shutdown returns.
        await stopping;

        Assert.Equal(0, Volatile.Read(ref created));
        Assert.Equal(0, deferrer.Outstanding);
    }

    [Fact]
    public async Task WhenAQueuedStartFindsItsSubjectDetached_ThenItsStartupHoldIsReleased()
    {
        // Arrange - the same leak through the liveness guard, which is the way out a graph move takes.
        await RunWithDeferrerAsync(async (context, deferrer) =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = 0;

            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            using var start = target.HoldAtTransition();

            parent.Child = child;
            Assert.Equal(1, deferrer.Outstanding);

            // Act - the detach clears liveness while the start is held at the seam.
            parent.Child = null;
            start.Release();

            // Assert
            await target.DrainAsync();

            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Equal(0, deferrer.Outstanding);
        });
    }

    [Fact]
    public async Task WhenAQueuedStartIsSkippedByTheOneInstanceGuard_ThenItsStartupHoldIsReleased()
    {
        // Arrange - the third way out, and the one no other test reaches: a subject visible from two
        // hosting contexts raises one context attach per context and the OWNING handler sees both, so
        // it appends a second start for a target that is already running. That start skips its work
        // in the body, where the chain serializes the two, and owes the release from there.
        await HostingTestHost.RunWithTwoContextsAsync(async (firstContext, secondContext) =>
        {
            // Registered on one context only: the subject's own context reaches it through the fallback,
            // so both handlers resolve the same single deferrer.
            var deferrer = new CallbackStartupDeferrer();
            firstContext.AddService<IStartupCompletionDeferrer>(deferrer);

            var subject = new CountingHostedSubject();
            ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);

            var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
            await target.DrainAsync();

            Assert.Equal(1, subject.StartCount);
            var takenByTheFirstAttach = deferrer.Taken;

            // Act
            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);

            // Assert
            await target.DrainAsync();

            Assert.Equal(1, subject.StartCount);

            // Exact, because "more than before" is also satisfied by the non owning handler alone: it
            // takes a hold, loses the compare and exchange, and releases the hold again without ever
            // reaching the guard under test. The second attach raises one context attach per handler,
            // so two more holds is the owning handler's queued start plus that refused append.
            Assert.Equal(
                takenByTheFirstAttach + 2,
                deferrer.Taken);

            Assert.Equal(0, deferrer.Outstanding);
        });
    }

    [Fact]
    public async Task WhenASubjectLeavesTheGraphBeforeItsAttachTakesTheTarget_ThenTheNextHandlerStillClaimsIt()
    {
        // Arrange - the liveness read inside the chain lock, as distinct from the start body's
        // re-read. The body's re-read makes the outcome right, but only after the take has installed
        // this handler as the owner of a target belonging to a subject that has left the graph, and
        // the detach released ownership before that take happened, so nothing releases it again. The
        // next handler over the same subject then loses the compare and exchange for good. Taking a
        // startup hold is the one piece of user code the attach path runs between the gate read and
        // the chain lock, so the deferrer drives the detach rather than a delay.
        var (firstHost, firstContext, deferrer) = await StartHostWithDeferrerAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();

        try
        {
            var firstParent = new Parent(firstContext);
            var child = new Person();
            firstParent.Child = child;

            var detachOnDefer = false;
            deferrer.OnDefer = () =>
            {
                if (detachOnDefer)
                {
                    detachOnDefer = false;
                    firstParent.Child = null;
                }
            };

            var created = 0;

            // Act
            detachOnDefer = true;
            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            // Assert
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            Assert.Equal(1, deferrer.Taken);
            Assert.Null(target.Owner);

            // The consequence, and the reason an unowned target matters: the handler of the next graph
            // the subject joins has to win the compare and exchange, or the subject sits in a live
            // graph with nothing running and no error anywhere.
            var secondParent = new Parent(secondContext);
            secondParent.Child = child;

            await target.DrainAsync();

            Assert.Equal(1, Volatile.Read(ref created));
            Assert.NotNull(attachment.Current);
        }
        finally
        {
            await secondHost.StopAsync();
            await firstHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachLandsItsTakeAfterTheDrainBegan_ThenTheTakeIsUndone()
    {
        // Arrange - the gate re-read after the ownership take and its record, as distinct
        // from the read on entry. An attach that read Running just before BeginDraining still lands
        // both writes after it, and the read on entry cannot see that. Nothing else undoes the take:
        // the drain's release loop covers only the targets its own snapshot held, and that snapshot is
        // taken after this attach has been swept past. Two seams, so the interleaving is driven rather
        // than timed: the deferrer runs between the read on entry and the take, and the drain seam is
        // what proves the drain has begun by the time it returns.
        var (host, context, deferrer) = await StartHostWithDeferrerAsync();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        Task? stopping = null;
        deferrer.OnDefer = () =>
        {
            if (stopping is not null)
            {
                return;
            }

            stopping = host.StopAsync();
            drain.WaitUntilReached();
        };

        var created = 0;

        // Act
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        // Assert - read while the drain is still held. Once it is let go the drain releases every
        // target its snapshot held, so a take that survived here would be released a moment later for
        // an unrelated reason and the window would be unobservable.
        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        Assert.True(drain.WasReached, "The attach did not land its writes inside the drain window.");
        Assert.True(handler.IsLive(child), "The drain cleared liveness early, so the take was refused for another reason.");
        Assert.Null(target.Owner);

        drain.Release();
        await stopping!;

        await target.DrainAsync();

        Assert.Equal(0, Volatile.Read(ref created));
        Assert.Null(attachment.Current);
    }

    [Fact]
    public async Task WhenADrainingHandlerSeesAnAttach_ThenItInstallsNoOwnerForALiveHandlerToLoseTo()
    {
        // Arrange - the gate read on entry, as distinct from the re-read after the writes. The re-read
        // undoes a take, but only once it has been installed, and a live handler that reaches the same
        // target inside that window loses the compare and exchange for good, because nothing retries
        // it. Keeping that window empty is what the read on entry is for. The seam holds the window
        // open, and it is reached only when that read is gone, so an intact build simply runs the
        // attach to completion and the seam never fires.
        var (host, context) = await HostingTestHost.StartAsync();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        MakeLive(child);

        var handler = context.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();
        using var take = handler.HoldAtOwnershipTake();

        var stopping = host.StopAsync();
        await drain.WaitUntilReachedAsync();
        Assert.True(handler.IsLive(child), "The drain cleared liveness early, so the window under test is unreachable.");

        // Act - the attach runs on its own task, because it parks on the seam when the read on entry
        // is gone and returns without touching it when it is there.
        var attaching = Task.Run(() => child.AttachHostedService(() => new TrackedBackgroundService()));
        await Task.WhenAny(take.Reached, attaching);

        // A stand-in for the live handler that takes over from a draining one. It only has to win the
        // compare and exchange, which is the one thing a target owned by a draining handler denies it.
        var liveHandler = new HostedServiceHandler();
        // Last, not Single: the arrange added one to make the child live, and ImmutableArray.Add
        // appends, so the one the act added is at the end. Counted first, because Single used to carry
        // that check implicitly and Last does not: picking the wrong target here would test nothing.
        var published = child.GetHostedServiceAttachments();
        Assert.Equal(2, published.Length);

        var target = ((IHostedServiceAttachmentTarget)published.Last()).Target;
        var claimed = target.TryTakeOwnership(liveHandler, child, out var ownershipTaken);
        target.ReleaseOwnership(liveHandler);

        take.Release();
        await attaching;

        drain.Release();
        await stopping;

        // Assert
        Assert.True(claimed, "The draining handler owned the target, so a live handler loses the compare and exchange for good.");
        Assert.True(ownershipTaken);
    }

    [Fact]
    public async Task WhenADetachRacesTheAppendInsideTheChainLock_ThenTheStartIsOrderedAheadOfTheStop()
    {
        // Arrange - the liveness read, the ownership take and the append are one critical section, and
        // the seam holds it open where a split would put its gap. A detach's stop that lands in that
        // gap runs first, finds nothing to stop, and leaves the start behind it to create an instance
        // that is reachable from nothing: the detach has already removed the attachment, so no later
        // context detach enumerates it and it is never stopped and never disposed. The two racing
        // appenders are the two the chain lock exists for, a lifecycle driven attach holding the
        // lifecycle lock and a user driven detach on another thread.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var leakedRounds = 0;

            // Act
            for (var round = 0; round < ChainLockRaceRounds; round++)
            {
                if (await RunChainLockRaceRoundAsync(parent))
                {
                    leakedRounds++;
                }
            }

            // Assert
            Assert.Equal(0, leakedRounds);
        });
    }

    /// <summary>
    /// Attaches a service to a fresh subject, then lets the subject enter the graph while a detach of
    /// that same attachment runs the gap a split would open. Returns true when the stop was appended
    /// ahead of the start, which leaves the start creating an instance no stop can reach.
    /// </summary>
    /// <remarks>
    /// The seam releases when the detaching thread has either blocked or finished, which is what makes
    /// both builds decide the same way every time rather than by whichever thread wakes first. Under an
    /// intact critical section that thread blocks on the chain lock, so the start is appended first.
    /// Under a split one it never blocks, so it finishes its whole detach inside the gap and the stop
    /// is appended first.
    /// </remarks>
    private static async Task<bool> RunChainLockRaceRoundAsync(Parent parent)
    {
        var child = new Person();
        var created = 0;

        // Attached before the subject enters the graph, so nothing resolves a handler and the target
        // exists, unowned, with its seam settable before the take that is under test.
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        var takeReached = new ManualResetEventSlim(false);
        var detachRunning = new ManualResetEventSlim(false);
        var detachFinished = 0;

        // A dedicated thread rather than a pool one: the seam reads this thread's state to decide when
        // to release, and a pool thread carries work the round knows nothing about.
        var detaching = new Thread(() =>
        {
            takeReached.Wait(TimeSpan.FromSeconds(30));
            detachRunning.Set();
            child.DetachHostedService(attachment);
            Volatile.Write(ref detachFinished, 1);
        });

        // Conditions are recorded rather than asserted here: the seam runs while holding the target's
        // chain lock and the lifecycle lock, so throwing would unwind out of a property write with the
        // target owned and its startup holds unreleased, which makes a failing round far noisier than
        // the failure it is reporting.
        var detachStarted = false;
        var detachSettled = false;

        target.ChainLockGate = () =>
        {
            takeReached.Set();
            detachStarted = detachRunning.Wait(TimeSpan.FromSeconds(30));
            if (!detachStarted)
            {
                return;
            }

            detachSettled = SpinWait.SpinUntil(
                () => Volatile.Read(ref detachFinished) == 1
                      || (detaching.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(30));
        };

        detaching.Start();

        parent.Child = child;
        detaching.Join();

        Assert.True(detachStarted, "The detaching thread never started.");
        Assert.True(detachSettled, "The detaching thread neither blocked on the chain lock nor finished.");

        await target.DrainAsync();

        var leaked = attachment.Current is not null;
        Assert.Equal(1, Volatile.Read(ref created));

        parent.Child = null;
        takeReached.Dispose();
        detachRunning.Dispose();
        return leaked;
    }

    [Fact]
    public async Task WhenARepeatTakeLandsAfterTheDrainBegan_ThenItLeavesTheEarlierAttachAlone()
    {
        // Arrange - the "ownershipTaken" half of the gate re-read, as distinct from the re-read
        // itself. The re-read undoes what its own call installed, and a repeat take installed nothing:
        // the owner and the record it finds belong to an earlier attach whose instance is running.
        // Undoing those pulls that target out of the set the drain is about to stop, and the instance
        // then survives shutdown with nothing left able to reach it.
        //
        // The repeat take needs a target this handler already owns, which one subject visible from two
        // hosting contexts gives: the second context raises one more attach that the owning handler
        // also sees, and its take finds itself already installed.
        var (host, firstContext, secondContext) = await HostingTestHost.StartWithTwoContextsAsync();

        var subject = new CountingHostedSubject();
        ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);

        var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
        await target.DrainAsync();
        Assert.Equal(1, subject.StartCount);

        var handler = firstContext.TryGetService<HostedServiceHandler>()!;
        using var drain = handler.HoldAtDrain();

        // Armed only now, so the first attach's own take runs past it untouched and the next call to
        // reach it is the repeat take under test. It fires outside the chain lock, so the drain below
        // is held by its own seam rather than by this one.
        using var take = handler.HoldAtOwnershipTake();

        // Act - the repeat take lands, the drain begins under it, and only then does it re-read.
        var attaching = Task.Run(() => ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext));
        await take.WaitUntilReachedAsync();

        var stopping = Task.Run(() => host.StopAsync());
        await drain.WaitUntilReachedAsync();

        take.Release();
        await attaching.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert - read while the drain is still held, which is before its snapshot. Once it is let go
        // the drain releases everything it covered, and the difference is unobservable.
        //
        // The record follows the owner now, so a repeat take that undid either undid both, and reading
        // both would be reading one fact twice. The damage is read by the stop count at the end: a
        // target pulled out of the snapshot is never stopped.
        Assert.Same(handler, target.Owner);

        drain.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, subject.StartCount);
        Assert.Equal(1, subject.StopCount);
    }

    [Fact]
    public async Task WhenARepeatTakeFindsLivenessCleared_ThenItLeavesTheEarlierAttachAlone()
    {
        // Arrange - the "ownershipTaken" half of the liveness undo inside the chain lock, as distinct
        // from its sibling on the gate undo. A repeat take installs nothing, so the owner and the record
        // it finds belong to an earlier attach whose instance is running. Undoing those pulls the target
        // out from under the drain's own append, which then reads a stranger and refuses, and the
        // instance survives shutdown.
        //
        // The clear the second liveness read has to see is the drain's own, and the repeat take needs a
        // target this handler already owns, which one subject visible from two hosting contexts gives.
        var (host, firstContext, secondContext) = await HostingTestHost.StartWithTwoContextsAsync();

        var subject = new CountingHostedSubject();
        ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);

        var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
        await target.DrainAsync();
        Assert.Equal(1, subject.StartCount);

        var handler = firstContext.TryGetService<HostedServiceHandler>()!;

        // Armed only now, so the first attach's own take ran past it untouched. It fires inside the
        // chain lock, between the take's first liveness read and its compare and exchange, which is
        // where the drain's liveness clear has to land for the second read to see it.
        using var take = handler.HoldAtLivenessRead();
        using var drainAppend = handler.HoldAtDrainAppend();

        // Act - the repeat take holds the chain lock, the drain clears liveness and snapshots what it
        // owns underneath it, and only then does the take read liveness for the second time.
        var attaching = Task.Run(() => ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext));
        await take.WaitUntilReachedAsync();

        var stopping = Task.Run(() => host.StopAsync());
        await drainAppend.WaitUntilReachedAsync();

        take.Release();
        await attaching.WaitAsync(TimeSpan.FromSeconds(30));

        drainAppend.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(1, subject.StartCount);
        Assert.Equal(1, subject.StopCount);
    }

    [Fact]
    public async Task WhenAWholeStopLandsAfterTheDrainSnapshotsWhatItOwns_ThenTheDrainStillWaitsForIt()
    {
        // Arrange - a whole detach, appended and released, lands between the drain's snapshot and its
        // own appends. The drain's append for that target is then refused, because ownership has moved
        // back out from under it, so nothing but the count carries the stop the detach appended. Held
        // at the seam between the two, which is the only place that interleaving is reachable.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        var created = new ConcurrentQueue<TrackedBackgroundService>();
        var attachment = child.AttachHostedService(() =>
        {
            var instance = new TrackedBackgroundService();
            created.Enqueue(instance);
            return instance;
        });

        await attachment.DrainAsync();
        Assert.True(created.ToArray() is [{ IsStarted: true }]);

        // Holds the detach's stop body, so a drain that missed it returns while it has provably not run.
        using var stop = ((IHostedServiceAttachmentTarget)attachment).Target.HoldAtTransition();
        using var snapshot = handler.HoldAtDrainAppend();

        var stopping = Task.Run(() => host.StopAsync());
        await snapshot.WaitUntilReachedAsync();

        // Act - the whole detach, appended and released, lands here.
        parent.Child = null;
        snapshot.Release();

        // Assert
        var returnedEarly = await Task.WhenAny(stopping, Task.Delay(DrainMustNotReturnWithin)) == stopping;

        stop.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(
            returnedEarly,
            "StopAsync returned while a stop appended after its snapshot was still held, so the append "
            + "it refused left that stop covered by nothing.");

        Assert.True(created.ToArray() is [{ IsStopped: true, IsDisposed: true }]);
    }

    [Fact]
    public async Task WhenTheGateReReadUndoesATakeWhoseStartIsAlreadyCommitted_ThenThatStartIsStillStopped()
    {
        // Arrange - the re-read undoes a take it made a moment ago, and it cannot assume the start it
        // appended has not run: that body reads the gate at its own top, so it can have read Running
        // just before BeginDraining and be past every guard it has. An undo that only retired the
        // ownership record would hide the instance that body is about to create from a snapshot taken
        // afterwards, and nothing would ever stop or dispose it. Appending a stop covers it instead.
        //
        // Three seams, so all of it is driven: the factory parks a start that is provably committed,
        // OwnershipTakenGate parks the appending thread between the append and the re-read, and
        // DrainGate holds the drain ahead of both of its snapshots.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var created = new ConcurrentQueue<TrackedBackgroundService>();
        var factoryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var child = new Person();
        var attachment = child.AttachHostedService(() =>
        {
            // Reached only after the gate read, the liveness read, the ownership read and the one
            // instance guard, so a start parked here cannot refuse itself afterwards.
            factoryReached.TrySetResult();
            releaseFactory.Task.Wait(TimeSpan.FromSeconds(30));

            var instance = new TrackedBackgroundService();
            created.Enqueue(instance);
            return instance;
        });

        using var take = handler.HoldAtOwnershipTake();

        var parent = new Parent(context);
        var attaching = Task.Run(() => { parent.Child = child; });

        await take.WaitUntilReachedAsync();
        await factoryReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using var drain = handler.HoldAtDrain();

        var stopping = Task.Run(() => host.StopAsync());
        await drain.WaitUntilReachedAsync();

        // Act - the appending thread reaches its re-read, sees Draining, and undoes a take whose start
        // is parked in its factory.
        take.Release();
        await attaching.WaitAsync(TimeSpan.FromSeconds(30));

        drain.Release();
        releaseFactory.SetResult();

        // Assert - the drain has to wait for that start and for the stop behind it.
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            created.ToArray() is [{ IsStarted: true, IsStopped: true, IsDisposed: true }],
            "The undo hid a committed start from the drain, so its instance outlived the host with "
            + "nothing able to reach it: "
            + string.Join(
                ", ",
                created.ToArray().Select(i => $"started={i.IsStarted} stopped={i.IsStopped} disposed={i.IsDisposed}")));

        Assert.Null(attachment.Current);
    }

    [Fact]
    public async Task WhenASubjectJoinsASecondHostWhileTheFirstIsDraining_ThenTheSecondHostsInstanceSurvives()
    {
        // Arrange - the drain reads its owned set holding nothing and appends afterwards, so ownership
        // can move in between. Decided outside the chain lock, this handler stops and disposes the
        // instance the second host started and owns, and the second graph is left live with nothing
        // running and no error anywhere. The seam holds the drain between the snapshot and the appends,
        // which is the only place that interleaving is reachable.
        var (firstHost, firstContext) = await HostingTestHost.StartAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();

        try
        {
            var created = new ConcurrentQueue<TrackedBackgroundService>();
            var child = new Person();
            var attachment = child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Enqueue(instance);
                return instance;
            });

            var firstParent = new Parent(firstContext);
            firstParent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created.ToArray() is [{ IsStarted: true }]);

            var firstHandler = firstContext.TryGetService<HostedServiceHandler>()!;
            using var snapshot = firstHandler.HoldAtDrainAppend();

            // Act - the whole move lands after the first host snapshotted the target and before it
            // appends anything for it.
            var stopping = firstHost.StopAsync();
            await snapshot.WaitUntilReachedAsync();

            firstParent.Child = null;
            var secondParent = new Parent(secondContext);
            secondParent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(
                () => created.ToArray() is [_, { IsStarted: true }],
                message: "The second host never started its own instance, so the drain below proves nothing.");

            snapshot.Release();
            await stopping.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert - an empty transition behind whatever the drain appended, so the reads below are
            // deterministic rather than timed.
            await attachment.DrainAsync();

            var instances = created.ToArray();
            Assert.Equal(2, instances.Length);
            Assert.True(instances[0].IsStopped);
            Assert.True(instances[0].IsDisposed);
            Assert.False(
                instances[1].IsStopped,
                "The first host's drain stopped an instance the second host started and owns.");

            Assert.False(instances[1].IsDisposed);
            Assert.Same(instances[1], attachment.Current);
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenNothingIsInFlightAsTheDrainBegins_ThenItStillWaitsForTheStopsItAppends()
    {
        // Arrange - everything the graph did has settled, so the count is zero at the moment the drain
        // starts. A barrier that decided on the count it read before appending, or that armed a signal
        // and waited for it to be set, returns here while its own stop is still running and the host
        // disposes the service provider underneath it.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var parent = new Parent(context);
        var child = new Person();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        await attachment.DrainAsync();
        Assert.True(instance.IsStarted);
        Assert.Equal(0, handler.InFlightTransitionCount);

        using var stop = ((IHostedServiceAttachmentTarget)attachment).Target.HoldAtTransition();

        // Act
        var stopping = host.StopAsync();

        // Assert - the drain's own stop is held, so a drain that did not wait for it returns here.
        var returnedEarly = await Task.WhenAny(stopping, Task.Delay(DrainMustNotReturnWithin)) == stopping;

        stop.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(
            returnedEarly,
            "StopAsync returned while the stop it appended itself was still held, so that stop would "
            + "run against a disposed service provider.");

        Assert.True(instance.IsStopped);
        Assert.True(instance.IsDisposed);
    }

    [Fact]
    public async Task WhenAStopIsAppendedAfterTheCountFirstReachedZero_ThenTheDrainStillWaitsForIt()
    {
        // Arrange - the count is read, not held, so a stop appended after the drain's own stops finished
        // went through the same increment and only a second read sees it. The seam sits after the first
        // wait and before the ownership release, which is the window a context detach still reads this
        // handler as the owner in and appends from.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var parent = new Parent(context);
        var child = new Person();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        await attachment.DrainAsync();
        Assert.True(instance.IsStarted);

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        using var firstWait = handler.HoldAtDrainRelease();

        // Armed from inside the seam, so the drain's own stop ran unheld and the count provably reached
        // zero before the detach below appends anything.
        TestGate? stop = null;
        firstWait.OnReached = () => stop = target.HoldAtTransition();

        // Act
        var stopping = host.StopAsync();
        await firstWait.WaitUntilReachedAsync();
        Assert.Equal(0, handler.InFlightTransitionCount);

        parent.Child = null;
        firstWait.Release();

        // Assert - the appended stop is held, so a drain that read the count once returns here.
        var returnedEarly = await Task.WhenAny(stopping, Task.Delay(DrainMustNotReturnWithin)) == stopping;

        stop!.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Dispose();

        Assert.False(
            returnedEarly,
            "StopAsync returned while a stop appended after its first read of the count was still held, "
            + "so that stop would run against a disposed service provider.");
    }

    [Fact]
    public async Task WhenAStartFaultedAndTheSubjectStayedInTheGraph_ThenTheDrainStillReleasesTheTarget()
    {
        // Arrange - the record is written when the take installs the owner, not when an instance is
        // created, so a target whose start faulted is still the drain's to release. Left owned by a
        // drained handler, every later handler over that subject loses the compare and exchange.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var parent = new Parent(context);
        var child = new Person();
        var attachment = child.AttachHostedService<TrackedBackgroundService>(
            () => throw new InvalidOperationException("the start fails"));

        parent.Child = child;
        await AsyncTestHelpers.WaitUntilAsync(() => attachment.Fault is not null);

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;
        Assert.Same(handler, target.Owner);
        Assert.True(handler.IsOwned(target), "The take recorded nothing, so the drain has nothing to release.");

        // Act
        await host.StopAsync();

        // Assert
        Assert.Null(target.Owner);
        Assert.False(
            handler.IsOwned(target),
            "The drained handler still holds the target, which roots the subject and denies every later "
            + "handler the compare and exchange.");
    }

    /// <summary>
    /// Gives the subject its first target, which is what makes it live.
    /// </summary>
    /// <remarks>
    /// Liveness is recorded when a subject gains its first target, not when it enters the graph, so a
    /// subject has to host something before a drain begins or the window under test does not exist. The
    /// recording is synchronous inside this call, so whether this service ever starts is irrelevant.
    /// </remarks>
    private static void MakeLive(IInterceptorSubject subject)
        => subject.AttachHostedService(() => new TrackedBackgroundService());

    /// <summary>
    /// Runs <paramref name="action"/> against a started host whose context carries a deferrer, and
    /// stops the host afterwards.
    /// </summary>
    private static async Task RunWithDeferrerAsync(
        Func<IInterceptorSubjectContext, CallbackStartupDeferrer, Task> action)
    {
        var (host, context, deferrer) = await StartHostWithDeferrerAsync();
        try
        {
            await action(context, deferrer);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<(IHost Host, IInterceptorSubjectContext Context, CallbackStartupDeferrer Deferrer)>
        StartHostWithDeferrerAsync()
    {
        var builder = HostingTestHost.CreateBuilder();

        var context = HostingTestHost.CreateContext(builder);

        var deferrer = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(deferrer);

        var host = builder.Build();
        await host.StartAsync();
        return (host, context, deferrer);
    }
}
