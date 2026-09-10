using HomeBlaze.History.Abstractions;
using HomeBlaze.History.InMemory;
using HomeBlaze.History.Sqlite;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

/// <summary>
/// One uniform seam over every history store engine, so a single parity test runs against all of them.
/// </summary>
public interface IParityStore : IDisposable
{
    void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType);
    void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath);
    Task FlushAsync();
    HistorySeries Query(HistoryQuery query);
    HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf);
}

/// <summary>The wall-clock bounds all parity stores share.</summary>
public static class ParityClock
{
    public static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Start = new(1960, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Now = Base.AddYears(1);
}

internal sealed class InMemoryParityStore : IParityStore
{
    private DateTimeOffset _now = ParityClock.Start;
    private readonly InMemoryHistoryStore _engine;

    public InMemoryParityStore()
    {
        _engine = new InMemoryHistoryStore(
            priority: 100,
            maxPointsPerProperty: 100_000,
            maxAge: TimeSpan.FromDays(36500),
            maxJsonSize: 8192,
            getUtcNow: () => _now);
        _now = ParityClock.Now;
    }

    public void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
        => _engine.Record(propertyPath, timestamp, value, propertyType);

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
        => _engine.RecordMove(timestamp, fromPath, toPath);

    public Task FlushAsync() => Task.CompletedTask; // in-memory records are immediately queryable

    public HistorySeries Query(HistoryQuery query) => _engine.Query(query);

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
        => _engine.GetSampleAtOrBefore(propertyPath, asOf);

    public void Dispose() { }
}

internal sealed class SqliteParityStore : IParityStore
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-parity-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = ParityClock.Start;
    private readonly SqliteHistoryStore _engine;

    public SqliteParityStore()
    {
        _engine = new SqliteHistoryStore(
            priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly,
            maxAge: TimeSpan.FromDays(36500), maxJsonSize: 8192, getUtcNow: () => _now);
        _now = ParityClock.Now;
    }

    public void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
        => _engine.Record(propertyPath, timestamp, value, propertyType);

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
        => _engine.RecordMove(timestamp, fromPath, toPath);

    public Task FlushAsync() => _engine.FlushAsync(CancellationToken.None);

    public HistorySeries Query(HistoryQuery query) => _engine.Query(query);

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
        => _engine.GetSampleAtOrBefore(propertyPath, asOf);

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

/// <summary>A named factory for one store under parity test; ToString drives the xUnit theory display name.</summary>
public sealed class ParityStoreFactory
{
    public required string Name { get; init; }
    public required Func<IParityStore> Create { get; init; }
    public override string ToString() => Name;
}

/// <summary>The set of stores every parity theory runs against. Add the TimescaleDB factory here when Task 6 lands.</summary>
public static class ParityStores
{
    public static TheoryData<ParityStoreFactory> Stores => new()
    {
        new ParityStoreFactory { Name = "InMemory", Create = () => new InMemoryParityStore() },
        new ParityStoreFactory { Name = "Sqlite", Create = () => new SqliteParityStore() },
    };
}

/// <summary>Tolerance-aware comparison of a query result's numeric points against a golden sequence.</summary>
internal static class ParityAssert
{
    public static void NumbersEqual(IReadOnlyList<double?> expected, HistorySeries actual, int precision = 6)
    {
        var got = actual.Points.Select(point => point.Number).ToArray();
        Assert.Equal(expected.Count, got.Length);
        for (var index = 0; index < expected.Count; index++)
        {
            if (expected[index] is null)
            {
                Assert.Null(got[index]);
            }
            else
            {
                Assert.NotNull(got[index]);
                Assert.Equal(expected[index]!.Value, got[index]!.Value, precision);
            }
        }
    }
}
