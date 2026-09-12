using Namotion.Interceptor.Hosting.Tests.Models;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceTargetTests
{
    /// <summary>
    /// One round of the append race does not reliably detect an unsynchronised chain, so a single
    /// round per continuous integration run would let a regression through. Measured against a build
    /// with the append lock removed: one round failed 13 of 15 runs, twelve rounds failed 15 of 15.
    /// </summary>
    private const int AppendRaceRounds = 12;

    /// <summary>
    /// Rounds of the take against release race. Each round is a thread pair, so this is the expensive
    /// kind of round, and it is still not the place to economise: measured against a build without the
    /// ownership lock, 2,000 rounds failed 8 of 8 runs at 90 ms each, and 200 rounds failed 2 of 8.
    /// </summary>
    private const int OwnershipRaceRounds = 2000;

    /// <summary>
    /// How far the releasing thread's park is swept across the window, in spins. The window is a few
    /// instructions wide and its position moves with the scheduler, so a fixed park finds it far less
    /// often than a sweep of the same total length does.
    /// </summary>
    private const int OwnershipRaceSpinSweep = 64;

    /// <summary>
    /// Appends onto an already completed tail. Measured against a build with the increment moved below
    /// the append, this reported a negative count on 7 of 7 runs and takes under a second.
    /// </summary>
    private const int CompletedTailAppends = 300000;

    [Fact]
    public async Task WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap()
    {
        // Arrange - an unsynchronised "_tail = _tail.ContinueWith(...)" is a read-modify-write and
        // loses an assignment under contention, running several transitions on one target at once.
        var maximumConcurrent = 0;

        // Act
        for (var round = 0; round < AppendRaceRounds; round++)
        {
            maximumConcurrent = Math.Max(maximumConcurrent, await RunAppendRaceAsync());
        }

        // Assert
        Assert.Equal(1, maximumConcurrent);
    }

    /// <summary>
    /// Races eight appends against one target while its head transition is held, and returns the
    /// highest number of transition bodies that were ever running at once.
    /// </summary>
    private static async Task<int> RunAppendRaceAsync()
    {
        var target = new HostedServiceTarget(factory: null, subject: null);
        var head = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximumConcurrent = 0;
        var sync = new object();

        var stall = target.AppendAsync(async () => await head.Task);

        async Task BodyAsync()
        {
            lock (sync)
            {
                concurrent++;
                maximumConcurrent = Math.Max(maximumConcurrent, concurrent);
            }

            await Task.Yield();

            lock (sync)
            {
                concurrent--;
            }
        }

        // Real threads and a barrier rather than Task.Run, which unwraps Func<Task> and would wait
        // for each transition instead of just for the append.
        const int appenderCount = 8;
        var transitions = new Task[appenderCount];
        var threads = new Thread[appenderCount];
        using var barrier = new Barrier(appenderCount);

        for (var index = 0; index < appenderCount; index++)
        {
            var slot = index;
            threads[slot] = new Thread(() =>
            {
                barrier.SignalAndWait();
                transitions[slot] = target.AppendAsync(BodyAsync);
            });

            threads[slot].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        head.SetResult();
        await stall;
        await Task.WhenAll(transitions);

        lock (sync)
        {
            return maximumConcurrent;
        }
    }

    [Fact]
    public async Task WhenATransitionThrows_ThenTheChainContinues()
    {
        // Arrange - a faulted tail would raise UnobservedTaskException for every dropped fire and
        // forget transition, and the fault would surface nowhere at all.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var secondRan = false;

        // Act
        await target.AppendAsync(() => throw new InvalidOperationException("boom"));
        await target.AppendAsync(() =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        // Assert
        Assert.True(secondRan);
    }

    [Fact]
    public async Task WhenATransitionIsAppended_ThenItDoesNotRunOnTheAppendingThread()
    {
        // Arrange - appends happen while LifecycleInterceptor holds its lock, so an inline
        // continuation would run a transition body under that lock. The append runs on a dedicated
        // thread rather than the test's own: xunit runs tests on pool threads, and a body queued to
        // the pool can be picked up by the very thread that appended once that thread parks on the
        // await below, which reads as inline execution without being it.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var ranInline = false;
        Task? transition = null;

        var appendingThread = new Thread(() =>
        {
            var appendingThreadId = Environment.CurrentManagedThreadId;
            transition = target.AppendAsync(() =>
            {
                ranInline = Environment.CurrentManagedThreadId == appendingThreadId;
                return Task.CompletedTask;
            });
        });

        // Act
        appendingThread.Start();
        appendingThread.Join();
        await transition!;

        // Assert
        Assert.False(ranInline);
    }

    [Fact]
    public async Task WhenTheTransitionGateIsSet_ThenTheTransitionIsHeld()
    {
        // Arrange - the seam the ordering and race tests need, so they do not depend on timing
        var target = new HostedServiceTarget(factory: null, subject: null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = false;

        target.TransitionGate = () => release.Task;

        // Act
        var transition = target.AppendAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        // Assert
        Assert.False(ran);
        release.SetResult();
        await transition;
        Assert.True(ran);
    }

    [Fact]
    public void WhenATakeRacesARelease_ThenTheOwnerAndTheRecordNeverDisagree()
    {
        // Arrange - the exchange and the handler's record are one fact, and a release landing between
        // them nulls the owner, finds no record to retire, and leaves one that no later release can
        // match. The window is a few instructions wide, so the release is swept across it rather than
        // fired once: each round parks the releasing thread a different number of spins in.
        var leaked = 0;
        var unrecorded = 0;

        // Act
        for (var round = 0; round < OwnershipRaceRounds; round++)
        {
            var target = new HostedServiceTarget(factory: null, subject: null);
            var handler = new HostedServiceHandler();
            var subject = new Person();

            using var start = new Barrier(2);
            var spins = round % OwnershipRaceSpinSweep;

            var releasing = new Thread(() =>
            {
                start.SignalAndWait();
                Thread.SpinWait(spins);
                target.ReleaseOwnership(handler);
            });

            releasing.Start();
            start.SignalAndWait();
            target.TryTakeOwnership(handler, subject, out _);
            releasing.Join();

            // Read after both threads finished, so this is the settled state rather than a snapshot of
            // the race. Both directions are counted: the first is a record nothing can ever retire, the
            // second is a running target no drain snapshot contains, which is the worse of the two and
            // is what retiring the record unconditionally trades the first one for.
            if (handler.IsOwned(target) && !ReferenceEquals(target.Owner, handler))
            {
                leaked++;
            }

            if (!handler.IsOwned(target) && ReferenceEquals(target.Owner, handler))
            {
                unrecorded++;
            }
        }

        // Assert
        Assert.Equal(0, leaked);
        Assert.Equal(0, unrecorded);
    }

    [Fact]
    public async Task WhenTransitionsAreAppendedOntoACompletedTail_ThenTheInFlightCountIsNeverNegative()
    {
        // Arrange - the increment sits ahead of the ContinueWith. Below it, an already completed tail's
        // continuation decrements before the appending thread reaches the increment, and the count goes
        // negative: a later increment then brings it back to zero while a transition is still running,
        // which is a drain returning into a service provider the host is disposing. A sampling thread
        // reaches the gap that no seam can, because the two statements are adjacent.
        var target = new HostedServiceTarget(factory: null, subject: null);
        var handler = new HostedServiceHandler();

        var stopSampling = false;
        var minimum = 0;

        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref stopSampling))
            {
                minimum = Math.Min(minimum, handler.InFlightTransitionCount);
            }
        })
        {
            IsBackground = true
        };

        sampler.Start();

        // Act - awaited one at a time, so every append but the first lands on a completed tail, which
        // is the only shape whose continuation can run before the appending thread's next statement.
        for (var append = 0; append < CompletedTailAppends; append++)
        {
            await target.AppendAsync(handler, () => Task.CompletedTask);
        }

        Volatile.Write(ref stopSampling, true);
        sampler.Join();

        // Assert
        Assert.Equal(0, minimum);
    }

    [Fact]
    public void WhenOwnershipIsTakenTwiceByTheSameHandler_ThenItSucceeds()
    {
        // Arrange - a re-attach arriving before the release must not read as "lost to another handler"
        var target = new HostedServiceTarget(factory: null, subject: null);
        var handler = new HostedServiceHandler();
        var subject = new Person();

        // Act
        var first = target.TryTakeOwnership(handler, subject, out var firstTaken);
        var second = target.TryTakeOwnership(handler, subject, out var secondTaken);

        // Assert - both succeed, but only the first installed the owner. A caller that has to undo
        // its own take must not undo the earlier one, whose instance may still be running.
        Assert.True(first);
        Assert.True(second);
        Assert.True(firstTaken);
        Assert.False(secondTaken);
    }

    [Fact]
    public void WhenASecondHandlerTakesOwnership_ThenItFails()
    {
        // Arrange
        var target = new HostedServiceTarget(factory: null, subject: null);
        var first = new HostedServiceHandler();
        var second = new HostedServiceHandler();
        var subject = new Person();
        target.TryTakeOwnership(first, subject, out _);

        // Act
        var taken = target.TryTakeOwnership(second, subject, out _);

        // Assert
        Assert.False(taken);
        Assert.Same(first, target.Owner);
    }

    [Fact]
    public void WhenOwnershipIsReleased_ThenAnotherHandlerCanTakeIt()
    {
        // Arrange - release on context detach is what lets a subject move between contexts
        var target = new HostedServiceTarget(factory: null, subject: null);
        var first = new HostedServiceHandler();
        var second = new HostedServiceHandler();
        var subject = new Person();
        target.TryTakeOwnership(first, subject, out _);

        // Act
        target.ReleaseOwnership(first);
        var taken = target.TryTakeOwnership(second, subject, out _);

        // Assert
        Assert.True(taken);
        Assert.Same(second, target.Owner);
    }
}
