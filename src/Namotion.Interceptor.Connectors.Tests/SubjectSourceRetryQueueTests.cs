using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceRetryQueueTests
{
    [Fact]
    public async Task WhenWriteFailsWithoutEnumeratedFailedChanges_ThenChangesAreQueuedAndRetried()
    {
        // Arrange: real context with a running SubjectSourceBase pump; the source fails FirstName
        // writes wholesale (error without enumerated failed changes) while the flag is set.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var failWholesale = false;
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    var batch = changes.ToArray();
                    if (failWholesale && batch.Any(change => change.Property.Name == nameof(Person.FirstName)))
                    {
                        return ValueTask.FromResult(WriteResult.Failure(
                            ReadOnlyMemory<SubjectPropertyChange>.Empty,
                            new InvalidOperationException("Wholesale boom")));
                    }

                    foreach (var change in batch)
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                    return ValueTask.FromResult(WriteResult.Success);
                }
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Wait until the pump processes outbound changes. The probe is re-written on each
            // poll because writes enqueued before the pump's subscription exists are not seen.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
            }, message: "Pump did not start processing changes.");

            // Act: fail the FirstName write wholesale; the change must land in the retry queue.
            lock (gate)
            {
                failWholesale = true;
            }
            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "Wholesale-failed write was not queued for retry.");

            // Recover the source; subsequent outbound writes flush the retry queue first.
            lock (gate)
            {
                failWholesale = false;
            }
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=John");
                }
            }, message: "Queued write was not retried after recovery.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheSourceIsIdleWithParkedWrites_ThenTheyAreFlushedWithoutAFurtherChange()
    {
        // Arrange: same live-pump arrangement as WhenWriteFailsWithoutEnumeratedFailedChanges_..., with
        // a short retryTime so the idle tick fires inside the test, and a write that fails once and is
        // parked.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var failWrites = false;
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8),
            retryTime: TimeSpan.FromMilliseconds(200))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    var batch = changes.ToArray();
                    if (failWrites && batch.Any(change => change.Property.Name == nameof(Person.FirstName)))
                    {
                        return ValueTask.FromResult(WriteResult.Failure(
                            ReadOnlyMemory<SubjectPropertyChange>.Empty,
                            new InvalidOperationException("Simulated failure")));
                    }

                    foreach (var change in batch)
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                    return ValueTask.FromResult(WriteResult.Success);
                }
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Wait until the pump processes outbound changes. The probe is re-written on each
            // poll because writes enqueued before the pump's subscription exists are not seen.
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
            }, message: "Pump did not start processing changes.");

            lock (gate)
            {
                failWrites = true;
            }
            person.FirstName = "John";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The failed write was not parked.");

            // Act: let writes succeed again and then make NO further change to any property.
            lock (gate)
            {
                failWrites = false;
            }

            // Assert: the idle tick drains the parked write on its own; nothing here writes a property.
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=John");
                }
            }, message: "The parked write was never drained while the model was idle.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeIsInProgress_ThenOutboundWritesAreParkedInsteadOfSent()
    {
        // Arrange: a live pump whose writes all succeed, so anything not sent was parked by the gate.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        try
        {
            // Act
            source.BeginResumeForTest();
            person.FirstName = "Parked";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write was not parked while the resume gate was set.");

            // Assert
            lock (gate)
            {
                Assert.DoesNotContain("FirstName=Parked", receivedWrites);
            }
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeCompletes_ThenTheParkedWriteReachesTheSource()
    {
        // Arrange: a live pump with a FirstName write parked while the resume gate is set.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        var resumeEpoch = source.BeginResumeForTest();
        person.FirstName = "Parked";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundRetries.Depth > 0,
            message: "The write was not parked while the resume gate was set.");
        try
        {
            // Act
            await source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=Parked");
                }
            }, message: "The parked write was not sent after the resume completed.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenResumeCompletes_ThenANewWriteAfterwardsReachesTheSource()
    {
        // Arrange: the resume has already completed and its own parked write already reached the
        // source through the reconcile's direct send, which bypasses the gate entirely. Only a
        // distinct write made afterwards, through the normal gated path, can prove the gate itself
        // was cleared rather than left set.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        var resumeEpoch = source.BeginResumeForTest();
        person.FirstName = "Parked";
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.OutboundRetries.Depth > 0,
            message: "The write was not parked while the resume gate was set.");
        await source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            lock (gate)
            {
                return receivedWrites.Contains("FirstName=Parked");
            }
        }, message: "The parked write was not sent after the resume completed.");

        try
        {
            // Act
            person.LastName = "AfterResume";

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("LastName=AfterResume");
                }
            }, message: "A write made after the resume completed should reach the source, proving the gate was cleared.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOlderResumeCompletesAfterANewerOneHasStarted_ThenItDoesNotClearTheNewerResumesGate()
    {
        // Arrange: a live pump. A second resume takes over before the first one completes, the way
        // the WebSocket client's own reconnect loop can open a new BeginResume while the attempt
        // loop's first load is still in flight for the same connect window.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        try
        {
            // Act
            var olderEpoch = source.BeginResumeForTest();
            var newerEpoch = source.BeginResumeForTest();

            // The older resume completes first; its completion must not release the gate the newer
            // resume still owns. Satisfied here by CompleteResumeAsync's ownership guard rather than by
            // TryEndResume's atomicity, which WhenAResumeEndsWhileANewerOneIsStarting covers instead.
            await source.CompleteResumeForTestAsync(olderEpoch, CancellationToken.None);

            var depthBeforeWrite = source.Diagnostics.OutboundRetries.Depth;
            person.FirstName = "StillHeld";

            // Assert: the write parks rather than reaching the source, proving the gate is still up.
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > depthBeforeWrite,
                message: "The write should have parked because the newer resume still owns the gate.");
            lock (gate)
            {
                Assert.DoesNotContain("FirstName=StillHeld", receivedWrites);
            }

            // The newer resume completing does clear the gate, so a further write goes through.
            await source.CompleteResumeForTestAsync(newerEpoch, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=StillHeld");
                }
            }, message: "The parked write should reach the source once the newer resume completes.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnOlderResumeCompletesAfterANewerOneHasStarted_ThenItDoesNotSendTheParkedWrites()
    {
        // Arrange: a write is parked under the older resume, then a newer resume takes the gate over
        // before the older one completes. The newer resume has not loaded the peer's state yet, so the
        // older one has nothing to judge the parked write against and must not send it: reconciling
        // here would flush it onto the replacement connection ahead of its initial-state load.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        try
        {
            var olderEpoch = source.BeginResumeForTest();
            person.FirstName = "Parked";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write was not parked while the resume gate was set.");

            var newerEpoch = source.BeginResumeForTest();

            // Act
            await source.CompleteResumeForTestAsync(olderEpoch, CancellationToken.None);

            // Assert: the reconcile's send arm runs inside the await above, so the write would already
            // be recorded here if the superseded resume had judged and flushed it.
            lock (gate)
            {
                Assert.DoesNotContain("FirstName=Parked", receivedWrites);
            }

            Assert.False(source.Diagnostics.OutboundRetries.Depth == 0,
                "The parked write must stay queued for the resume that owns the gate.");

            // The owning resume is what judges and delivers it.
            await source.CompleteResumeForTestAsync(newerEpoch, CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=Parked");
                }
            }, message: "The parked write should reach the source once the owning resume completes.");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheGateIsAlreadyOpen_ThenACompletingResumeStillJudgesTheParkedWrites()
    {
        // Arrange: the attempt loop holds a resume while the connector's own reconnect loop opens and
        // then abandons a second one, which is what ReconnectAndResumeAsync's catch arms do when the
        // reconnect fails. The gate is open again and no resume owns it, so the attempt loop's own
        // completion is the only thing left that can judge what it parked. Deferring to "whoever owns
        // the gate" here would leave the entries unjudged for the idle drain to flush raw.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        try
        {
            var attemptEpoch = source.BeginResumeForTest();
            person.FirstName = "Parked";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write was not parked while the resume gate was set.");

            var reconnectEpoch = source.BeginResumeForTest();
            Assert.True(source.TryEndResumeForTest(reconnectEpoch));
            Assert.False(source.IsResumeGateHeldForTest);

            // Act
            await source.CompleteResumeForTestAsync(attemptEpoch, CancellationToken.None);

            // Assert: the reconcile's send arm runs inline, so no wait is needed and the idle drain
            // cannot be what delivered it.
            lock (gate)
            {
                Assert.Contains("FirstName=Parked", receivedWrites);
            }

            Assert.Equal(0, source.Diagnostics.OutboundRetries.Depth);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WhenTwoResumesOpenAtOnce_ThenTheNewerOneOwnsTheGate()
    {
        // Arrange: taking an epoch and publishing it are two steps, so two resumes opening together can
        // take 5 and 6 and publish in the other order, leaving the older one owning the gate while the
        // newer reconnect is the live one. No shipped connector calls BeginResume concurrently, but the
        // gate's contract is that two loops may hold it, so the invariant is pinned here rather than
        // left to the first connector that does.
        //
        // Probabilistic, and written to be sound rather than complete: it never fails on correct code.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance);

        const int iterations = 20_000;
        var first = 0;
        var second = 0;

        using var barrier = new Barrier(2);
        var opener = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                barrier.SignalAndWait();
                Volatile.Write(ref second, source.BeginResumeForTest());
                barrier.SignalAndWait();
            }
        });

        opener.Start();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            barrier.SignalAndWait();
            Volatile.Write(ref first, source.BeginResumeForTest());
            barrier.SignalAndWait();

            // Act & Assert: two resumes must never be handed the same epoch, or either could end the
            // other's gate, and whichever took the higher one must be the resume holding it.
            var older = Volatile.Read(ref first);
            var newer = Volatile.Read(ref second);
            Assert.True(older != newer,
                $"Two concurrent resumes were handed the same epoch {older} (iteration {iteration}).");
            Assert.True(source.TryEndResumeForTest(Math.Max(older, newer)),
                $"The older of two concurrent resumes ended up owning the gate (iteration {iteration}).");
        }

        opener.Join();
    }

    [Fact]
    public void WhenTheEpochIsZero_ThenEndingTheResumeReportsNoOwnership()
    {
        // Arrange: zero is what a connector's reconnect loop carries before its first BeginResume, and
        // BeginResume never hands it out. A cleared gate also reads as zero, so an unguarded compare
        // would let that carried zero match it and report ownership it never had.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance);

        // Act & Assert
        Assert.False(source.TryEndResumeForTest(0));

        var epoch = source.BeginResumeForTest();
        Assert.False(source.TryEndResumeForTest(0));
        Assert.True(source.TryEndResumeForTest(epoch));
    }

    [Fact]
    public void WhenAResumeEndsWhileANewerOneIsStarting_ThenItNeverClearsTheNewerResumesGate()
    {
        // Arrange: ending a resume has to test ownership and clear the gate as one step. Split into a
        // read and a write, a BeginResume landing between them has its gate cleared by the older
        // resume, which leaves outbound delivery open for the whole of the newer reconnect.
        //
        // Probabilistic by nature, so it is written to be sound rather than complete: it can miss the
        // interleaving on an unlucky scheduler, but it never fails on correct code. The read-then-write
        // version it guards against was caught in roughly 7 of 20000 iterations.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);
        using var source = new TestSubjectSource(person, context, NullLogger.Instance);

        const int iterations = 20_000;
        var olderEpoch = 0;

        // Two long-lived threads rather than a fresh pair per iteration: the interleaving comes from the
        // barrier releasing both at the same instant, not from thread creation.
        using var barrier = new Barrier(3);

        var ender = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                barrier.SignalAndWait();
                source.TryEndResumeForTest(Volatile.Read(ref olderEpoch));
                barrier.SignalAndWait();
            }
        });

        var beginner = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                barrier.SignalAndWait();
                source.BeginResumeForTest();
                barrier.SignalAndWait();
            }
        });

        ender.Start();
        beginner.Start();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Volatile.Write(ref olderEpoch, source.BeginResumeForTest());
            barrier.SignalAndWait();
            barrier.SignalAndWait();

            // Act & Assert: the newer BeginResume is the last write to the gate in this iteration, so
            // an open gate here can only mean the older resume cleared one it no longer owned.
            Assert.True(source.IsResumeGateHeldForTest,
                $"An older resume cleared the gate a concurrent BeginResume had taken (iteration {iteration}).");
        }

        ender.Join();
        beginner.Join();
    }

    [Fact]
    public async Task WhenTheGateClearsBetweenTheEnqueueAndTheSecondRead_ThenTheSelfHealSendsTheParkedWriteWithNoFurtherWrite()
    {
        // Arrange: a live pump with the resume gate held. AfterResumeGateObserved fires between
        // the retry-queue enqueue and the second gate read inside WriteChangesViaRetryQueueAsync, which
        // has no other externally observable synchronization point, so clearing the gate from inside it
        // reproduces the TOCTOU window the self-heal exists for: the write parks while the gate still
        // reads as held, and by the time the write handler re-reads it, it has already cleared, with no
        // CompleteResumeAsync ever having run for this epoch.
        var (source, person, gate, receivedWrites) = await StartPumpAsync();
        var resumeEpoch = source.BeginResumeForTest();

        var hookRan = 0;
        source.AfterResumeGateObserved = () =>
        {
            if (Interlocked.Exchange(ref hookRan, 1) == 0)
            {
                source.TryEndResumeForTest(resumeEpoch);
            }
        };

        try
        {
            // Act: the only write in this test. Nothing else ever triggers the write handler again, so
            // only the self-heal inside the parked branch can still deliver it.
            person.FirstName = "SelfHealed";

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=SelfHealed");
                }
            }, message: "The self-heal should have sent the write parked in the window between the gate read and the enqueue.");
        }
        finally
        {
            source.AfterResumeGateObserved = null;
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheReconcilesOwnRestoreIsReParkedWhileTheGateIsStillHeld_ThenTheSecondPassRescuesIt()
    {
        // Arrange: one property parked under the gate, then moved off that parked value by a
        // source-tagged write, the way an initial-state load does, so the reconcile has to restore it.
        // A write interceptor lets the restore commit for real (next(ref context) runs first, so it
        // reaches the live processor's subscription), then blocks before returning, so the gate is
        // still provably held when the depth check below observes the processor parking the restore
        // right back into the retry queue. That is the exact strand the second reconcile pass exists
        // to rescue: without it, the restore is the only change, so no later flush tick calls the
        // write handler again and the value never arrives.
        //
        // Registered before WithFullPropertyTracking() so it sits outside PropertyChangeInterceptor in
        // the write chain: that interceptor enqueues to subscriptions on its own unwind, after its own
        // call to next() returns, so a gate positioned inside it would hold up the very publish this
        // test needs to observe while still blocked.
        var context = InterceptorSubjectContext.Create();

        var restoreGate = new BlockNextCommitInterceptor();
        context.AddService<IWriteInterceptor>(restoreGate);

        context.WithRegistry().WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.ToArray())
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            var probeValue = 0;
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                person.LastName = "Probe" + probeValue++;
                lock (gate)
                {
                    return receivedWrites.Any(w => w.StartsWith("LastName=", StringComparison.Ordinal));
                }
            }, message: "Pump did not start processing changes.");

            // The probe loop above can write a value the pump has not flushed yet by the time it exits
            // (it only waits for the first one to land, not for its own last write). Draining here
            // keeps that leftover from later masking the fix under test: left parked, it would sit in
            // the retry queue alongside the write under test, the reconcile would classify it as
            // sendable because the model still holds it, and the reconcile's own send would flush the
            // whole queue, including the re-parked restore, before the gate is cleared, rescuing the
            // restore even without the second pass.
            //
            // The depth is required to hold at zero across two consecutive polls: a bare zero read can
            // land in the microseconds between a drain and the handler running, which is a false red
            // here rather than a false green, but the stronger check costs nothing.
            var previousDepth = -1;
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    var depth = source.Diagnostics.OutboundChanges.Depth;
                    var isSettled = depth == 0 && depth == previousDepth;
                    previousDepth = depth;
                    return isSettled;
                },
                message: "The processor's buffer did not settle at zero after the warmup probe.");

            // Act: park the write, move the model off it with a source-tagged commit so the reconcile
            // takes the restore branch, then arm the interceptor and start the resume without awaiting
            // it so the test can observe the park while the reconcile is still blocked inside it.
            var resumeEpoch = source.BeginResumeForTest();
            person.FirstName = "Parked";
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The write was not parked while the resume gate was set.");

            new PropertyReference(person, nameof(Person.FirstName))
                .SetValueFromSource(source, null, null, "ServerChanged");

            // Task.Run so the interceptor's synchronous block runs on a pool thread: the restore
            // commit reaches the interceptor before any real await, so calling this inline would block
            // the test's own thread there, and it is that same thread the awaits below need to reach
            // restoreGate.Release().
            restoreGate.Arm();
            var completeTask = Task.Run(() => source.CompleteResumeForTestAsync(resumeEpoch, CancellationToken.None));
            await restoreGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.OutboundRetries.Depth > 0,
                message: "The processor should have parked the restore back into the retry queue while the reconcile was still blocked inside it.");

            restoreGate.Release();
            await completeTask;

            // Assert: the second pass rescues the re-parked restore.
            await AsyncTestHelpers.WaitUntilAsync(() =>
            {
                lock (gate)
                {
                    return receivedWrites.Contains("FirstName=Parked");
                }
            }, message: "The restored value should have reached the source through the second reconcile pass.");
        }
        finally
        {
            restoreGate.Release();
            await source.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<(TestSubjectSource Source, Person Person, object Gate, List<string> ReceivedWrites)>
        StartPumpAsync()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context);

        var gate = new object();
        var receivedWrites = new List<string>();

        var source = new TestSubjectSource(person, context, NullLogger.Instance,
            bufferTime: TimeSpan.FromMilliseconds(8))
        {
            WriteChangesOverride = (changes, _) =>
            {
                lock (gate)
                {
                    foreach (var change in changes.ToArray())
                    {
                        receivedWrites.Add($"{change.Property.Name}={change.GetNewValue<object?>()}");
                    }
                }

                return ValueTask.FromResult(WriteResult.Success);
            },
        };

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source);

        await source.StartAsync(CancellationToken.None);

        var probeValue = 0;
        await AsyncTestHelpers.WaitUntilAsync(() =>
        {
            person.LastName = "Probe" + probeValue++;
            return CountWrites(gate, receivedWrites, nameof(Person.LastName)) >= 1;
        }, message: "Pump did not start processing changes.");

        return (source, person, gate, receivedWrites);
    }

    private static int CountWrites(object gate, List<string> writes, string propertyName)
    {
        lock (gate)
        {
            return writes.Count(write => write.StartsWith(propertyName + "=", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Once armed, blocks the next commit it sees after that commit has already run: <c>next</c> is
    /// called first, so the commit reaches the subject and the change subscription, and only then does
    /// the call sit until <see cref="Release"/>. Lets a test observe state that exists only while a
    /// commit has landed but the caller that made it has not yet returned.
    /// </summary>
    private sealed class BlockNextCommitInterceptor : IWriteInterceptor
    {
        private int _armed;
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the armed commit has run and the block has started.</summary>
        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                next(ref context);
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
                return;
            }

            next(ref context);
        }
    }
}
