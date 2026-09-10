using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceHandlerTests
{
    /// <summary>
    /// Shortened so a deadlocked shutdown ends the test rather than the run, and long enough that a
    /// healthy shutdown (two transitions, each with the handler's start delay) is nowhere near half.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The deadline the wedged shutdown test spends in full, so it is as short as the handler's two
    /// transition delays allow rather than as long as <see cref="ShutdownTimeout"/>.
    /// </summary>
    private static readonly TimeSpan WedgedShutdownTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WhenTwoValueEqualSubjectsAreHosted_ThenDetachingOneLeavesTheOtherLive()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var container = new ValueEqualityContainer(context);
            var first = new ValueEqualityHostedSubject { Name = "same" };
            var second = new ValueEqualityHostedSubject { Name = "same" };
            container.First = first;
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act - the scope holds the second start until after the detach, which is the shape the
            // symptom has: one shared liveness entry means detaching either subject clears the other's,
            // and the start still waiting reads it and declines.
            using (context.DeferHostedServiceStartup())
            {
                container.Second = second;
                container.First = null;
            }

            // Assert
            Assert.False(handler.IsLive(first));
            Assert.True(handler.IsLive(second));
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenSubjectImplementsIHostedService_ThenItIsStartedAndStopped()
    {
        // Arrange
        PersonWithBackgroundService person = null!;

        // Act
        await HostingTestHost.RunAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Assert
            Assert.Equal("John", person.FirstName);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
    }

    [Fact]
    public async Task WhenAttachmentIsCreated_ThenTheFactoryProducesTheRunningInstance()
    {
        // Arrange
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);

            // Act
            var attachment = await person.AttachHostedServiceAsync(
                () => new PersonBackgroundService(person), CancellationToken.None);

            // Assert
            Assert.NotNull(attachment.Current);
            Assert.Equal("John", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenSubjectIsDetachedAndReattached_ThenAFreshInstanceRuns()
    {
        // Arrange - this is the whole point of the factory API. The pre-detach instance must be
        // disposed and a NEW one created; restarting the old one is impossible because a disposed
        // connector cannot restart.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();

            // A concurrent queue, because the factory runs on the transition thread while the
            // assertions below poll from the test thread.
            var created = new ConcurrentQueue<TrackedBackgroundService>();

            child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Enqueue(instance);
                return instance;
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => created.ToArray() is [{ IsStarted: true }]);

            // Act - detach and reattach with no quiescing in between
            parent.Child = null;
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => created.ToArray() is [_, { IsStarted: true }],
                message: "The re-attach did not create a second instance.");
            await AsyncTestHelpers.WaitUntilAsync(() => created.ToArray()[0].IsDisposed,
                message: "The pre-detach instance was never disposed.");

            var instances = created.ToArray();
            Assert.False(instances[1].IsDisposed);
        });
    }

    [Fact]
    public async Task WhenTheFactoryReturnsTheInstanceItAlreadyProduced_ThenTheStartFaultsInsteadOfUsingItAfterDispose()
    {
        // Arrange - the shape a caller migrating from an instance based API is steered into: the only
        // attach overload takes a factory, so "AttachHostedService(() => myService)" is what compiles.
        // The handler disposed that instance when the subject left the graph, so a re-entry that
        // started it again would be a use after dispose with nothing reported anywhere.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var instance = new TrackedBackgroundService();
            var attachment = child.AttachHostedService(() => instance);

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

            parent.Child = null;
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsDisposed);

            // Act
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => attachment.Fault is not null,
                message: "The re-entry started the instance the handler had already disposed.");

            Assert.Equal(1, instance.StartCount);
            Assert.Equal(1, instance.DisposeCount);
            Assert.Null(attachment.Current);
        });
    }

    [Fact]
    public async Task WhenSubjectIsDetached_ThenTheInstanceIsDisposedExactlyOnce()
    {
        // Arrange
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var instance = new TrackedBackgroundService();
            child.AttachHostedService(() => instance);
            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

            // Act
            parent.Child = null;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsDisposed);
            Assert.Equal(1, instance.DisposeCount);
        });
    }

    [Fact]
    public async Task WhenAttachmentIsDetachedExplicitly_ThenALaterContextAttachStartsNothing()
    {
        // Arrange
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var created = 0;
            var attachment = child.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            });

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref created) == 1);

            // Act
            await child.DetachHostedServiceAsync(attachment, CancellationToken.None);
            parent.Child = null;
            parent.Child = child;

            // Assert - the empty transition drains what the two graph moves appended. Counting is the
            // claim in the name; the attachment being gone is the mechanism.
            await attachment.DrainAsync();

            Assert.Equal(1, Volatile.Read(ref created));
            Assert.Empty(child.GetHostedServiceAttachments());
        });
    }

    [Fact]
    public async Task WhenAnAttachmentIsDetachedSynchronously_ThenItIsStoppedDisposedAndForgotten()
    {
        // Arrange - the synchronous overload is what the OPC UA wrappers call, and the removal is the
        // half of it that decides whether a later context attach starts the factory again.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var instance = new TrackedBackgroundService();
            var attachment = child.AttachHostedService(() => instance);

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsStarted);

            // Act
            var detached = child.DetachHostedService(attachment);

            // Assert
            Assert.True(detached);
            Assert.Empty(child.GetHostedServiceAttachments());

            await AsyncTestHelpers.WaitUntilAsync(() => instance.IsDisposed);
            Assert.True(instance.IsStopped);
            Assert.Null(attachment.Current);

            // A second detach removes nothing, which is what tells a caller its handle is spent.
            Assert.False(child.DetachHostedService(attachment));
        });
    }

    [Fact]
    public async Task WhenAnAttachmentIsDetachedExplicitly_ThenTheHandlerStopsRetainingItsTarget()
    {
        // Arrange - the explicit detach stops the target without releasing it, and that is deliberate:
        // releasing makes a start queued ahead of the detach read Owner as null and refuse, which
        // leaves the stop no instance to dispose. The record still has to go, or every attach and
        // detach cycle retains a target and its subject on the handler for the handler's whole life.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var person = new Person(context);
            var instance = new TrackedBackgroundService();
            var attachment = person.AttachHostedService(() => instance);

            await attachment.DrainAsync();
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            Assert.True(handler.IsOwned(target));

            // Act
            Assert.True(person.DetachHostedService(attachment));
            await attachment.DrainAsync();

            // Assert
            Assert.False(
                handler.IsOwned(target),
                "The handler retains the detached target and its subject for the rest of its life.");

            Assert.Same(handler, target.Owner);
            Assert.True(instance.IsDisposed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentIsDetachedByTheAwaitingOverload_ThenTheHandlerStopsRetainingItsTarget()
    {
        // Arrange - the same retirement on the other overload. Each one retires its own record, so
        // deleting either alone leaves the test that drives the other green.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var person = new Person(context);
            var instance = new TrackedBackgroundService();
            var attachment = person.AttachHostedService(() => instance);

            await attachment.DrainAsync();
            var target = ((IHostedServiceAttachmentTarget)attachment).Target;
            Assert.True(handler.IsOwned(target));

            // Act
            Assert.True(await person.DetachHostedServiceAsync(attachment, CancellationToken.None));

            // Assert
            Assert.False(
                handler.IsOwned(target),
                "The handler retains the detached target and its subject for the rest of its life.");

            Assert.Same(handler, target.Owner);
            Assert.True(instance.IsDisposed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenTheFactoryThrows_ThenTheFaultIsRecordedAndCurrentStaysNull()
    {
        // Arrange
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync<TrackedBackgroundService>(
                    () => throw new InvalidOperationException("factory failed"), CancellationToken.None));

            Assert.Equal("factory failed", exception.Message);

            // The transactional guarantee: a caller's catch is never left owning an invisible attachment
            Assert.Empty(person.GetHostedServiceAttachments());
        });
    }

    [Fact]
    public async Task WhenAnAwaitedAttachFaults_ThenTheHandlerStopsRetainingItsTarget()
    {
        // Arrange - the removal that makes the awaiting overload transactional is also what puts the
        // target out of reach: no later attach or detach enumerates an attachment that is gone from the
        // subject's data, so the ownership this call took has to be undone here or the subject stays
        // rooted on the handler until shutdown. A host that retries failed attaches, which is the
        // connector shape, leaks one subject per failure.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var person = new Person(context);

            // Read from inside the factory, which is the last moment the attachment is still published:
            // the overload removes it before it throws, so the caller never sees the handle.
            HostedServiceTarget? target = null;

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync<TrackedBackgroundService>(
                    () =>
                    {
                        var attachment = person.GetHostedServiceAttachments().Single();
                        target = ((IHostedServiceAttachmentTarget)attachment).Target;
                        throw new InvalidOperationException("factory failed");
                    },
                    CancellationToken.None));

            // Assert
            Assert.NotNull(target);
            Assert.False(
                handler.IsOwned(target!),
                "The handler retains the target of a failed attach, and the attachment it belongs to is "
                + "already gone, so nothing reaches it again before shutdown.");

            Assert.Null(target!.Owner);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAStartFaults_ThenTheInstanceIsDisposed()
    {
        // Arrange - leaving a half started connector undisposed is the leak this design exists to fix
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService { ThrowOnStart = true };

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync(() => instance, CancellationToken.None));

            // Assert
            Assert.True(instance.IsDisposed);
        });
    }

    [Fact]
    public async Task WhenATransitionFaultedEarlier_ThenTheNextSuccessfulOneClearsTheFault()
    {
        // Arrange - a stale Fault would make a later successful attach throw, and the OPC UA wrappers
        // would turn that into Status = Error with a stale message.
        await HostingTestHost.RunAsync(async context =>
        {
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

            // Act
            parent.Child = null;
            parent.Child = child;

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is not null);
            Assert.Null(attachment.Fault);
        });
    }

    [Fact]
    public async Task WhenHostStops_ThenHandlerCreatedInstancesAreDisposedAndSubjectsAreNot()
    {
        // Arrange
        var instance = new TrackedBackgroundService();
        PersonWithBackgroundService person = null!;

        await HostingTestHost.RunAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await person.AttachHostedServiceAsync(() => instance, CancellationToken.None);
        });

        // Assert - the container disposes AddSubject singletons, so the claim under test is
        // specifically that the HANDLER did not dispose the subject.
        Assert.True(instance.IsDisposed);
        Assert.False(person.WasDisposedByHandler);
    }

    [Fact]
    public async Task WhenReparentedWithoutReachingZeroReferences_ThenNothingRestarts()
    {
        // Arrange - add-then-remove keeps the reference count above zero, so isLastDetach never fires
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

            parent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => attachment.Current is not null);
            var original = attachment.Current;

            // Act - add then remove keeps the reference count above zero, so isLastDetach never fires
            parent.SecondChild = child;
            parent.Child = null;

            // Assert - without the empty transition a stop is still waiting out its transition delay
            // when Current is read, so the assertion cannot observe a restart even when one happens.
            await attachment.DrainAsync();

            Assert.Same(original, attachment.Current);
            Assert.False(original!.IsDisposed);
        });
    }

    [Fact]
    public async Task WhenAServiceIsAttachedAfterItsHandlerDrained_ThenTheNextHandlerStartsIt()
    {
        // Arrange - two hosts, the second still running when the first has drained. The subject stays
        // attached to the drained handler's context, so the attach below really does resolve it and
        // the claim under test is reachable.
        var firstBuilder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(firstBuilder);

        var firstHost = firstBuilder.Build();
        await firstHost.StartAsync();

        var secondBuilder = HostingTestHost.CreateBuilder();

        var secondContext = HostingTestHost.CreateContext(secondBuilder);

        var secondHost = secondBuilder.Build();
        await secondHost.StartAsync();

        try
        {
            var subject = new Person();
            ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);
            await firstHost.StopAsync();

            // Act
            var attachment = subject.AttachHostedService(() => new TrackedBackgroundService());
            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => attachment.Current is { IsStarted: true },
                message: "The drained handler claimed the target and never released it, so the live handler could not take it.");
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenADrainedHandlerIsAskedToWaitForAStart_ThenItClaimsNothingAndReportsNothingStarted()
    {
        // Arrange - WaitForStartAsync is what an activation calls after resolving a subject, and it
        // needs the same guards as the attach paths. The attach happens before the drain, so the
        // handler really did create, own and start the target: attaching afterwards would leave no
        // target at all and the call would short circuit before reaching anything under test. A
        // drained handler releases only what its own drain snapshotted, so a claim taken here is
        // never released and the live handler below would lose the compare and exchange forever.
        var (firstHost, firstContext) = await HostingTestHost.StartAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();

        var subject = new CountingHostedSubject();
        var drainedHandler = firstContext.TryGetService<HostedServiceHandler>()!;

        ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.StartCount == 1);

        var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
        Assert.Same(drainedHandler, target.Owner);

        await firstHost.StopAsync();

        try
        {
            // Act
            var started = await drainedHandler.WaitForStartAsync(subject, CancellationToken.None);

            // Assert
            Assert.False(started);
            Assert.Equal(1, subject.StartCount);
            Assert.Null(target.Owner);

            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);
            await AsyncTestHelpers.WaitUntilAsync(
                () => subject.StartCount == 2,
                message: "The drained handler claimed the target and never released it, so the live handler could not take it.");
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenANonOwningHandlerIsAskedToWaitForAStart_ThenItReportsNothingStarted()
    {
        // Arrange - both handlers are live for the subject and both saw the attach, but only the
        // first owns the target and appended a start. This is the case only the ownership check
        // rejects: the second handler's activation would otherwise read the owner's running instance
        // as its own start.
        var (firstHost, firstContext) = await HostingTestHost.StartAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();

        try
        {
            var subject = new CountingHostedSubject();
            var firstHandler = firstContext.TryGetService<HostedServiceHandler>()!;
            var secondHandler = secondContext.TryGetService<HostedServiceHandler>()!;

            ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);
            await AsyncTestHelpers.WaitUntilAsync(() => subject.StartCount == 1);

            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);
            Assert.True(secondHandler.IsLive(subject));

            // Act
            var started = await secondHandler.WaitForStartAsync(subject, CancellationToken.None);

            // Assert
            Assert.False(started);
            Assert.Same(firstHandler, ((IInterceptorSubject)subject).TryGetSubjectTarget()!.Owner);
            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await firstHost.StopAsync();
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAHandlerIsAskedToWaitWhileItsOwnDrainIsStopping_ThenItAnswersWithoutQueueingBehindTheStop()
    {
        // Arrange - the drain clears liveness before it releases ownership, so a subject held inside
        // its own stop is the one window where the handler still owns the target and only the
        // liveness check can reject. Without it the call queues an empty transition behind that stop
        // and an activation would block host startup on another host's shutdown.
        var (host, context) = await HostingTestHost.StartAsync();

        var subject = new CountingHostedSubject();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subject.StopHold = () =>
        {
            stopEntered.TrySetResult();
            return releaseStop.Task;
        };

        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        await AsyncTestHelpers.WaitUntilAsync(() => subject.StartCount == 1);

        var drain = host.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            var target = ((IInterceptorSubject)subject).TryGetSubjectTarget()!;
            Assert.Same(handler, target.Owner);
            Assert.False(handler.IsLive(subject));

            // Act
            var wait = handler.WaitForStartAsync(subject, CancellationToken.None);

            // Assert - the guard answers before the first await, so the task is already complete. An
            // empty transition could not be: the stop ahead of it on the chain is still held.
            Assert.True(wait.IsCompleted, "The call queued behind the in flight stop instead of answering that it has no start.");
            Assert.False(await wait);
        }
        finally
        {
            releaseStop.TrySetResult();
            await drain;
        }
    }

    [Fact]
    public async Task WhenAStartFaultedForAWaitingCaller_ThenTheFaultIsRethrown()
    {
        // Arrange - the AddHostedService guarantee the activation preserves: a subject that fails to
        // start aborts host startup rather than leaving ApplicationStarted claiming it is running.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var subject = new ThrowingHostedSubject(context);
            var handler = context.TryGetService<HostedServiceHandler>()!;

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.WaitForStartAsync(subject, CancellationToken.None));

            Assert.Equal("start failed", exception.Message);

            // The fault was raised on the transition thread, and a plain rethrow overwrites its stack
            // with the rethrow point. This is the exception a failing subject aborts host startup
            // with, so it is the one a user reads.
            Assert.NotNull(exception.StackTrace);
            Assert.Contains(nameof(ThrowingHostedSubject), exception.StackTrace);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAFailedStartIsRethrownToTheAttachingCaller_ThenTheOriginalStackSurvives()
    {
        // Arrange - the same claim on the attach path, where the fault crosses from the transition
        // thread to the caller's. Two callers can also reach one fault instance concurrently, and a
        // plain rethrow leaves at most one of them a usable trace.
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                person.AttachHostedServiceAsync(FailingFactory, CancellationToken.None));

            Assert.NotNull(exception.StackTrace);
            Assert.Contains(nameof(FailingFactory), exception.StackTrace);
        });
    }

    /// <summary>A named frame, so the stack trace of the exception it raises is recognizable.</summary>
    private static TrackedBackgroundService FailingFactory()
        => throw new InvalidOperationException("factory failed");

    [Fact]
    public async Task WhenTheAttachAwaitIsCancelled_ThenTheStartStillRunsToCompletion()
    {
        // Arrange - the token bounds the caller's wait, not the transition. Aborting the work instead
        // would leave a half started service behind and record the cancellation as a start failure,
        // which the OPC UA wrappers surface as a user visible error status.
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService();
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => person.AttachHostedServiceAsync(() => instance, cancellation.Token));

            // Assert - waits for Current rather than for IsStarted, which the instance sets before its
            // StartAsync returns, while the handler records Current only afterwards.
            var attachment = Assert.Single(person.GetHostedServiceAttachments());
            await AsyncTestHelpers.WaitUntilAsync(
                () => attachment.Current is not null,
                message: "The cancelled await aborted the start transition instead of only the wait.");

            Assert.True(instance.IsStarted);
            Assert.Same(instance, attachment.Current);
            Assert.Null(attachment.Fault);
        });
    }

    [Fact]
    public void WhenASubjectWithoutHostedServicesIsRead_ThenNoDataEntryIsInserted()
    {
        // Arrange - both reads run on every context detach, under the lifecycle lock, for every
        // subject in the graph.
        var subject = (IInterceptorSubject)new Person();
        var entriesBefore = subject.Data.Count;

        // Act
        var target = subject.TryGetSubjectTarget();
        var attachments = subject.GetHostedServiceAttachments();

        // Assert
        Assert.Null(target);
        Assert.Empty(attachments);
        Assert.Equal(entriesBefore, subject.Data.Count);
    }

    [Fact]
    public void WhenASubjectWithoutHostedServicesIsDetached_ThenNothingIsAllocated()
    {
        // Arrange - one context detach reaches this for every subject in the detaching graph, under
        // the lifecycle lock, and almost none of them host anything.
        var handler = new HostedServiceHandler();
        var subject = (IInterceptorSubject)new Person();
        var change = new SubjectLifecycleChange
        {
            Subject = subject,
            ReferenceCount = 0,
            IsContextDetach = true
        };

        // Warm up so jit compilation does not land inside the measured window.
        for (var i = 0; i < 100; i++)
        {
            handler.HandleLifecycleChange(change);
        }

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            handler.HandleLifecycleChange(change);
        }

        // Assert
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void WhenASubjectTargetAlreadyExists_ThenReadingItAllocatesNothing()
    {
        // Arrange - a re-attach of a hosted subject reaches this, and building the target and its
        // chain lock before the lookup throws both away every time.
        var subject = (IInterceptorSubject)new CountingHostedSubject();
        var hostedService = (IHostedService)subject;
        var first = hostedService.GetOrAddSubjectTarget();

        for (var i = 0; i < 100; i++)
        {
            hostedService.GetOrAddSubjectTarget();
        }

        // Act
        var same = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            if (ReferenceEquals(first, hostedService.GetOrAddSubjectTarget()))
            {
                same++;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(1000, same);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public async Task WhenADrainedHandlerSeesAContextDetach_ThenTheLiveHandlersInstancesKeepRunning()
    {
        // Arrange - the drained handler is still wired into the graph it was started over, so a
        // later graph move still reaches it. It created none of the instances now running, so it
        // must stop and dispose none of them: whatever creates an instance disposes it, and only it.
        var (firstHost, firstContext) = await HostingTestHost.StartAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();

        try
        {
            var firstParent = new HostedParent(firstContext);
            var child = new CountingHostedSubject();
            var created = new ConcurrentQueue<TrackedBackgroundService>();

            var attachment = child.AttachHostedService(() =>
            {
                var instance = new TrackedBackgroundService();
                created.Enqueue(instance);
                return instance;
            });

            firstParent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(
                () => child.StartCount == 1 && created.ToArray() is [{ IsStarted: true }]);

            await firstHost.StopAsync();

            var secondParent = new HostedParent(secondContext);
            secondParent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(
                () => child.StartCount == 2 && created.ToArray() is [_, { IsStarted: true }]);

            // Act
            firstParent.Child = null;

            // Assert - the empty transitions drain what the detach appended to each chain.
            var subjectTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;
            var attachmentTarget = ((IHostedServiceAttachmentTarget)attachment).Target;
            await subjectTarget.AppendAsync(() => Task.CompletedTask);
            await attachmentTarget.AppendAsync(() => Task.CompletedTask);

            Assert.Equal(1, child.StopCount);
            Assert.NotNull(subjectTarget.Current);
            Assert.NotNull(attachment.Current);

            var instances = created.ToArray();
            Assert.False(instances[1].IsStopped);
            Assert.False(instances[1].IsDisposed);
        }
        finally
        {
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenANonOwningHandlerSeesAContextDetach_ThenTheOwnersInstanceKeepsRunning()
    {
        // Arrange - the same rule with both handlers live. The second handler loses the compare and
        // exchange on every target, so it started nothing and has nothing to stop.
        var (firstHost, firstContext) = await HostingTestHost.StartAsync();
        var (secondHost, secondContext) = await HostingTestHost.StartAsync();

        try
        {
            var firstParent = new HostedParent(firstContext);
            var secondParent = new HostedParent(secondContext);
            var child = new CountingHostedSubject();
            var instance = new TrackedBackgroundService();
            var attachment = child.AttachHostedService(() => instance);

            firstParent.Child = child;
            await AsyncTestHelpers.WaitUntilAsync(() => child.StartCount == 1 && instance.IsStarted);

            secondParent.Child = child;

            // Act
            secondParent.Child = null;

            // Assert
            var subjectTarget = ((IInterceptorSubject)child).TryGetSubjectTarget()!;
            var attachmentTarget = ((IHostedServiceAttachmentTarget)attachment).Target;
            await subjectTarget.AppendAsync(() => Task.CompletedTask);
            await attachmentTarget.AppendAsync(() => Task.CompletedTask);

            Assert.Equal(0, child.StopCount);
            Assert.NotNull(subjectTarget.Current);
            Assert.NotNull(attachment.Current);
            Assert.False(instance.IsStopped);
            Assert.False(instance.IsDisposed);
        }
        finally
        {
            await firstHost.StopAsync();
            await secondHost.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAStopIsCancelled_ThenTheInstanceIsStillDisposed()
    {
        // Arrange - the stop clears Current before it runs the instance, so an instance whose
        // StopAsync is cut short by the token is unreachable afterwards. The handler created it, so
        // it owes the dispose whatever the stop itself managed to do.
        await HostingTestHost.RunAsync(async context =>
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService();
            var attachment = await person.AttachHostedServiceAsync(() => instance, CancellationToken.None);

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => person.DetachHostedServiceAsync(attachment, cancellation.Token));

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => instance.IsDisposed,
                message: "The cancelled stop escaped the transition body and skipped the dispose.");

            Assert.False(instance.IsStopped);
            Assert.Null(attachment.Fault);
        });
    }

    [Fact]
    public async Task WhenTheShutdownTokenIsAlreadyCancelled_ThenTheInstanceIsStillDisposed()
    {
        // Arrange - the ordinary HostOptions.ShutdownTimeout path: the drain hands the stopping
        // token straight to every instance, so an expired timeout cancels each StopAsync it runs.
        var (host, context) = await HostingTestHost.StartAsync();

        var person = new Person(context);
        var instance = new TrackedBackgroundService();
        await person.AttachHostedServiceAsync(() => instance, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        await host.StopAsync(cancellation.Token);

        // Assert - the drain's barrier is bounded by the same token, so a deadline that has already
        // passed buys the stops no waiting at all and the drain returns while this one is still
        // running. The dispose is still owed, and is observed rather than read synchronously: the
        // stop clears Current before it runs the instance, so an instance that escaped here would be
        // unreachable and never disposed.
        await AsyncTestHelpers.WaitUntilAsync(
            () => instance.IsDisposed,
            message: "The stop the drain stopped waiting for never disposed the instance it had already unpublished.");

        Assert.False(instance.IsStopped);
    }

    [Fact]
    public async Task WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout()
    {
        // Arrange - the wrapper shape, and the regression guard for the deadlock the wrappers were
        // migrated away from: a subject that detaches its own attachment from its own unwind waits on
        // a chain that is waiting on that unwind, and the host recovers only when ShutdownTimeout
        // expires, so the elapsed time is what tells the two apart.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);

        var context = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        var subject = new SubjectOwningAnAttachment(context);
        await AsyncTestHelpers.WaitUntilAsync(
            () => subject.Instance?.IsStarted == true,
            message: "The attachment never started, so the shutdown below would prove nothing.");

        // Act
        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync();
        stopwatch.Stop();

        // Assert
        Assert.True(
            stopwatch.Elapsed < ShutdownTimeout / 2,
            $"Shutdown took {stopwatch.Elapsed.TotalSeconds:F1} seconds of a " +
            $"{ShutdownTimeout.TotalSeconds:F1} second timeout, which is the deadlock signature.");

        Assert.True(subject.Instance!.IsStopped);
        Assert.True(subject.Instance!.IsDisposed);
    }

    [Fact]
    public async Task WhenAServiceStopNeverReturns_ThenShutdownDoesNotOutlastTheTimeout()
    {
        // Arrange - the stopping token reaches the instance, but a service that ignores it is what
        // the drain's own barrier has to bound. Untokened, the barrier waits for a stop that never
        // returns and the process never leaves StopAsync, whatever ShutdownTimeout says.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = WedgedShutdownTimeout);

        var context = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subject = new CountingHostedSubject { StopHold = () => wedged.Task };

        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        await AsyncTestHelpers.WaitUntilAsync(
            () => subject.StartCount == 1,
            message: "The subject never started, so the shutdown below would prove nothing.");

        try
        {
            // Act - the outer wait is what turns an unbounded drain into a failure rather than a hang
            var stopwatch = Stopwatch.StartNew();
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            stopwatch.Stop();

            // Assert
            Assert.True(
                stopwatch.Elapsed < WedgedShutdownTimeout * 3,
                $"Shutdown took {stopwatch.Elapsed.TotalSeconds:F1} seconds of a " +
                $"{WedgedShutdownTimeout.TotalSeconds:F1} second timeout.");
        }
        finally
        {
            // Lets the transition the drain gave up on finish, so it does not outlive the test.
            wedged.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenTheSubjectStopFindsNothingRunning_ThenTheAttachmentStopItGatesStillRuns()
    {
        // Arrange - the attachment's stop waits for the subject's stop, and a subject whose start
        // never ran has nothing to stop, so the early return in that stop is an ordinary case rather
        // than an edge one. Its signal has to be set from a finally: set after the body instead, the
        // attachment stop parks on it forever and wedges that chain against every later append. The
        // host is built but not started, so the startup gate holds both starts at a known point and
        // the graph moves below provably overtake them.
        var builder = HostingTestHost.CreateBuilder();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = WedgedShutdownTimeout);

        var context = HostingTestHost.CreateContext(builder);

        var host = builder.Build();

        var parent = new HostedParent(context);
        var child = new CountingHostedSubject();
        var instance = new TrackedBackgroundService();
        var attachment = child.AttachHostedService(() => instance);

        parent.Child = child;
        parent.Child = null;

        // Act
        await host.StartAsync();

        try
        {
            // Assert - an empty transition behind the attachment's stop completes only once that stop
            // has run, which it can only do once the subject's stop released it.
            await attachment.DrainAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, child.StartCount);
            Assert.False(instance.IsStarted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAwaitedAttachRunsOnAHostThatWasNeverStarted_ThenItStillReturns()
    {
        // Arrange - awaiting is an explicit request for the service to be running, so the awaiting
        // overloads open the startup gate themselves. Without that the start body parks on a gate
        // nothing is going to open, and the caller waits forever rather than getting an answer.
        var builder = HostingTestHost.CreateBuilder();

        var context = HostingTestHost.CreateContext(builder);

        var host = builder.Build();

        try
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService();

            // Act
            var attachment = await person
                .AttachHostedServiceAsync(() => instance, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Same(instance, attachment.Current);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAwaitedDetachRunsOnAHostThatWasNeverStarted_ThenItStillReturns()
    {
        // Arrange - the synchronous attach deliberately leaves the gate closed, which is what makes
        // the detach's own gate opening the only thing that can ever release its stop.
        var builder = HostingTestHost.CreateBuilder();

        var context = HostingTestHost.CreateContext(builder);

        var host = builder.Build();

        try
        {
            var person = new Person(context);
            var instance = new TrackedBackgroundService();
            var attachment = person.AttachHostedService(() => instance);

            // Act
            var detached = await person
                .DetachHostedServiceAsync(attachment, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            // Assert - the stop is queued behind the start the attach appended, so both have run.
            Assert.True(detached);
            Assert.True(instance.IsDisposed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenASubjectHostsNothing_ThenTheAttachRecordsNoLiveness()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;

            // Act
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            // Assert - the fast path, and the whole reason a graph of subjects that host nothing costs
            // nothing to attach. Every reader of liveness holds a target when it reads, so a subject
            // with no target has no reader and needs no entry.
            Assert.False(handler.IsLive(parent));
            Assert.False(handler.IsLive(child));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedToASubjectAlreadyInTheGraph_ThenItStarts()
    {
        // Arrange - the subject hosted nothing when it entered, so the attach path recorded no
        // liveness for it. The attachment has to establish liveness itself, or its start is refused
        // for a subject that is in the graph the whole time.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;
            Assert.False(handler.IsLive(child));

            // Act
            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());

            // Assert
            await attachment.DrainAsync();
            Assert.True(handler.IsLive(child));
            Assert.NotNull(attachment.Current);
            Assert.True(attachment.Current!.IsStarted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenASubjectLostItsLastAttachmentBeforeLeavingTheGraph_ThenALaterAttachmentStartsNothing()
    {
        // Arrange - the one way a liveness entry can outlive the graph membership it stands for.
        // Detaching an attachment must not clear liveness, because a start already appended re-reads
        // it, so a subject that loses its last attachment leaves the graph through the fast path and
        // keeps its entry. Reading it for a new attachment is where that has to be caught. The subject
        // is constructed with the context so its own context keeps resolving the handler after the
        // detach and the attach really does reach it.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person(context);
            parent.Child = child;

            var first = child.AttachHostedService(() => new TrackedBackgroundService());
            Assert.True(handler.IsLive(child));

            child.DetachHostedService(first);
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
            Assert.False(handler.IsLive(child));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentIsReplacedWhileTheSubjectStaysInTheGraph_ThenTheNewOneStarts()
    {
        // Arrange - the same subject crossing from hosting something to hosting nothing and back
        // without ever leaving the graph, which is the sequence that decides whether liveness recorded
        // lazily can be trusted a second time.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var first = child.AttachHostedService(() => new TrackedBackgroundService());
            await first.DrainAsync();
            Assert.NotNull(first.Current);

            child.DetachHostedService(first);

            // Act
            var second = child.AttachHostedService(() => new TrackedBackgroundService());

            // Assert
            await second.DrainAsync();
            Assert.True(handler.IsLive(child));
            Assert.NotNull(second.Current);
            Assert.True(second.Current!.IsStarted);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAHostedSubjectLeavesTheGraph_ThenItsLivenessIsCleared()
    {
        // Arrange - the detach side of lazy recording. The fast path must not swallow the clear for a
        // subject that does host something, or a later attachment starts a service for a subject that
        // has left the graph.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person(context);
            parent.Child = child;

            child.AttachHostedService(() => new TrackedBackgroundService());
            Assert.True(handler.IsLive(child));

            // Act
            parent.Child = null;

            // Assert
            Assert.False(handler.IsLive(child));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentIsDetachedAndTheSubjectLeavesBeforeItsQueuedStartRuns_ThenNothingIsCreated()
    {
        // Arrange - the host is built but never started, so the startup gate parks the queued start at
        // a known point and the whole sequence is deterministic rather than a race. The subject hosts
        // nothing by the time it leaves the graph, which is exactly the case a context detach must not
        // treat as "nothing to clear": the start queued against the detached attachment re-reads
        // liveness in its body and would otherwise find an entry that outlived the graph membership.
        var builder = HostingTestHost.CreateBuilder();
        var context = HostingTestHost.CreateContext(builder);
        var host = builder.Build();

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        var created = 0;
        var attachment = child.AttachHostedService(() =>
        {
            Interlocked.Increment(ref created);
            return new TrackedBackgroundService();
        });

        // Act
        child.DetachHostedService(attachment);
        parent.Child = null;

        await host.StartAsync();
        await attachment.DrainAsync();

        try
        {
            // Assert
            Assert.Equal(0, Volatile.Read(ref created));
            Assert.Null(attachment.Current);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenASubjectThatOnceHostedSomethingLeavesTheGraph_ThenItsLivenessIsCleared()
    {
        // Arrange - the retention side of the same fact. Liveness is recorded lazily, so a context
        // detach could skip its clear for a subject that hosts nothing at that moment; an entry left
        // behind roots the subject on the handler for the whole life of the host.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var attachment = child.AttachHostedService(() => new TrackedBackgroundService());
            Assert.True(handler.IsLive(child));
            child.DetachHostedService(attachment);

            // Act
            parent.Child = null;

            // Assert
            Assert.False(handler.IsLive(child));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentRecordsLiveness_ThenAGraphMoveCannotLandBetweenTheCheckAndTheWrite()
    {
        // Arrange - the membership answer and the liveness write have to be one step. Split, a graph
        // move landing between them makes the write land on the opposite answer: a detach that just
        // cleared liveness has it re-armed, and a start then runs for a subject that has left the
        // graph. The window is nanoseconds wide, so it is driven by a seam rather than by repetition:
        // the seam sits where the write does, and holding it holds the graph mutation lock, so a
        // concurrent move provably cannot proceed.
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var handler = context.TryGetService<HostedServiceHandler>()!;
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            // The mover spins rather than waiting on a handle, so that once it is released the only
            // thing that can park it is the graph mutation lock itself.
            var release = 0;
            var moverEntered = 0;
            var moveFinished = 0;
            var moveRanDuringTheWrite = false;

            var mover = new Thread(() =>
            {
                while (Volatile.Read(ref release) == 0)
                {
                    Thread.SpinWait(50);
                }

                Volatile.Write(ref moverEntered, 1);
                parent.Child = null;
                Volatile.Write(ref moveFinished, 1);
            });

            handler.LivenessWriteGate = () =>
            {
                Volatile.Write(ref release, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref moverEntered) == 1, TimeSpan.FromSeconds(30));

                // The assertion is on the move COMPLETING, not on the mover's thread state: a thread
                // parked on its own start signal also reads as waiting, which would pass this whether
                // or not the write is serialized. Completion cannot happen while this callback holds
                // the graph mutation lock, so a completion here is the defect itself. Recorded rather
                // than thrown, because this runs under that lock.
                moveRanDuringTheWrite = SpinWait.SpinUntil(
                    () => Volatile.Read(ref moveFinished) == 1,
                    TimeSpan.FromSeconds(2));
            };

            mover.Start();

            // Act - joined in a finally, or an attach that throws leaves the mover spinning for the
            // rest of the run on a thread nothing collects.
            try
            {
                child.AttachHostedService(() => new TrackedBackgroundService());
            }
            finally
            {
                Volatile.Write(ref release, 1);
                mover.Join(TimeSpan.FromSeconds(30));
            }

            // Assert
            Assert.False(
                moveRanDuringTheWrite,
                "The graph move completed while liveness was being written, so the membership answer and the write are not one step.");
            Assert.Equal(1, Volatile.Read(ref moveFinished));

            // The detach ran after the write, so it cleared the entry the write had just made. Liveness
            // agreeing with membership is the invariant the atomicity exists to keep.
            Assert.False(handler.IsLive(child));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachmentIsAddedAfterTheDrainClearedLiveness_ThenTheSubjectIsNotLeftLive()
    {
        // Arrange - the drain window MarkLiveIfAttached's own gate reads exist for, and the only one
        // that reaches them. Attaching while the drain is parked at DrainGate heals itself, because
        // the clear still follows; this parks the drain inside a stop body instead, past
        // StopAsync's _liveSubjects.Clear(), so an entry written here is one nothing ever removes and
        // it roots the subject on a dead handler for the life of the process.
        var (host, context) = await HostingTestHost.StartAsync();
        var handler = context.TryGetService<HostedServiceHandler>()!;

        var running = new CountingHostedSubject();
        ((IInterceptorSubject)running).Context.AddFallbackContext(context);
        await AsyncTestHelpers.WaitUntilAsync(() => running.StartCount == 1);

        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.StopHold = () =>
        {
            stopEntered.TrySetResult();
            return release.Task;
        };

        var parent = new Parent(context);
        var child = new Person();
        parent.Child = child;

        // The drain has to begin after this attach passed the entry gate read and before its write
        // lands, which is the only interleaving the re-read below covers and the reason it is not
        // redundant with the read on entry. Driven through the seam, because the window is two
        // adjacent statements.
        var writeReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.LivenessWriteGate = () =>
        {
            writeReached.TrySetResult();
            releaseWrite.Task.GetAwaiter().GetResult();
        };

        var attaching = Task.Run(() => child.AttachHostedService(() => new TrackedBackgroundService()));
        await writeReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Act - the drain runs to past its liveness clear while the write is held. Nothing on this
        // path needs the lifecycle lock the held write is holding.
        var stopping = host.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await AsyncTestHelpers.WaitUntilAsync(
            () => !handler.IsLive(running),
            message: "The drain never reached its liveness clear.");

        releaseWrite.SetResult();
        await attaching.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert - the write landed on a handler whose drain had already cleared the set, so the
        // re-read after it is the only thing that stops the entry outliving the handler.
        Assert.False(handler.IsLive(child));

        handler.LivenessWriteGate = null;
        release.SetResult();
        await stopping;

        Assert.False(handler.IsLive(child));
    }

    [Fact]
    public void WhenAnAttachmentIsDetachedFromASubjectThatNeverHadOne_ThenThatSubjectIsNotRecordedAsEverHosting()
    {
        // Arrange - the detach path's fast path turns on whether a subject has ever hosted anything,
        // and that answer is the presence of the attachments data key. A refused detach must not
        // create it: the subject would then pay the full detach path for the rest of its life, which
        // is the cost the fast path exists to avoid.
        var owner = new Person();
        var bystander = new Person();
        var attachment = owner.AttachHostedService(() => new TrackedBackgroundService());

        // Act
        var removed = bystander.DetachHostedService(attachment);

        // Assert
        Assert.False(removed);
        Assert.False(((IInterceptorSubject)bystander).TryGetHostedServiceAttachments(out var attachments));
        Assert.Empty(attachments);
        Assert.True(((IInterceptorSubject)owner).TryGetHostedServiceAttachments(out _));
    }

    [Fact]
    public async Task WhenTakingAStartupHoldThrows_ThenTheAttachStillStartsAndTheOtherHoldsAreReleased()
    {
        // Arrange - taking the holds is third party code on the attach path, and the attach runs inside
        // a property write, so an exception escaping it surfaces at an unrelated assignment. The
        // throwing deferrer sits between two working ones, so the guard has to do both halves: keep
        // the hold already taken before it, and go on to take the one after it. Either failure leaves
        // a host that never finishes starting.
        var builder = HostingTestHost.CreateBuilder();
        var context = HostingTestHost.CreateContext(builder);

        var throwing = new ThrowingStartupDeferrer { ThrowOnDefer = true };
        var before = new CallbackStartupDeferrer();
        var working = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(before);
        context.AddService<IStartupCompletionDeferrer>(throwing);
        context.AddService<IStartupCompletionDeferrer>(working);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            // Act
            var attachment = await child.AttachHostedServiceAsync(
                () => new TrackedBackgroundService(), CancellationToken.None);

            // Assert
            await attachment.DrainAsync();
            Assert.True(attachment.Current is { IsStarted: true });
            Assert.Null(attachment.Fault);

            Assert.Equal(1, throwing.Taken);
            Assert.Equal(1, before.Taken);
            Assert.Equal(1, working.Taken);
            Assert.Equal(0, before.Outstanding);
            Assert.Equal(0, working.Outstanding);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenReleasingAStartupHoldThrows_ThenTheOtherHoldsAreStillReleased()
    {
        // Arrange - the release runs in a finally on the transition thread, so an exception there
        // faults the transition rather than any caller, and the holds behind it are never released:
        // the host then waits on a completion that never comes. The throwing one is registered first
        // so the working one is the one that would be stranded.
        var builder = HostingTestHost.CreateBuilder();
        var context = HostingTestHost.CreateContext(builder);

        var throwing = new ThrowingStartupDeferrer { ThrowOnRelease = true };
        var working = new CallbackStartupDeferrer();
        context.AddService<IStartupCompletionDeferrer>(throwing);
        context.AddService<IStartupCompletionDeferrer>(working);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            // Act
            var attachment = await child.AttachHostedServiceAsync(
                () => new TrackedBackgroundService(), CancellationToken.None);

            // Assert
            await attachment.DrainAsync();
            Assert.True(attachment.Current is { IsStarted: true });
            Assert.Null(attachment.Fault);

            Assert.Equal(1, throwing.Released);
            Assert.Equal(0, working.Outstanding);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenDisposingAnInstanceThrows_ThenTheDetachStillCompletesAndARepeatAttachStarts()
    {
        // Arrange - a factory instance whose disposal throws is still stopped, still counted as
        // disposed once, and still leaves the subject able to host again. The stop path is where the
        // catch inside DisposeInstanceAsync only reports; the test below it covers the path where that
        // catch also contains.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var first = new TrackedBackgroundService { ThrowOnDispose = true };
            var attachment = await child.AttachHostedServiceAsync(() => first, CancellationToken.None);
            Assert.True(first.IsStarted);

            // Act
            var detached = await child.DetachHostedServiceAsync(attachment, CancellationToken.None);

            // Assert
            Assert.True(detached);
            Assert.True(first.IsStopped);
            Assert.Equal(1, first.DisposeCount);

            var second = await child.AttachHostedServiceAsync(
                () => new TrackedBackgroundService(), CancellationToken.None);

            Assert.True(second.Current is { IsStarted: true });
            Assert.Null(second.Fault);
        });
    }

    [Fact]
    public async Task WhenAFailedStartsCleanupDisposeAlsoThrows_ThenTheCallerStillGetsTheStartException()
    {
        // Arrange - a start that throws is cleaned up by disposing what it created, and that dispose
        // runs inside a catch whose job is to rethrow the start's own exception afterwards. An escape
        // from the dispose skips that rethrow, so the caller is handed the cleanup failure and never
        // learns why the start failed, which is the exception they are actually waiting for.
        await HostingTestHost.RunAsync(async context =>
        {
            var parent = new Parent(context);
            var child = new Person();
            parent.Child = child;

            var instance = new TrackedBackgroundService { ThrowOnStart = true, ThrowOnDispose = true };

            // Act
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => child.AttachHostedServiceAsync(() => instance, CancellationToken.None));

            // Assert
            Assert.Equal("start failed", thrown.Message);
            Assert.Equal(1, instance.DisposeCount);
        });
    }
}
