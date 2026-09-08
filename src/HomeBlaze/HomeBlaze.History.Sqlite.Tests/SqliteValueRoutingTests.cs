using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public class SqliteValueRoutingTests
{
    [Fact]
    public void WhenDecimalRouted_ThenStoredAsDoubleWithExactJsonArchive()
    {
        // Arrange & Act - decimal is routed to value_double for numeric use, but the exact decimal text is
        // also archived in value_json so the original precision is recoverable later.
        var routed = SqliteValueRouting.CreateRow(0.1m, ValueColumn.Double, isUlong: false, maxJsonSize: 8192);

        // Assert
        Assert.Equal(0.1d, routed.Row.Double);
        Assert.Equal("0.1", routed.Row.Json);
        Assert.Null(routed.Row.Long);
        Assert.False(routed.Oversized);
    }

    [Fact]
    public void WhenPlainDoubleRouted_ThenNoJsonArchive()
    {
        // Arrange & Act - a real double has no exact-decimal text to preserve, so value_json stays empty.
        var routed = SqliteValueRouting.CreateRow(2.5d, ValueColumn.Double, isUlong: false, maxJsonSize: 8192);

        // Assert
        Assert.Equal(2.5d, routed.Row.Double);
        Assert.Null(routed.Row.Json);
    }
}
