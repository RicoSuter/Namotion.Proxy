using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class ParityHarnessTests
{
    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenSingleSampleRecordedAndFlushed_ThenRawQueryReturnsIt(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        store.Record("/a/Value", ParityClock.Base, 42d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Value", ParityClock.Base.AddSeconds(-1), ParityClock.Base.AddSeconds(1)));

        // Assert
        var point = Assert.Single(series.Points);
        Assert.Equal(42d, point.Number!.Value, 6);
    }
}
