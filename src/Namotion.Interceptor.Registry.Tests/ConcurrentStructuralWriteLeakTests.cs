using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Xunit.Abstractions;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Tests that verify no registry memory leaks occur during concurrent structural
/// property writes in LifecycleInterceptor.WriteProperty.
///
/// The concurrency model: a structural write enters the lifecycle's topology gate and the writing
/// subject's attachment monitor before the chain is resolved and holds both through the terminal,
/// then validates and claims every subject the proposed value reaches, calls next to commit the
/// backing store, and reconciles the committed value against the property's baseline. Two
/// structural writes on the same graph therefore serialize against each other, and a write racing
/// an attachment transition of the writing subject orders against it instead of throwing, so
/// every write below is expected to commit or reconcile cleanly; any exception is a defect.
/// </summary>
public class ConcurrentStructuralWriteLeakTests(ITestOutputHelper output)
{
    /// <summary>
    /// Multiple threads rapidly replace the same ObjectRef property.
    /// After all threads finish and the property is set to a known final value,
    /// only the parent and final child should remain in the registry.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentObjectRefWrites_OrphanedSubjectsLeakInRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();
        var parent = new Person(context) { FirstName = "Parent" };

        const int threadCount = 4;
        const int iterationsPerThread = 2000;
        var barrier = new Barrier(threadCount);

