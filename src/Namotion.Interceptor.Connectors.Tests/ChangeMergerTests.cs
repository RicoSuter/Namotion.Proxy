using System.Buffers;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeMergerTests
{
    [Fact]
    public void WhenTwoChangesToOnePropertyHaveNoRevision_ThenTheyCollapseByArrivalPosition()
    {
        // Arrange
        using var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "Value1", "Value2", revision: 0),
            CreateChange(property, "Value2", "Value3", revision: 0)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert - the batch collapses to one change keeping the oldest old value and the newest new value
        var change = Assert.Single(merged);
        Assert.Equal("Value1", change.GetOldValue<string>());
        Assert.Equal("Value3", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenTheNewerCommitArrivesFirst_ThenTheSurvivorTakesItsNewValue()
    {
        // Arrange - the enqueue order is inverted against the commit order, which is the race the
        // revision fixes: enqueuing happens after the commit and outside the subject lock.
        using var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        var newerSource = new object();
        var newerOrigin = ChangeOrigin.FromSource(newerSource);
        var newerTimestamp = DateTimeOffset.UtcNow;
        var olderTimestamp = newerTimestamp.AddSeconds(-1);

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "NewerOld", "NewerNew", revision: 20, newerOrigin, newerTimestamp),
            CreateChange(property, "OlderOld", "OlderNew", revision: 10, ChangeOrigin.Local, olderTimestamp)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert
        var change = Assert.Single(merged);
        Assert.Equal("OlderOld", change.GetOldValue<string>());
        Assert.Equal("NewerNew", change.GetNewValue<string>());
        Assert.Equal(20, change.Revision);

        // The survivor's metadata follows the highest revision, not the last arrival, which matters for a
        // consumer that keys off Origin.Source (echo suppression) or off the timestamp.
        Assert.Equal(ChangeOriginKind.FromSource, change.Origin.Kind);
        Assert.Same(newerSource, change.Origin.Source);
        Assert.Equal(newerTimestamp, change.ChangedTimestamp);
    }

    [Fact]
    public void WhenThreeCommitsArriveOutOfOrder_ThenTheSurvivorSpansTheLowestAndHighestRevision()
    {
        // Arrange
        using var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "Old14", "New14", revision: 14),
            CreateChange(property, "Old21", "New21", revision: 21),
            CreateChange(property, "Old7", "New7", revision: 7)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert - the baseline comes from the lowest revision, the current state from the highest
        var change = Assert.Single(merged);
        Assert.Equal("Old7", change.GetOldValue<string>());
        Assert.Equal("New21", change.GetNewValue<string>());
        Assert.Equal(21, change.Revision);
    }

    [Fact]
    public void WhenARevisionZeroChangeIsNotTheLastArrival_ThenTheSurvivorKeepsTheLastArrivalNewValue()
    {
        // Arrange - a revision 0 change makes the whole property fall back to arrival position, even
        // though a later arrival carries the highest revision of the batch.
        using var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0),
            CreateChange(property, "MiddleOld", "MiddleNew", revision: 999),
            CreateChange(property, "LastOld", "LastNew", revision: 50)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert - the first arrival supplies the old value, the last arrival the new value
        var change = Assert.Single(merged);
        Assert.Equal("ZeroOld", change.GetOldValue<string>());
        Assert.Equal("LastNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenARevisionZeroChangeSitsBetweenHigherRevisions_ThenTheSurvivorKeepsTheLastArrivalNewValue()
    {
        // Arrange - high revisions arrive both before and after the revision 0 change, so neither of
        // them may be promoted into the survivor once the fallback applies.
        using var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "EarlyHighOld", "EarlyHighNew", revision: 800),
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0),
            CreateChange(property, "LateHighOld", "LateHighNew", revision: 900),
            CreateChange(property, "LastOld", "LastNew", revision: 10)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert - the first arrival supplies the old value, the last arrival the new value
        var change = Assert.Single(merged);
        Assert.Equal("EarlyHighOld", change.GetOldValue<string>());
        Assert.Equal("LastNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenTheArrivalOrderFallbackApplies_ThenTheSurvivorIsNotDroppedAsSuperseded()
    {
        // Arrange: once a revision 0 change forces the fallback, the survivor carries the last
        // arrival's revision rather than the batch's highest. If that revision is below the property's
        // marker, the supersession pass drops the survivor, and the higher-revision change whose value
        // it carries was merged away in this same batch, so nothing re-delivers it.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        subject.FirstName = "First";
        subject.FirstName = "Settled";
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var marker, out _));
        Assert.True(marker > 1, "the marker needs room below it");

        using var merger = new ChangeMerger();

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "SettledOld", "Settled", marker),
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0),
            CreateChange(property, "LastOld", "LastNew", revision: marker - 1)
        ];

        // Act
        var merged = merger.Merge(changes, ChangeDeliveryRule.SourceValuesMayBeStale).ToArray();

        // Assert: a survivor built by arrival position orders against nothing, exactly as a lone
        // revision 0 change would, so it must be delivered rather than ranked against the marker.
        var change = Assert.Single(merged);
        Assert.Equal("LastNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenTheRevisionZeroChangeArrivesLast_ThenTheBatchCollapsesByArrivalPosition()
    {
        // Arrange - the fallback also holds when the unordered change closes the batch
        using var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(property, "FirstOld", "FirstNew", revision: 30),
            CreateChange(property, "MiddleOld", "MiddleNew", revision: 10),
            CreateChange(property, "ZeroOld", "ZeroNew", revision: 0)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert - the first arrival supplies the old value, the last arrival the new value
        var change = Assert.Single(merged);
        Assert.Equal("FirstOld", change.GetOldValue<string>());
        Assert.Equal("ZeroNew", change.GetNewValue<string>());
    }

    [Fact]
    public void WhenChangesBelongToDifferentSubjects_ThenEachPropertyIsCollapsedIndependently()
    {
        // Arrange - revisions of different subjects are not comparable, so the two properties must be
        // collapsed against their own revisions only.
        using var merger = new ChangeMerger();

        var firstSubject = new Person();
        var secondSubject = new Person();

        var firstProperty = new PropertyReference(firstSubject, nameof(Person.FirstName));
        var secondProperty = new PropertyReference(secondSubject, nameof(Person.FirstName));

        SubjectPropertyChange[] changes =
        [
            CreateChange(firstProperty, "FirstOld12", "FirstNew12", revision: 12),
            CreateChange(secondProperty, "SecondOld3", "SecondNew3", revision: 3),
            CreateChange(firstProperty, "FirstOld5", "FirstNew5", revision: 5),
            CreateChange(secondProperty, "SecondOld8", "SecondNew8", revision: 8)
        ];

        // Act
        var merged = merger.Merge(changes).ToArray();

        // Assert - both survive, in the arrival order of their last occurrence
        Assert.Equal(2, merged.Length);

        Assert.Equal(firstProperty, merged[0].Property);
        Assert.Equal("FirstOld5", merged[0].GetOldValue<string>());
        Assert.Equal("FirstNew12", merged[0].GetNewValue<string>());

        Assert.Equal(secondProperty, merged[1].Property);
        Assert.Equal("SecondOld3", merged[1].GetOldValue<string>());
        Assert.Equal("SecondNew8", merged[1].GetNewValue<string>());
    }

    // Matches the merger's minimum rental, so the array left in the pool lands in the bucket the
    // merger rents its first buffer from.
    private const int PooledBatchSize = 256;
    private const int LargeBatchSize = 64;
    private const int SmallBatchSize = 2;

    [Fact]
    public void WhenBatchesHaveBeenReleased_ThenNeitherThePooledNorTheMergedChangesStayReferenced()
    {
        // Arrange - two sets of stale changes have to be gone: the ones the array pool handed over with
        // the buffer, which only the clear on rent removes because no flush ever writes those slots, and
        // the ones a released batch wrote, which Reset has to clear over the batch's full length rather
        // than over the length of whatever batch comes after it.
        var (merger, pooledSubjects, largeBatchSubjects) = RunLargeBatchThenSmallBatch();

        // Act
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.All(pooledSubjects, subject => Assert.False(subject.IsAlive,
            "The merger must release what the array pool left in the buffer it rented."));
        Assert.All(largeBatchSubjects, subject => Assert.False(subject.IsAlive,
            "Resetting a batch must clear every slot that batch filled."));

        merger.Dispose();
    }

    // Not inlined, so the batches this builds are dead once it returns and the merger's buffer is
    // the only thing that could still root their subjects.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ChangeMerger Merger, WeakReference[] PooledSubjects, WeakReference[] LargeBatchSubjects)
        RunLargeBatchThenSmallBatch()
    {
        var pooledSubjects = LeaveChangesInThePool();

        var merger = new ChangeMerger();

        var largeBatch = CreateBatch(LargeBatchSize);
        var largeBatchSubjects = ToWeakReferences(largeBatch);
        merger.Merge(largeBatch);
        merger.Reset();

        merger.Merge(CreateBatch(SmallBatchSize));
        merger.Reset();

        return (merger, pooledSubjects, largeBatchSubjects);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LeaveChangesInThePool()
    {
        // ArrayPool does not clear what it takes back, so the next renter sees these changes.
        var buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(PooledBatchSize);
        var subjects = new WeakReference[buffer.Length];
        for (var index = 0; index < buffer.Length; index++)
        {
            var subject = new Person();
            buffer[index] = CreateChange(
                new PropertyReference(subject, nameof(Person.FirstName)), "Old", "New", revision: index + 1);
            subjects[index] = new WeakReference(subject);
        }

        ArrayPool<SubjectPropertyChange>.Shared.Return(buffer);
        return subjects;
    }

    [Fact]
    public void WhenValueTypedSurvivorsAreCheckedForSupersession_ThenNothingIsAllocated()
    {
        // Arrange: generated int properties, written through the terminal so each carries a committed
        // revision, and changes stamped with that same revision so every survivor is kept and every
        // check runs to the end rather than short-circuiting. This is the flush path every buffered
        // connector runs, and it is meant to be allocation free.
        using var merger = new ChangeMerger();

        var changes = new SubjectPropertyChange[16];
        for (var index = 0; index < changes.Length; index++)
        {
            var subject = new DerivedCollectionDevice(InterceptorSubjectContext.Create()) { First = index };
            var property = new PropertyReference(subject, nameof(DerivedCollectionDevice.First));

            Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var revision, out _),
                "The write did not reach a terminal, so this measures the wrong path.");
            Assert.NotEqual(0, revision);

            changes[index] = SubjectPropertyChange.Create(
                property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, 0, index, revision);
        }

        // Warm up: the JIT, the property index capacity, the pooled buffer and the sticky written-out
        // mark are all one-time costs that would otherwise land inside the measurement.
        for (var warmup = 0; warmup < AllocationWarmupRounds; warmup++)
        {
            merger.Merge(changes, ChangeDeliveryRule.SourceValuesMayBeStale);
            merger.Reset();
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var merged = merger.Merge(changes, ChangeDeliveryRule.SourceValuesMayBeStale);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.Equal(changes.Length, merged.Length);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenABatchRepeatsFewProperties_ThenTheIndexIsSizedByPropertiesNotByChanges()
    {
        // Arrange: the index is keyed by property, so 50,000 changes to 4 of them need 4 entries.
        // Sizing it by the batch retained ~11 MB for the merger's lifetime.
        using var merger = new ChangeMerger();

        // Act: asserted before Reset, because the trim there would mask a pre-size that is still
        // sized by the batch.
        merger.Merge(CreateWideBatch(changeCount: 50_000, distinctProperties: 4));
        var capacityDuringBatch = GetPropertyIndexCapacity(merger);
        merger.Reset();

        // Assert
        Assert.True(capacityDuringBatch <= 293,
            $"index capacity was {capacityDuringBatch} during the batch, so it is still sized by change count");
        Assert.True(GetPropertyIndexCapacity(merger) <= 293,
            $"index capacity was {GetPropertyIndexCapacity(merger)}, so it is still sized by change count");
    }

    [Fact]
    public void WhenAWideBatchIsFollowedByNarrowOnes_ThenTheIndexCapacityIsReleased()
    {
        // Arrange
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
        merger.Reset();
        Assert.True(GetPropertyIndexCapacity(merger) > PropertyIndexMaximum);

        // Act: the trim waits for the narrow condition to persist, so one batch is not enough.
        for (var round = 0; round < 4; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 2, distinctProperties: 2));
            merger.Reset();
        }

        // Assert: both high-water marks are released, and by the same evidence.
        Assert.True(GetPropertyIndexCapacity(merger) <= 293,
            $"index capacity stayed at {GetPropertyIndexCapacity(merger)} after sustained narrow batches");
        Assert.True(GetBufferLength(merger) < 4096,
            $"buffer length stayed at {GetBufferLength(merger)} after sustained narrow batches");
    }

    [Fact]
    public void WhenNarrowAndWideBatchesAlternate_ThenTheIndexCapacityIsNotChurned()
    {
        // Arrange: flush widths vary constantly under load. Trimming on the first narrow batch makes the
        // next wide one regrow the index, which measured as +17% allocation on the connector delivery
        // benchmark, so the trim must not fire on routine variation.
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
        merger.Reset();
        var settledCapacity = GetPropertyIndexCapacity(merger);

        // Act & Assert: the capacity has to hold across each narrow batch, not merely end up back where it
        // started. Asserting only the final capacity passes even when the trim fires on every narrow batch,
        // because the wide batch that follows regrows the index to the same prime.
        for (var round = 0; round < 5; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 2, distinctProperties: 2));
            merger.Reset();

            Assert.Equal(settledCapacity, GetPropertyIndexCapacity(merger));

            merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
            merger.Reset();
        }

        Assert.Equal(settledCapacity, GetPropertyIndexCapacity(merger));
    }

    /// <summary>
    /// The shape where the buffer is the only mark that can drive a release. Many changes over few
    /// properties leaves the index at its floor, so its side of the guard is permanently false and the
    /// counter advances on the buffer term alone. The other two buffer tests seed from a wide-distinct
    /// batch, where the index drives the counter and that term is never exercised: without this, dropping
    /// it from the guard leaves the whole suite green while the rental is retained forever on exactly the
    /// workload that retains the most.
    /// </summary>
    [Fact]
    public void WhenABurstCollapsesToFewProperties_ThenTheBufferAloneDrivesTheRelease()
    {
        // Arrange
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 8));
        merger.Reset();

        var burstLength = GetBufferLength(merger);
        Assert.True(burstLength >= 4096, $"the burst should have grown the buffer, it is {burstLength}");
        Assert.True(GetPropertyIndexCapacity(merger) < PropertyIndexMaximum,
            "the index must stay at its floor, or it rather than the buffer is driving the counter");

        // Act: three more, so four narrow resets counting the burst's own.
        for (var round = 0; round < 3; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 2, distinctProperties: 2));
            merger.Reset();
        }

        // Assert
        Assert.True(GetBufferLength(merger) < burstLength,
            $"buffer stayed at {GetBufferLength(merger)}, so the buffer term never advanced the counter");
    }

    /// <summary>
    /// The buffer shrink used to fire on the first qualifying reset while the index trim waited for the
    /// threshold, so one narrow batch after a burst returned the rental and the next burst re-rented and
    /// re-cleared the whole pool bucket. Both high-water marks now wait for the same evidence.
    /// </summary>
    [Fact]
    public void WhenNarrowBatchesStopOneShortOfTheThreshold_ThenTheBufferIsKept()
    {
        // Arrange
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
        merger.Reset();
        var settledLength = GetBufferLength(merger);
        Assert.True(settledLength >= 4096, $"the wide batch should have grown the buffer, it is {settledLength}");

        // Act
        for (var round = 0; round < 3; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 2, distinctProperties: 2));
            merger.Reset();
        }

        // Assert
        Assert.Equal(settledLength, GetBufferLength(merger));
    }

    [Fact]
    public void WhenNarrowBatchesStopOneShortOfTheThreshold_ThenTheIndexCapacityIsKept()
    {
        // Arrange: one fewer consecutive narrow batch than the trim requires. Together with
        // WhenAWideBatchIsFollowedByNarrowOnes, which trims at four, this brackets the threshold exactly:
        // lower it and this test trims early, raise it and that one never trims.
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
        merger.Reset();
        var settledCapacity = GetPropertyIndexCapacity(merger);
        Assert.True(settledCapacity > PropertyIndexMaximum);

        // Act
        for (var round = 0; round < 3; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 2, distinctProperties: 2));
            merger.Reset();
        }

        // Assert
        Assert.Equal(settledCapacity, GetPropertyIndexCapacity(merger));
    }

    [Fact]
    public void WhenWideBatchesKeepArriving_ThenTheIndexCapacityIsNotChurned()
    {
        // Arrange: a large model whose properties all change every flush must keep its capacity, or the
        // trim reintroduces the per-flush allocation it exists to remove.
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
        merger.Reset();
        var settledCapacity = GetPropertyIndexCapacity(merger);

        // Act
        for (var round = 0; round < 5; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 4096, distinctProperties: 4096));
            merger.Reset();
        }

        // Assert
        Assert.Equal(settledCapacity, GetPropertyIndexCapacity(merger));
    }

    [Fact]
    public void WhenAShrinkFollowsABatchWiderThanTheFloor_ThenItDoesNotThrow()
    {
        // Arrange: TrimExcess throws when the requested capacity is below Count, and the shrink guard
        // does not bound Count by the floor. A batch of 300 distinct properties is both above the 256
        // floor and below a quarter of the capacity a 6000-property batch leaves behind, so this is the
        // shape that throws if the trim runs before the clear. It would escape the flush task's finally
        // and end the periodic flush loop for good.
        using var merger = new ChangeMerger();

        merger.Merge(CreateWideBatch(changeCount: 6000, distinctProperties: 6000));
        merger.Reset();

        var wideCapacity = GetPropertyIndexCapacity(merger);

        // Act: repeated because the trim only fires after enough consecutive narrow batches. One round
        // leaves the capacity untouched, so a single pass would never reach the code this pins.
        for (var round = 0; round < NarrowBatchesBeforeTrim; round++)
        {
            merger.Merge(CreateWideBatch(changeCount: 300, distinctProperties: 300));
            merger.Reset();
        }

        // Assert: the trim ran, which is what proves the clear happened first. Without that ordering
        // the Reset above throws, and asserting only "does not throw" would pass while never trimming.
        Assert.True(GetPropertyIndexCapacity(merger) < wideCapacity,
            "the trim should have fired, otherwise this test never reaches the ordering it pins");
    }

    [Fact]
    public void WhenManyChangesCollapseToFewProperties_ThenNothingIsAllocated()
    {
        // Arrange: the inverted shape of the survivor-check allocation test, which uses as many
        // properties as changes and so cannot see an index sized by the batch.
        using var merger = new ChangeMerger();
        var changes = CreateWideBatch(changeCount: 4096, distinctProperties: 8);

        for (var warmup = 0; warmup < AllocationWarmupRounds; warmup++)
        {
            merger.Merge(changes);
            merger.Reset();
        }

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        merger.Merge(changes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        merger.Reset();

        // Assert
        Assert.Equal(0, allocated);
    }

    // Deep enough that the measured call runs fully promoted code. Tier-0 has runtime work billed to
    // the calling thread, and a five round warmup left the single measured call still in tier-0, which
    // is what made these tests report a few thousand stray bytes under CI load. The count is kept
    // congruent to the old one modulo the buffer's four batch trim cycle, so the measured call still
    // lands off a re-rent exactly as it did at five rounds.
    private const int AllocationWarmupRounds = 101;

    private const int PropertyIndexMaximum = 1024;

    // Mirrors ChangeMerger's hysteresis count. A shrink test that runs fewer rounds than this never
    // reaches the trim, so it pins nothing.
    private const int NarrowBatchesBeforeTrim = 4;

    private static SubjectPropertyChange[] CreateWideBatch(int changeCount, int distinctProperties)
    {
        var properties = new PropertyReference[distinctProperties];
        for (var index = 0; index < distinctProperties; index++)
        {
            properties[index] = new PropertyReference(new Person(), nameof(Person.FirstName));
        }

        var changes = new SubjectPropertyChange[changeCount];
        for (var index = 0; index < changeCount; index++)
        {
            changes[index] = CreateChange(
                properties[index % distinctProperties], "Old", "New", revision: index + 1);
        }

        return changes;
    }

    private static int GetBufferLength(ChangeMerger merger)
    {
        var field = typeof(ChangeMerger)
            .GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.True(field is not null, "_buffer was renamed, this test needs updating.");
        return ((SubjectPropertyChange[])field!.GetValue(merger)!).Length;
    }

    private static int GetPropertyIndexCapacity(ChangeMerger merger)
    {
        var field = typeof(ChangeMerger)
            .GetField("_propertyIndices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.True(field is not null, "_propertyIndices was renamed, this test needs updating.");
        return ((Dictionary<PropertyReference, (int, int, long, long)>)field!.GetValue(merger)!).Capacity;
    }

    private static SubjectPropertyChange[] CreateBatch(int size)
    {
        // One subject per change, so every change is the only root of a subject of its own.
        var changes = new SubjectPropertyChange[size];
        for (var index = 0; index < size; index++)
        {
            changes[index] = CreateChange(
                new PropertyReference(new Person(), nameof(Person.FirstName)), "Old", "New", revision: index + 1);
        }

        return changes;
    }

    private static WeakReference[] ToWeakReferences(SubjectPropertyChange[] changes)
    {
        var subjects = new WeakReference[changes.Length];
        for (var index = 0; index < changes.Length; index++)
        {
            subjects[index] = new WeakReference(changes[index].Property.Subject);
        }

        return subjects;
    }

    /// <summary>
    /// The disposed merger is reachable: <see cref="ChangeQueueProcessor"/> releases the buffer
    /// once its Dispose wins the flush gate, and the periodic flush task can outlive that and tick
    /// again on whatever was enqueued in between. Throwing there escapes the flush, kills the periodic
    /// loop for good and leaves the queue growing unbounded, so the guards are load-bearing rather
    /// than defensive tidiness, and nothing else exercises them.
    /// </summary>
    [Fact]
    public void WhenDisposed_ThenMergeIsEmptyAndResetIsANoOp()
    {
        // Arrange
        var merger = new ChangeMerger();

        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        SubjectPropertyChange[] changes = [CreateChange(property, "Value1", "Value2", revision: 1)];

        Assert.Single(merger.Merge(changes).ToArray());
        merger.Reset();

        // Act
        merger.Dispose();

        // Assert: a flush that reaches a released buffer skips the write handler instead of throwing.
        Assert.True(merger.Merge(changes).IsEmpty);

        // And the calls that would follow it in that same flush stay no-ops.
        merger.Reset();
        merger.Dispose();
    }

    [Fact]
    public void WhenTheModelHasMovedPastAChange_ThenTheSurvivorIsSuppressed()
    {
        // Arrange: collapsing a batch cannot see across flushes, so a change enqueued late enough to
        // land in the next batch would otherwise overwrite the source with an older commit's value.
        // The property's own commit revision settles it: FirstName has moved on, LastName has not.
        using var merger = new ChangeMerger();

        var subject = new Person(InterceptorSubjectContext.Create());
        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        subject.FirstName = "Stale";
        Assert.True(firstName.TryGetWriteState(includeSourceCommitsInRevision: false, out var stragglerRevision, out _));

        subject.LastName = "Newer";
        Assert.True(lastName.TryGetWriteState(includeSourceCommitsInRevision: false, out var lastNameRevision, out _));

        subject.FirstName = "Newest";

        SubjectPropertyChange[] straggler =
        [
            CreateChange(firstName, "Old", "Stale", stragglerRevision),
            CreateChange(lastName, "Newest", "Newer", lastNameRevision)
        ];

        // Act
        var merged = merger.Merge(straggler, ChangeDeliveryRule.SourceValuesMayBeStale).ToArray();

        // Assert: the superseded commit is dropped, the one carrying the current value still flows.
        var survivor = Assert.Single(merged);
        Assert.Equal(nameof(Person.LastName), survivor.Property.Name);
        Assert.Equal("Newer", survivor.GetNewValue<string>());
    }

    [Fact]
    public void WhenAdmissionRejectsFilteredSurvivors_ThenMergeReturnsEmptyWithTheExactSurvivorCount()
    {
        // Arrange
        using var merger = new ChangeMerger();

        var subject = new Person(InterceptorSubjectContext.Create());
        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        subject.FirstName = "Stale";
        Assert.True(firstName.TryGetWriteState(includeSourceCommitsInRevision: false, out var staleRevision, out _));
        subject.FirstName = "Newest";

        subject.LastName = "Current";
        Assert.True(lastName.TryGetWriteState(includeSourceCommitsInRevision: false, out var currentRevision, out _));

        var admissionCount = 0;
        SubjectPropertyChange[] changes =
        [
            CreateChange(firstName, "Old", "Stale", staleRevision),
            CreateChange(lastName, "Old", "Current", currentRevision)
        ];

        // Act
        var merged = merger.Merge(
            changes,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            count =>
            {
                admissionCount = count;
                return false;
            });

        // Assert
        Assert.True(merged.IsEmpty);
        Assert.Equal(1, admissionCount);
    }

    /// <summary>
    /// Suppression shrinks the survivor count, and <see cref="ChangeMerger.Reset"/> only clears
    /// the prefix that count describes. Without clearing the dropped tail, those slots would keep their
    /// subjects and boxed values alive inside the pooled buffer, which outlives the batch.
    /// </summary>
    [Fact]
    public void WhenSurvivorsAreSuppressed_ThenTheDroppedSlotsHoldNoReferences()
    {
        // Arrange
        using var merger = new ChangeMerger();

        var subject = new Person(InterceptorSubjectContext.Create());
        var firstName = new PropertyReference(subject, nameof(Person.FirstName));
        var lastName = new PropertyReference(subject, nameof(Person.LastName));

        subject.FirstName = "Stale";
        Assert.True(firstName.TryGetWriteState(includeSourceCommitsInRevision: false, out var stragglerRevision, out _));

        subject.LastName = "Newer";
        Assert.True(lastName.TryGetWriteState(includeSourceCommitsInRevision: false, out var lastNameRevision, out _));

        subject.FirstName = "Newest";

        // Act: the first is superseded and dropped, the second survives.
        var merged = merger.Merge(
            [CreateChange(firstName, "Old", "Stale", stragglerRevision), CreateChange(lastName, "Newest", "Newer", lastNameRevision)],
            ChangeDeliveryRule.SourceValuesMayBeStale);

        // Assert
        Assert.Equal(1, merged.Length);

        var buffer = GetBuffer(merger);
        for (var index = merged.Length; index < buffer.Length; index++)
        {
            Assert.True(buffer[index].Property.Subject is null,
                $"slot {index} past the survivor count still references a subject");
        }
    }

    private static SubjectPropertyChange[] GetBuffer(ChangeMerger merger)
    {
        var field = typeof(ChangeMerger)
            .GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.True(field is not null, "_buffer was renamed, this test needs updating.");
        return (SubjectPropertyChange[])field!.GetValue(merger)!;
    }

    private static SubjectPropertyChange CreateChange(
        PropertyReference property,
        string? oldValue,
        string? newValue,
        long revision,
        ChangeOrigin? origin = null,
        DateTimeOffset? changedTimestamp = null)
    {
        return SubjectPropertyChange.Create(
            property,
            origin ?? ChangeOrigin.Local,
            changedTimestamp ?? DateTimeOffset.UtcNow,
            null,
            oldValue,
            newValue,
            revision);
    }
}
