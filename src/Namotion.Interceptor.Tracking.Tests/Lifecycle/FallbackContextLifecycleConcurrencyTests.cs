using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Concurrency coverage against the real lifecycle stack rather than test doubles, so the
/// interaction with <see cref="LifecycleInterceptor"/> and <c>ContextInheritanceHandler</c> is
/// exercised and not just the executor in isolation.
/// </summary>
public class FallbackContextLifecycleConcurrencyTests
{
    /// <summary>
    /// Hammers add and remove of the same fallback edge against the real LifecycleInterceptor and
    /// ContextInheritanceHandler, then checks that the topology and the lifecycle bookkeeping agree.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void WhenFallbackIsToggledConcurrently_ThenBookkeepingAgreesWithTopology(int threadCount)
    {
        var failures = new List<string>();

        for (var round = 0; round < 400; round++)
        {
            // Arrange
            var parentContext = InterceptorSubjectContext
                .Create()
                .WithContextInheritance()
                .WithLifecycle();

            var lifecycle = parentContext.GetServices<ILifecycleInterceptor>().OfType<LifecycleInterceptor>().Single();

            var attached = 0;
            var detached = 0;
            lifecycle.SubjectAttached += _ => Interlocked.Increment(ref attached);
            lifecycle.SubjectDetaching += _ => Interlocked.Increment(ref detached);

            var child = new Person { FirstName = "child", Mother = new Person { FirstName = "grandmother" } };
            var childContext = ((IInterceptorSubject)child).Context;
            const int expectedWithEdge = 2; // child + grandmother

            // Every thread parks on the rendezvous before any of them runs, so the round really
            // does race instead of letting the first thread finish while the last one starts. It
            // also keeps these events safe to dispose: nothing reaches them after the join below.
            using var ready = new CountdownEvent(threadCount);
            using var start = new ManualResetEventSlim(false);

            var threads = new Thread[threadCount];
            for (var index = 0; index < threadCount; index++)
            {
                var isAdder = index % 2 == 0;
                threads[index] = new Thread(() =>
                {
                    ready.Signal();
                    start.Wait();
                    for (var operation = 0; operation < 6; operation++)
                    {
                        if (isAdder)
                        {
                            childContext.AddFallbackContext(parentContext);
                        }
                        else
                        {
                            childContext.RemoveFallbackContext(parentContext);
                        }
                    }
                });

                // Background, so a genuine deadlock fails the round on the Join below rather than
                // keeping the test host alive after the run has given up on the thread.
                threads[index].IsBackground = true;
                threads[index].Start();
            }

            // Act
            ready.Wait();
            start.Set();

            var deadlocked = false;
            foreach (var thread in threads)
            {
                if (!thread.Join(TimeSpan.FromSeconds(30)))
                {
                    failures.Add($"DEADLOCK in round {round}");
                    deadlocked = true;
                }
            }

            if (deadlocked)
            {
                break;
            }

            // Assert: the balance the callbacks produced matches the topology that survived.
            var edge = !childContext.GetServices<ILifecycleInterceptor>().IsEmpty;
            var expected = edge ? expectedWithEdge : 0;
            var balance = Volatile.Read(ref attached) - Volatile.Read(ref detached);
            if (balance != expected)
            {
                failures.Add($"round {round}: edge={edge} attached={attached} detached={detached} balance={balance}");
            }

            // Assert: with every thread joined the edge is stable, so a sequential remove and add
            // must report exactly what the topology says and must settle the bookkeeping to match.
            // Without this the balance above agrees with the topology just as happily when the
            // mutators do nothing at all, which is what the hammer alone cannot tell apart.
            if (childContext.RemoveFallbackContext(parentContext) != edge)
            {
                failures.Add($"round {round}: the sequential remove disagreed with the topology, edge={edge}");
            }

            balance = Volatile.Read(ref attached) - Volatile.Read(ref detached);
            if (!childContext.GetServices<ILifecycleInterceptor>().IsEmpty || balance != 0)
            {
                failures.Add($"round {round}: after the sequential remove balance={balance}");
            }

            if (!childContext.AddFallbackContext(parentContext))
            {
                failures.Add($"round {round}: the sequential add was refused on an edge-free context");
            }

            balance = Volatile.Read(ref attached) - Volatile.Read(ref detached);
            if (balance != expectedWithEdge)
            {
                failures.Add($"round {round}: after the sequential add balance={balance}");
            }

            if (failures.Count > 8)
            {
                break;
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