        // Act: Multiple threads rapidly replace the Mother property with different subjects
        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            threads[threadIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var child = new Person { FirstName = $"T{Thread.CurrentThread.ManagedThreadId}_I{iteration}" };
                    parent.Mother = child;
                }
            });
            threads[threadIndex].IsBackground = true;
            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Set Mother to a known final value on the main thread
        var finalChild = new Person { FirstName = "Final" };
        parent.Mother = finalChild;

        // Assert: only parent + finalChild should remain
        var knownSubjects = registry.KnownSubjects;
        var expectedCount = 2;

        var orphanedSubjects = knownSubjects
            .Where(kvp => !ReferenceEquals(kvp.Key, parent) && !ReferenceEquals(kvp.Key, finalChild))
            .ToList();

        Assert.True(
            knownSubjects.Count == expectedCount,
            $"Expected {expectedCount} known subjects (parent + finalChild), " +
            $"but found {knownSubjects.Count}. " +
            $"There are {orphanedSubjects.Count} orphaned subject(s) leaked in the registry. " +
            $"This indicates the concurrent structural write race condition in LifecycleInterceptor.WriteProperty.");
    }

    /// <summary>
    /// Threads alternate between assigning a new subject and null to the same property.
    /// This stresses the attach/detach path asymmetry: a subject may be attached by
    /// one thread's diff but never detached because the null write's diff sees the
    /// backing store has already been overwritten by yet another thread's value.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentObjectRefAndNullWrites_OrphanedSubjectsLeakInRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();
        var parent = new Person(context) { FirstName = "Parent" };

        const int threadCount = 4;
        const int iterationsPerThread = 2000;
        var barrier = new Barrier(threadCount);

        // Act: Threads alternate between assigning a new subject and null
        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            threads[threadIndex] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    if (iteration % 2 == 0)
                    {
                        parent.Mother = new Person { FirstName = $"T{Thread.CurrentThread.ManagedThreadId}_I{iteration}" };
                    }
                    else
                    {
                        parent.Mother = null;
                    }
                }
            });
            threads[threadIndex].IsBackground = true;
            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Final state: clear the property on the main thread
        parent.Mother = null;

        // Assert: only the parent should remain
        var knownSubjects = registry.KnownSubjects;
        var expectedCount = 1;

        var orphanedSubjects = knownSubjects
            .Where(kvp => !ReferenceEquals(kvp.Key, parent))
            .ToList();

        Assert.True(
            knownSubjects.Count == expectedCount,
            $"Expected {expectedCount} known subject (parent only), " +
            $"but found {knownSubjects.Count}. " +
            $"There are {orphanedSubjects.Count} orphaned subject(s) leaked in the registry. " +
            $"This indicates the concurrent structural write race condition in LifecycleInterceptor.WriteProperty.");
    }

    /// <summary>
    /// Reproduces the parent-detach-races-with-child-write scenario.
    /// One thread repeatedly attaches and detaches a child subtree from the parent,
    /// while another thread writes to a structural property on the child.
    /// The child's property write calls next() (writing to backing store) before
    /// acquiring the lock, creating a window where the parent detach cascade can
    /// miss the newly written grandchild, leaving it orphaned in the registry.
    ///
    /// Because this is a timing-dependent race, we run multiple rounds to
    /// increase the probability of hitting the interleaving that triggers the leak.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ParentDetachDuringChildPropertyWrite_OrphanedGrandchildrenLeakInRegistry()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var grandparent = new Person(context) { FirstName = "Grandparent" };

            var barrier = new Barrier(2);
            var child = new Person { FirstName = "Child" };

            // Act:
            // Thread 1: rapidly attaches/detaches child from grandparent.Mother
            // Thread 2: rapidly writes grandchild subjects to child.Mother
            var detachThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    grandparent.Mother = child;
                    grandparent.Mother = null;
                }
            });
            detachThread.IsBackground = true;

            var childWriteThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var grandchildName = $"Grandchild_{iteration}";
                    child.Mother = new Person { FirstName = grandchildName };
                    child.Mother = null;
                }
            });
            childWriteThread.IsBackground = true;

            detachThread.Start();
            childWriteThread.Start();
            detachThread.Join();
            childWriteThread.Join();

            // Final state: ensure clean detach
            grandparent.Mother = null;
            child.Mother = null;

            // Check for leaked subjects
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, grandparent))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    var parents = kvp.Value.Parents;
                    var parentDesc = parents.Length > 0
                        ? string.Join(", ", parents.Select(p => $"{p.Property.Name}"))
                        : "no-parents";
                    output.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount} parents=[{parentDesc}]");
                    totalOrphaned++;
                }
            }
        }

        // Assert: across all rounds, no orphaned subjects should accumulate
        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates the parent-detach-during-child-write race condition in " +
            $"LifecycleInterceptor.WriteProperty where grandchild subjects are attached " +
            $"to a child that is concurrently being detached from its parent, and the " +
            $"detach cascade misses the newly written grandchild.");
    }

    /// <summary>
    /// Concurrent writes to different structural properties (Mother and Father)
    /// on the same subject. Each property has its own lastProcessed baseline,
    /// but they share the same _attachedSubjects lock. This tests whether
    /// interleaved lock acquisitions across different properties can cause
    /// reference count mismatches that prevent proper cleanup.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentWritesToDifferentStructuralProperties_OrphanedSubjectsLeakInRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();
        var parent = new Person(context) { FirstName = "Parent" };

        const int iterationsPerThread = 2000;
        var barrier = new Barrier(2);

        // Act:
        // Thread 1: rapidly writes to parent.Mother
        // Thread 2: rapidly writes to parent.Father
        var motherThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var iteration = 0; iteration < iterationsPerThread; iteration++)
            {
                parent.Mother = new Person { FirstName = $"Mother_{iteration}" };
            }
        });
        motherThread.IsBackground = true;

        var fatherThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var iteration = 0; iteration < iterationsPerThread; iteration++)
            {
                parent.Father = new Person { FirstName = $"Father_{iteration}" };
            }
        });
        fatherThread.IsBackground = true;

        motherThread.Start();
        fatherThread.Start();
        motherThread.Join();
        fatherThread.Join();

        // Set final known values
        var finalMother = new Person { FirstName = "FinalMother" };
        var finalFather = new Person { FirstName = "FinalFather" };
        parent.Mother = finalMother;
        parent.Father = finalFather;

        // Assert: parent + finalMother + finalFather = 3
        var knownSubjects = registry.KnownSubjects;
        var expectedCount = 3;

        var expectedSubjects = new HashSet<IInterceptorSubject> { parent, finalMother, finalFather };
        var orphanedSubjects = knownSubjects
            .Where(kvp => !expectedSubjects.Contains(kvp.Key))
            .ToList();

        Assert.True(
            knownSubjects.Count == expectedCount,
            $"Expected {expectedCount} known subjects (parent + finalMother + finalFather), " +
            $"but found {knownSubjects.Count}. " +
            $"There are {orphanedSubjects.Count} orphaned subject(s) leaked in the registry. " +
            $"Orphaned subjects: [{string.Join(", ", orphanedSubjects.Select(o => ((Person)o.Key).FirstName))}]");
    }

    /// <summary>
    /// Deep graph detach (3+ levels): grandparent → child → grandchild → great-grandchild.
    /// One thread detaches grandparent's child, another writes to grandchild's structural
    /// property. Verifies the recursive detach + late-attachment cleanup works at depth > 2.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void DeepGraphDetach_RecursiveDetachWithLateAttachmentAtDepthGreaterThanTwo()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var grandparent = new Person(context) { FirstName = "Grandparent" };

            var child = new Person { FirstName = "Child" };
            var grandchild = new Person { FirstName = "Grandchild" };

            // Build a 3-level deep graph: grandparent → child → grandchild
            grandchild.Mother = null;
            child.Mother = grandchild;
            grandparent.Mother = child;

            var barrier = new Barrier(2);

            // Act:
            // Thread 1: rapidly detaches/reattaches child from grandparent.Mother
            // Thread 2: rapidly writes great-grandchild subjects to grandchild.Mother
            var detachThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    grandparent.Mother = null;
                    grandparent.Mother = child;
                }
            });
            detachThread.IsBackground = true;

            var deepWriteThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var greatGrandchildName = $"GreatGrandchild_{iteration}";
                    grandchild.Mother = new Person { FirstName = greatGrandchildName };
                    grandchild.Mother = null;
                }
            });
            deepWriteThread.IsBackground = true;

            detachThread.Start();
            deepWriteThread.Start();
            detachThread.Join();
            deepWriteThread.Join();

            // Final state: ensure clean detach of the entire subtree
            grandchild.Mother = null;
            child.Mother = null;
            grandparent.Mother = null;

            // Check for leaked subjects
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, grandparent))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    output.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount}");
                    totalOrphaned++;
                }
            }
        }

        // Assert: across all rounds, no orphaned subjects should accumulate
        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates the recursive detach at depth > 2 does not correctly clean up " +
            $"great-grandchild subjects that are concurrently attached during parent detach.");
    }

    /// <summary>
    /// Two threads concurrently write to parent.Mother and parent.Children with
    /// overlapping attach/detach patterns. Verifies that reference counting remains
    /// consistent when structural properties of different types (ObjectRef vs Collection)
    /// are written concurrently on the same parent.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentObjectRefAndCollectionWrites_NoRefCountCorruption()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var parent = new Person(context) { FirstName = "Parent" };

            var barrier = new Barrier(2);

            // Act:
            // Thread 1: rapidly adds/removes the shared subject via parent.Mother
            // Thread 2: rapidly adds/removes the same subject via parent.Children
            var motherThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var shared = new Person { FirstName = $"Shared_{iteration}" };

                    // Place in both Mother and Children[0]
                    parent.Mother = shared;
                    parent.Children = [shared];

                    // Remove from Mother
                    parent.Mother = null;
                }
            });
            motherThread.IsBackground = true;

            var childrenThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var shared = new Person { FirstName = $"Shared_{iteration}" };

                    // Place in both Children and Mother
                    parent.Children = [shared];
                    parent.Mother = shared;

                    // Remove from Children
                    parent.Children = [];
                }
            });
            childrenThread.IsBackground = true;

            motherThread.Start();
            childrenThread.Start();
            motherThread.Join();
            childrenThread.Join();

            // Final state: clear both properties
            parent.Mother = null;
            parent.Children = [];

            // Check for leaked subjects
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, parent))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    output.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount}");
                    totalOrphaned++;
                }
            }
        }

        // Assert: across all rounds, no orphaned subjects should accumulate
        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates a reference count mismatch when a subject is referenced from " +
            $"multiple properties and concurrently removed from both.");
    }

    /// <summary>
    /// Concurrent collection add/remove: one thread adds items to a collection,
    /// another replaces the collection (removing items). Stresses the collection diff path
    /// where old and new arrays must be compared to determine attach/detach operations.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentCollectionAddAndReplace_CollectionDiffPathHandlesConcurrentMutations()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var parent = new Person(context) { FirstName = "Parent" };

            var barrier = new Barrier(2);

            // Act:
            // Thread 1: grows the collection by adding items
            // Thread 2: replaces the collection entirely (removing all items)
            var addThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var current = parent.Children;
                    var newChild = new Person { FirstName = $"Add_{iteration}" };
                    parent.Children = [..current, newChild];
                }
            });
            addThread.IsBackground = true;

            var replaceThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    var replacement = new Person { FirstName = $"Replace_{iteration}" };
                    parent.Children = [replacement];
                }
            });
            replaceThread.IsBackground = true;

            addThread.Start();
            replaceThread.Start();
            addThread.Join();
            replaceThread.Join();

            // Final state: clear the collection
            parent.Children = [];

            // Check for leaked subjects
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, parent))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    output.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount}");
                    totalOrphaned++;
                }
            }
        }

        // Assert: across all rounds, no orphaned subjects should accumulate
        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates the collection diff path does not correctly handle concurrent " +
            $"add and replace operations, leaving orphaned subjects in the registry.");
    }

    /// <summary>
    /// Rapid attach-detach-reattach cycle: one thread repeatedly does
    /// parent.Mother = child; parent.Mother = null using the same child instance.
    /// This stresses the isFirstAttach / isLastDetach tracking for the same subject
    /// being repeatedly attached and detached. A second thread does the same with
    /// parent.Father to add contention on the shared lock.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void RapidAttachDetachReattachCycle_SameSubjectReusedDoesNotLeakOrCorruptRefCount()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var parent = new Person(context) { FirstName = "Parent" };

            // Reuse the same child instances across all iterations
            var reusedMother = new Person { FirstName = "ReusedMother" };
            var reusedFather = new Person { FirstName = "ReusedFather" };

            var barrier = new Barrier(2);

            // Act:
            // Thread 1: rapidly attaches/detaches reusedMother via parent.Mother
            // Thread 2: rapidly attaches/detaches reusedFather via parent.Father
            var motherThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    parent.Mother = reusedMother;
                    parent.Mother = null;
                }
            });
            motherThread.IsBackground = true;

            var fatherThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var iteration = 0; iteration < iterationsPerThread; iteration++)
                {
                    parent.Father = reusedFather;
                    parent.Father = null;
                }
            });
            fatherThread.IsBackground = true;

            motherThread.Start();
            fatherThread.Start();
            motherThread.Join();
            fatherThread.Join();

            // Final state: ensure both are detached
            parent.Mother = null;
            parent.Father = null;

            // Check for leaked subjects
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, parent))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    output.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount}");
                    totalOrphaned++;
                }
            }
        }

        // Assert: across all rounds, no orphaned subjects should accumulate
        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates isFirstAttach/isLastDetach tracking is incorrect when the same " +
            $"subject instance is rapidly attached and detached, causing reference count " +
            $"corruption that leaves subjects orphaned in the registry.");
    }

    /// <summary>
    /// Verifies that all subjects reachable from the root are registered in the registry.
    /// One thread rapidly replaces a dictionary (adding/removing items), while another
    /// thread rapidly detaches/reattaches the dictionary's parent.
    /// Previously, the parentStillAttached guard could prevent child registration when
    /// the parent was concurrently being detached, leaving subjects in the graph but
    /// not in the registry. This test verifies the invariant: reachable → registered.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentDictWriteDuringParentDetach_AllReachableSubjectsAreRegistered()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalUnregistered = 0;

        for (var round = 0; round < rounds; round++)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var grandparent = new Person(context) { FirstName = "Grandparent" };
            var parent = new Person { FirstName = "Parent" };

            grandparent.Mother = parent;

            var barrier = new Barrier(2);

            // Thread 1: rapidly replaces parent's Children (dictionary-like structural property)
            var dictWriteThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    var child = new Person { FirstName = $"Child_{i}" };
                    parent.Mother = child;
                }
            });
            dictWriteThread.IsBackground = true;

            // Thread 2: rapidly detaches/reattaches parent from grandparent
            var detachThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    grandparent.Mother = null;
                    grandparent.Mother = parent;
                }
            });
            detachThread.IsBackground = true;

            dictWriteThread.Start();
            detachThread.Start();
            dictWriteThread.Join();
            detachThread.Join();

            // Set final known state
            var finalChild = new Person { FirstName = "FinalChild" };
            parent.Mother = finalChild;
            grandparent.Mother = parent;

            // Verify: all reachable subjects are registered
            var reachable = new HashSet<IInterceptorSubject> { grandparent, parent, finalChild };
            foreach (var subject in reachable)
            {
                var registered = registry.TryGetRegisteredSubject(subject);
                if (registered is null)
                {
                    output.WriteLine($"  Round {round}: subject '{((Person)subject).FirstName}' is reachable but NOT registered");
                    totalUnregistered++;
                }
            }

            // Also check no leaks (registered but not reachable)
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!reachable.Contains(kvp.Key))
                {
                    output.WriteLine($"  Round {round}: orphan '{((Person)kvp.Key).FirstName}' refCount={kvp.Value.ReferenceCount}");
                    totalUnregistered++; // count leaks too
                }
            }

            // Cleanup
            parent.Mother = null;
            grandparent.Mother = null;
        }

        Assert.True(
            totalUnregistered == 0,
            $"Detected {totalUnregistered} total inconsistencies across {rounds} rounds. " +
            $"This indicates either subjects reachable from the graph are not registered " +
            $"(parentStillAttached guard preventing registration) or subjects are leaked " +
            $"in the registry (dangling _lastProcessedValues entries).");
    }

    /// <summary>
    /// Reproduces the "no-parents" registry leak: a child attaches to a parent that is
    /// being concurrently detached. The registry's HandleLifecycleChange re-registers
    /// the parent (via RegisterSubject for the parent side-effect) after the parent was
    /// already removed from _knownSubjects. The parent ends up in _knownSubjects with
    /// refCount=0 and no parent references — a permanent leak.
    ///
    /// Thread A: rapidly attaches/detaches a child from grandparent (which detaches
    ///           the child and triggers _knownSubjects.Remove for the child)
    /// Thread B: rapidly attaches grandchildren to the child's structural property
    ///           (which triggers HandleLifecycleChange with IsPropertyReferenceAdded,
    ///           causing RegisterSubject(child) if child was just removed)
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ParentReRegisteredAfterDetach_NoParentsLeakInRegistry()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking()
                .WithRegistry();

            var registry = context.GetService<ISubjectRegistry>();
            var grandparent = new Person(context) { FirstName = "Grandparent" };
            var child = new Person { FirstName = "Child" };

            var barrier = new Barrier(3);

            // Thread A: rapidly attaches/detaches child from grandparent
            var detachThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    grandparent.Mother = child;
                    grandparent.Mother = null;
                }
            });
            detachThread.IsBackground = true;

            // Thread B: rapidly attaches grandchildren to child.Mother
            var attachThread1 = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    var grandchildName = $"GrandchildM_{i}";
                    child.Mother = new Person { FirstName = grandchildName };
                    child.Mother = null;
                }
            });
            attachThread1.IsBackground = true;

            // Thread C: rapidly attaches grandchildren to child.Father
            // More concurrent writers increase the chance of the race
            var attachThread2 = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    var grandchildName = $"GrandchildF_{i}";
                    child.Father = new Person { FirstName = grandchildName };
                    child.Father = null;
                }
            });
            attachThread2.IsBackground = true;

            detachThread.Start();
            attachThread1.Start();
            attachThread2.Start();
            detachThread.Join();
            attachThread1.Join();
            attachThread2.Join();

            // Clean up
            grandparent.Mother = null;
            child.Mother = null;
            child.Father = null;

            // Check: only grandparent should remain
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, grandparent))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    var parents = kvp.Value.Parents;
                    var parentDesc = parents.Length > 0 ? "has-parents" : "no-parents";
                    output.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount} {parentDesc}");
                    totalOrphaned++;
                }
            }
        }

        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates the registry re-registers a parent subject via RegisterSubject " +
            $"after it was concurrently detached, leaving it in _knownSubjects with " +
            $"refCount=0 and no parent references.");
    }
}
