using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests that lifecycle tracking remains consistent when multiple threads
/// concurrently write to the same structural property on a subject.
/// Verifies the fix for a race condition in LifecycleInterceptor.WriteProperty
/// where stale currentValue captures caused orphaned subjects in _attachedSubjects.
/// </summary>
public class ConcurrentWriteLifecycleTests
{
    [Fact]
    public void WhenConcurrentCollectionWrites_ThenOnlyFinalSubjectsAreTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var initialChildren = Enumerable.Range(0, 5)
            .Select(i => new Person { FirstName = $"Initial{i}" })
            .ToArray();
        root.Children = initialChildren;

        // Verify initial state
        foreach (var child in initialChildren)
        {
            Assert.Equal(1, child.GetReferenceCount());
        }

        // Act: Two threads concurrently set Children to different arrays.
        // Use a barrier to maximize the chance of overlap.
        var barrier = new Barrier(2);
        const int iterations = 200;

        var allCreatedByA = new List<Person>();
        var allCreatedByB = new List<Person>();

        var threadA = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var children = new[]
                {
                    new Person { FirstName = $"A{i}_0" },
                    new Person { FirstName = $"A{i}_1" },
                    new Person { FirstName = $"A{i}_2" }
                };

                allCreatedByA.AddRange(children);
                if (i == 0) barrier.SignalAndWait();
                root.Children = children;
            }
        });

        var threadB = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var children = new[]
                {
                    new Person { FirstName = $"B{i}_0" },
                    new Person { FirstName = $"B{i}_1" }
                };

                allCreatedByB.AddRange(children);
                if (i == 0) barrier.SignalAndWait();
                root.Children = children;
            }
        });

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Assert: All subjects currently in Children must have ref count = 1.
        var winningSet = new HashSet<Person>(root.Children);
        foreach (var child in winningSet)
        {
            Assert.Equal(1, child.GetReferenceCount());
        }

        // Every subject NOT in the winning set (initial + all created by both threads)
        // must have ref count 0 — no orphaned subjects.
        foreach (var child in initialChildren.Concat(allCreatedByA).Concat(allCreatedByB))
        {
            if (!winningSet.Contains(child))
            {
                Assert.Equal(0, child.GetReferenceCount());
            }
        }

        // Verify registry only contains root + winning children (no orphaned subjects)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(1 + winningSet.Count, registry.KnownSubjects.Count);
    }

    [Fact]
    public void WhenConcurrentObjectRefWrites_ThenOnlyFinalSubjectIsTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var initialFather = new Person { FirstName = "InitialFather" };
        root.Father = initialFather;
        Assert.Equal(1, initialFather.GetReferenceCount());

        // Act: Two threads concurrently set Father to different subjects.
        var barrier = new Barrier(2);
        const int iterations = 200;

        var allCreatedByA = new List<Person>();
        var allCreatedByB = new List<Person>();

        var threadA = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var father = new Person { FirstName = $"FatherA{i}" };
                allCreatedByA.Add(father);
                if (i == 0) barrier.SignalAndWait();
                root.Father = father;
            }
        });

        var threadB = new Thread(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var father = new Person { FirstName = $"FatherB{i}" };
                allCreatedByB.Add(father);
                if (i == 0) barrier.SignalAndWait();
                root.Father = father;
            }
        });

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        // Assert: The final Father must have ref count = 1.
        var finalFather = root.Father;
        Assert.NotNull(finalFather);
        Assert.Equal(1, finalFather.GetReferenceCount());

        // Every subject NOT currently referenced (initial + all created by both threads)
        // must have ref count 0 — no orphaned subjects.
        foreach (var father in allCreatedByA.Concat(allCreatedByB).Append(initialFather))
        {
            if (!ReferenceEquals(father, finalFather))
            {
                Assert.Equal(0, father.GetReferenceCount());
            }
        }

        // Verify registry only contains root + final father (no orphaned subjects)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(2, registry.KnownSubjects.Count);
    }

    [Fact]
    public void WhenManyThreadsSetCollectionRepeatedly_ThenNoOrphanedSubjectsRemain()
    {
        // Stress test: multiple threads, many iterations, verify no orphaned subjects.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };

        const int threadCount = 4;
        const int iterations = 100;
        var barrier = new Barrier(threadCount);
        var allCreatedSubjects = new List<Person>[threadCount];
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            allCreatedSubjects[threadIndex] = [];

            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterations; i++)
                {
                    var children = new[]
                    {
                        new Person { FirstName = $"T{threadIndex}_I{i}_0" },
                        new Person { FirstName = $"T{threadIndex}_I{i}_1" }
                    };

                    lock (allCreatedSubjects[threadIndex])
                    {
                        allCreatedSubjects[threadIndex].AddRange(children);
                    }

                    root.Children = children;
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        // Assert: Only subjects currently in Children should have ref count > 0.
        var currentChildren = new HashSet<Person>(root.Children);

        foreach (var subjectList in allCreatedSubjects)
        {
            foreach (var subject in subjectList)
            {
                if (currentChildren.Contains(subject))
                {
                    Assert.Equal(1, subject.GetReferenceCount());
                }
                else
                {
                    Assert.Equal(0, subject.GetReferenceCount());
                }
            }
        }

        // Verify registry only contains root + current children (no orphaned subjects)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(1 + currentChildren.Count, registry.KnownSubjects.Count);
    }
}
