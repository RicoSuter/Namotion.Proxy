using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class PropertyBufferTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static Sample LongSample(int secondsFromBase, long value) =>
        new(Base.AddSeconds(secondsFromBase), value, null, null);

    private static PropertyBuffer NewBuffer(int capacity = 1000) =>
        new(capacity, ValueColumn.Long, isUlong: false);

    [Fact]
    public void WhenAppendedInOrder_ThenRangeReturnsAscending()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(1, 11));
        buffer.Append(LongSample(2, 12));

        // Act
        var range = buffer.Range(Base, Base.AddSeconds(10));

        // Assert
        Assert.Equal(new long?[] { 10, 11, 12 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenAppendedOutOfOrder_ThenRangeStaysAscending()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(2, 12));
        buffer.Append(LongSample(1, 11)); // late arrival

        // Act
        var range = buffer.Range(Base, Base.AddSeconds(10));

        // Assert
        Assert.Equal(new long?[] { 10, 11, 12 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenCapacityExceeded_ThenOldestEvictedAndCountReported()
    {
        // Arrange
        var buffer = NewBuffer(capacity: 3);

        // Act - append five into a capacity-three ring
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(LongSample(i, 100 + i));
        }

        // Assert - newest three retained in order, two evicted
        var range = buffer.Range(Base, Base.AddSeconds(10));
        Assert.Equal(new long?[] { 102, 103, 104 }, range.Select(sample => sample.Long).ToArray());
        Assert.Equal(2, buffer.EvictedCount);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void WhenCapacityNotExceeded_ThenNothingIsEvicted()
    {
        // Arrange
        var buffer = NewBuffer(capacity: 3);

        // Act
        var evicted = buffer.Append(LongSample(10, 10));

        // Assert
        Assert.False(evicted);
    }

    [Fact]
    public void WhenCapacityExceeded_ThenEvictionIsReportedAndOldestRetainedAdvances()
    {
        // Arrange
        var buffer = NewBuffer(capacity: 2);
        buffer.Append(LongSample(1, 1));
        buffer.Append(LongSample(2, 2));

        // Act
        var evicted = buffer.Append(LongSample(3, 3));

        // Assert
        Assert.True(evicted);
        Assert.Equal(Base.AddSeconds(2), buffer.OldestTimestamp);
    }

    [Fact]
    public void WhenOlderLateSampleArrivesAtCapacity_ThenNewerRetainedSamplesRemain()
    {
        // Arrange
        var buffer = NewBuffer(capacity: 2);
        buffer.Append(LongSample(2, 2));
        buffer.Append(LongSample(3, 3));

        // Act
        var evicted = buffer.Append(LongSample(1, 1));

        // Assert
        var range = buffer.Range(Base, Base.AddSeconds(10));
        Assert.Equal(new long?[] { 2, 3 }, range.Select(sample => sample.Long).ToArray());
        Assert.Equal(1, buffer.EvictedCount);
        Assert.True(evicted);
        Assert.Equal(Base.AddSeconds(2), buffer.OldestTimestamp);
    }

    [Fact]
    public void WhenTimestampAlreadyExists_ThenSampleIsReplaced()
    {
        // Arrange
        var buffer = NewBuffer(capacity: 2);
        buffer.Append(LongSample(1, 1));
        buffer.Append(LongSample(2, 2));

        // Act
        buffer.Append(LongSample(1, 10));

        // Assert
        var range = buffer.Range(Base, Base.AddSeconds(10));
        Assert.Equal(new long?[] { 10, 2 }, range.Select(sample => sample.Long).ToArray());
        Assert.Equal(2, buffer.Count);
        Assert.Equal(0, buffer.EvictedCount);
    }

    [Fact]
    public void WhenEvictOlderThan_ThenLeadingOldSamplesDropped()
    {
        // Arrange
        var buffer = NewBuffer();
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(LongSample(i, 100 + i));
        }

        // Act - drop everything strictly older than Base+2s
        var dropped = buffer.EvictOlderThan(Base.AddSeconds(2));

        // Assert - samples at 0s and 1s gone; 2s..4s remain
        Assert.Equal(2, dropped);
        var range = buffer.Range(Base, Base.AddSeconds(10));
        Assert.Equal(new long?[] { 102, 103, 104 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenRangeIsHalfOpen_ThenToBoundIsExclusive()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(1, 11));
        buffer.Append(LongSample(2, 12));

        // Act - [0s, 2s): excludes the 2s sample
        var range = buffer.Range(Base, Base.AddSeconds(2));

        // Assert
        Assert.Equal(new long?[] { 10, 11 }, range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenAtOrBefore_ThenReturnsMostRecentNotAfter()
    {
        // Arrange
        var buffer = NewBuffer();
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(2, 12));
        buffer.Append(LongSample(4, 14));

        // Act
        var atGap = buffer.AtOrBefore(Base.AddSeconds(3)); // between 2s and 4s
        var beforeAll = buffer.AtOrBefore(Base.AddSeconds(-1));

        // Assert
        Assert.Equal(12, atGap!.Value.Long);
        Assert.Null(beforeAll);
    }

    [Fact]
    public void WhenEmpty_ThenOldestAndNewestAreNull()
    {
        // Arrange
        var buffer = NewBuffer();

        // Act & Assert
        Assert.Null(buffer.Oldest);
        Assert.Null(buffer.Newest);
    }
    [Fact]
    public void WhenOnlyAFewSamplesAreHeld_ThenTheRingDoesNotAllocateItsFullCapacity()
    {
        // Arrange - the configured capacity is a ceiling, not an up-front cost. Paths embed collection
        // indices, so a single list reorder abandons one buffer per renamed subject; allocating the
        // whole ring on the first sample made each of those cost the full array forever.
        var buffer = NewBuffer(capacity: 1000);

        // Act
        buffer.Append(LongSample(0, 10));
        buffer.Append(LongSample(1, 11));
        buffer.Append(LongSample(2, 12));

        // Assert
        Assert.True(buffer.Capacity < 1000, $"ring allocated {buffer.Capacity} slots for 3 samples");
        Assert.Equal(1000, buffer.MaxCapacity);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void WhenTheRingGrows_ThenEverySampleSurvivesInOrder()
    {
        // Arrange - growth re-linearizes the ring, which is where the index arithmetic could drop or
        // reorder samples.
        var buffer = NewBuffer(capacity: 1000);
        for (var second = 0; second < 50; second++)
        {
            buffer.Append(LongSample(second, second));
        }

        // Act
        var range = buffer.Range(Base, Base.AddSeconds(100));

        // Assert
        Assert.Equal(Enumerable.Range(0, 50).Select(value => (long?)value).ToArray(),
            range.Select(sample => sample.Long).ToArray());
        Assert.True(buffer.Capacity >= 50 && buffer.Capacity < 1000);
    }

    [Fact]
    public void WhenGrowthWrappedTheRingFirst_ThenOrderIsStillPreserved()
    {
        // Arrange - growth has to read through the wrap rather than straight off the array. Reaching
        // that needs the ring's start to have moved off zero *before* it grows, which an age sweep
        // does: it evicts from the front, and the next refill then grows a wrapped ring. A capacity at
        // or below the initial allocation never grows at all, so this test previously proved nothing.
        var buffer = NewBuffer(capacity: 1000);
        for (var second = 0; second < 16; second++)
        {
            buffer.Append(LongSample(second, second));
        }

        buffer.EvictOlderThan(Base.AddSeconds(3)); // _start moves off zero, ring is now wrapped
        for (var second = 16; second < 24; second++)
        {
            buffer.Append(LongSample(second, second)); // refills past the allocation, forcing growth
        }

        // Act
        var range = buffer.Range(Base, Base.AddSeconds(100));

        // Assert
        Assert.Equal(
            Enumerable.Range(3, 21).Select(value => (long?)value).ToArray(),
            range.Select(sample => sample.Long).ToArray());
    }

    [Fact]
    public void WhenALateSampleForcesGrowth_ThenTheInsertLandsInOrder()
    {
        // Arrange - the late-arrival path can also trigger growth, which is the one place the shift
        // loop runs against a freshly re-linearized array. A ring already at its ceiling takes the
        // evict branch instead, so this needs a ceiling well above the current allocation.
        var buffer = NewBuffer(capacity: 1000);
        buffer.Append(LongSample(0, 0));
        buffer.Append(LongSample(2, 2));
        buffer.Append(LongSample(4, 4));
        buffer.Append(LongSample(6, 6)); // ring is now full at its current allocation, not its ceiling

        // Act
        buffer.Append(LongSample(3, 3));

        // Assert
        var range = buffer.Range(Base, Base.AddSeconds(100));
        Assert.Equal(new long?[] { 0, 2, 3, 4, 6 }, range.Select(sample => sample.Long).ToArray());
        Assert.Equal(0, buffer.EvictedCount);
    }

    [Fact]
    public void WhenASweepEmptiesTheRing_ThenItReleasesTheGrownArray()
    {
        // Arrange - this is what actually reclaims an abandoned path: the samples age out and the ring
        // hands back the array it grew into, leaving only a small husk in the dictionary.
        var buffer = NewBuffer(capacity: 1000);
        for (var second = 0; second < 200; second++)
        {
            buffer.Append(LongSample(second, second));
        }

        var grown = buffer.Capacity;

        // Act
        buffer.EvictOlderThan(Base.AddSeconds(1000));

        // Assert
        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.Capacity < grown, $"ring still holds {buffer.Capacity} slots after emptying");
    }

    [Fact]
    public void WhenALateSampleLandsInsideAFullRing_ThenTheOldestIsDroppedAndOrderHolds()
    {
        // Arrange - the late-arrival branch that both evicts and shifts: a full ring plus an insert
        // strictly inside the retained range. It combines dropping the oldest, the position fixup and
        // the shift loop, which is the densest index arithmetic in the buffer.
        var buffer = NewBuffer(capacity: 3);
        buffer.Append(LongSample(10, 10));
        buffer.Append(LongSample(20, 20));
        buffer.Append(LongSample(30, 30));

        // Act
        buffer.Append(LongSample(15, 15));

        // Assert
        var range = buffer.Range(Base, Base.AddSeconds(100));
        Assert.Equal(new long?[] { 15, 20, 30 }, range.Select(sample => sample.Long).ToArray());
        Assert.Equal(3, buffer.Count);
        Assert.Equal(1, buffer.EvictedCount);
        Assert.Equal(Base.AddSeconds(15), buffer.OldestTimestamp);
    }
}
