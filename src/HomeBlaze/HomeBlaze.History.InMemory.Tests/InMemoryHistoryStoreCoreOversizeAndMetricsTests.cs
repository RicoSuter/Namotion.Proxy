using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;

namespace HomeBlaze.History.InMemory.Tests;

public class InMemoryHistoryStoreCoreOversizeAndMetricsTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryHistoryStore NewCore(int maxJsonSize = 8192, int maxPoints = 1000) =>
        new(priority: 100, maxPointsPerProperty: maxPoints, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: maxJsonSize, getUtcNow: () => Base.AddHours(1));

    [Fact]
    public void WhenStringExceedsMaxJsonSize_ThenPlaceholderStoredAndCounted()
    {
        // Arrange - cap at 16 chars; record a 100-char string
        var core = NewCore(maxJsonSize: 16);
        var big = new string('x', 100);
        core.Record("/a/Name", Base.AddSeconds(1), big, typeof(string));

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - placeholder row, timeline preserved, OversizeCount incremented
        Assert.Equal(JsonValueKind.Object, point.Json!.Value.ValueKind);
        Assert.True(point.Json!.Value.GetProperty("$oversize").GetBoolean());
        Assert.True(point.Json!.Value.GetProperty("size").GetInt32() >= 100);
        Assert.Equal(1, core.OversizeCount);
    }

    [Fact]
    public void WhenStringWithinCap_ThenStoredVerbatimAndNotCounted()
    {
        // Arrange
        var core = NewCore(maxJsonSize: 1024);
        core.Record("/a/Name", Base.AddSeconds(1), "small", typeof(string));

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal("small", point.Json!.Value.GetString());
        Assert.Equal(0, core.OversizeCount);
    }

    [Fact]
    public void WhenSamplesRecorded_ThenCountMetricsReflectThem()
    {
        // Arrange
        var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/V", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/b/V", Base.AddSeconds(1), 3d, typeof(double));

        // Act & Assert
        Assert.Equal(3, core.RecordedCount);
        Assert.Equal(2, core.TrackedPropertyCount);
        Assert.Equal(3, core.TotalSampleCount);
        Assert.True(core.EstimatedMemoryBytes > 0);
    }

    [Fact]
    public void WhenCapacityEvicts_ThenEvictedCountAccumulates()
    {
        // Arrange - capacity 2, append 5 -> 3 evicted
        var core = NewCore(maxPoints: 2);
        for (var i = 0; i < 5; i++)
        {
            core.Record("/a/V", Base.AddSeconds(i), (double)i, typeof(double));
        }

        // Act & Assert
        Assert.Equal(3, core.EvictedCount);
        Assert.Equal(2, core.TotalSampleCount);
    }
    [Fact]
    public void WhenEverySampleForAPathAgesOut_ThenThePathIsReclaimed()
    {
        // Arrange - the in-memory store is bounded by time, but nothing used to free the per-path
        // bookkeeping. Canonical paths embed collection indices, so reordering a list renames every
        // subject after the removed element and abandons one path each time.
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromSeconds(60),
            maxJsonSize: 8192, getUtcNow: () => now);

        store.Record("/Devices[0]/Value", now, 1d, typeof(double));
        store.Record("/Devices[1]/Value", now, 2d, typeof(double));
        Assert.Equal(2, store.TrackedPropertyCount);

        // Act - both paths fall out of the retention window and the sweep runs.
        now = now.AddMinutes(5);
        store.Sweep();

        // Assert
        Assert.Equal(0, store.TrackedPropertyCount);
    }

    [Fact]
    public void WhenAPathIsReclaimed_ThenTheCumulativeEvictionCountDoesNotGoBackwards()
    {
        // Arrange - eviction counts live on the buffer, so dropping one must carry its total over.
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 2, maxAge: TimeSpan.FromSeconds(60),
            maxJsonSize: 8192, getUtcNow: () => now);

        for (var second = 0; second < 5; second++)
        {
            store.Record("/a/Value", now.AddSeconds(second), second, typeof(double));
        }

        var beforeSweep = store.EvictedCount;
        Assert.True(beforeSweep > 0);

        // Act
        now = now.AddMinutes(5);
        store.Sweep();

        // Assert - the exact total, not merely "not smaller". A >= assertion cannot tell a count that
        // was carried over from one that was counted twice.
        Assert.Equal(0, store.TrackedPropertyCount);
        Assert.Equal(beforeSweep + 2, store.EvictedCount); // 3 capacity evictions, then 2 aged out
    }

    [Fact]
    public async Task WhenARecordRacesTheSweepThatReclaimsItsPath_ThenTheSampleIsNotLost()
    {
        // Arrange - a buffer is empty for the instant between being created and taking its first
        // sample, so a concurrent sweep can retire it right out from under the recorder. Losing the
        // sample there would be invisible: this store claims the live edge at the highest priority, so
        // the merger serves the gap from here rather than falling back to a durable store.
        //
        // maxAge is long, so nothing ages out and every accepted sample must still be held at the end.
        // Each path is distinct, so every single write goes through that create-then-append window.
        const int pathCount = 5000;
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryHistoryStore(
            priority: 100, maxPointsPerProperty: 1000, maxAge: TimeSpan.FromHours(1),
            maxJsonSize: 8192, getUtcNow: () => now);

        var stop = false;
        var sweeper = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                store.Sweep();
            }
        });

        // Act
        for (var index = 0; index < pathCount; index++)
        {
            store.Record($"/Devices[{index}]/Value", now, index, typeof(double));
        }

        Volatile.Write(ref stop, true);
        await sweeper;

        // Assert - not one write ended up in a buffer the sweep had already dropped.
        Assert.Equal(pathCount, store.TotalSampleCount);
        Assert.Equal(pathCount, store.TrackedPropertyCount);
    }
}
