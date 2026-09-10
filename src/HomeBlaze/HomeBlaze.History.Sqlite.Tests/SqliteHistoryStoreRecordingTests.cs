using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Sqlite.Tests;

/// <summary>
/// Drives <see cref="SqliteHistoryStoreSubject"/> against a real graph wired with the real
/// <see cref="SubjectPathResolver"/>, mutates [State] properties, and asserts that changes are
/// recorded under their canonical property paths and persisted to the SQLite partition files.
/// Because the store flushes on an interval, the harness forces a flush through the internal
/// <see cref="SqliteHistoryStoreSubject.FlushNowAsync"/> test hook before each query, so the wait is
/// deterministic rather than timing-dependent.
/// </summary>
public class SqliteHistoryStoreRecordingTests
{
    private static (IInterceptorSubjectContext Context, TestRoot Root, RootManager RootManager) CreateGraph()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);

        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        // The resolver reads the root lazily, so it can be built before the manager that owns it,
        // and RootManager registers it into the context.
        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, context, pathResolver, null);
        context.WithService(() => rootManager);

        // PropertyAttributeInitializer turns the C# [State] attribute into the KnownAttributes.State
        // registry attribute that HasHistory() checks. Without it, HasHistory() is always false.
        context.WithService<IPropertyLifecycleHandler>(
            () => new PropertyAttributeInitializer(),
            handler => handler is PropertyAttributeInitializer);

        var root = new TestRoot(context);

        // RootManager.Root has an internal setter (accessible only to HomeBlaze.Services.Tests); set it
        // through reflection so the resolver treats TestRoot as the canonical root ("/").
        typeof(RootManager).GetProperty(nameof(RootManager.Root))!
            .SetValue(rootManager, root);

        return (context, root, rootManager);
    }

    private static (SqliteHistoryStoreSubject Store, string DatabasePath) CreateStore(IInterceptorSubjectContext sharedContext)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

        var store = new SqliteHistoryStoreSubject(NullLogger<SqliteHistoryStoreSubject>.Instance)
        {
            DatabasePath = databasePath,
            // A short buffer time keeps the change-queue batch small so the warm-up loop converges quickly.
            BufferTimeMilliseconds = 50,
            FlushIntervalSeconds = 1
        };

        // Attach to the shared graph context: the store's ChangeQueueProcessor subscription and
        // path resolver are resolved through it, so the store observes the whole graph.
        ((IInterceptorSubject)store).AttachToContext(sharedContext);

        return (store, databasePath);
    }

    /// <summary>
    /// Mutates <paramref name="propertyPath"/> to <paramref name="targetValue"/> and waits until a
    /// point with exactly that value is persisted under the canonical path. A warm-up phase re-applies
    /// a distinct sentinel value until any point appears, which deterministically bridges the brief
    /// startup gap before the change-queue subscription goes live (no fixed sleep). It then applies the
    /// target value and polls until it lands. Every poll forces a flush through the internal test hook so
    /// queued samples become queryable immediately instead of waiting for the interval flush.
    /// </summary>
    private static async Task<HistorySeries> RecordAndWaitForValueAsync(
        SqliteHistoryStoreSubject store, string propertyPath, Action<double> mutate, double targetValue)
    {
        var warmupValue = -1000.0;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                warmupValue -= 1.0;
                mutate(warmupValue);
                store.FlushNowAsync().GetAwaiter().GetResult();
                var warmup = QuerySeries(store, propertyPath);
                return warmup.Points.Length > 0;
            },
            message: $"Store never started recording under '{propertyPath}' (status='{store.Status}', recorded={store.RecordedCount}).");

        // Now the subscription is live; apply the asserted value and wait for it specifically.
        mutate(targetValue);
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                store.FlushNowAsync().GetAwaiter().GetResult();
                return QuerySeries(store, propertyPath).Points.Any(point => point.Number == targetValue);
            },
            message: $"Value {targetValue} not recorded under '{propertyPath}' (recorded={store.RecordedCount}).");

        return QuerySeries(store, propertyPath);
    }

    private static HistorySeries QuerySeries(SqliteHistoryStoreSubject store, string propertyPath) =>
        store.QueryAsync(
            new HistoryQuery(propertyPath, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None).GetAwaiter().GetResult();

    private static void DeleteDirectory(string databasePath)
    {
        try
        {
            if (Directory.Exists(databasePath))
            {
                Directory.Delete(databasePath, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }

    [Fact]
    public void WhenConstructed_ThenTitleIsRenderedForSQLiteHistory()
    {
        // Arrange
        var store = new SqliteHistoryStoreSubject(NullLogger<SqliteHistoryStoreSubject>.Instance);

        // Act
        var title = store.Title;

        // Assert
        Assert.Equal("SQLite History", title);
    }

    [Fact]
    public async Task WhenRootStatePropertyMutated_ThenRecordedUnderCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 21.5);

            // Assert
            Assert.Contains(series.Points, point => point.Number == 21.5);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenChildStatePropertyMutated_ThenRecordedUnderChildCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var series = await RecordAndWaitForValueAsync(store, "/Child/Pressure", value => child.Pressure = value, 3.3);

            // Assert
            Assert.Contains(series.Points, point => point.Number == 3.3);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenChildReparentedToNewSlot_ThenPreMoveAndPostMoveSamplesQueryableUnderNewPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            // 1. Record a pre-move sample at the child's original canonical path (/Child/Pressure). This
            //    also seeds the store's move-detection cache with the subject's old path.
            var beforeMove = await RecordAndWaitForValueAsync(
                store, "/Child/Pressure", value => child.Pressure = value, 3.3);
            Assert.Contains(beforeMove.Points, point => point.Number == 3.3);

            // 2. Reparent the same child subject so its canonical path changes from /Child to /SecondChild.
            //    Add the new reference FIRST (child keeps at least one parent throughout, so no context
            //    detach fires and the move-detection cache entry survives), then clear the old slot so the
            //    resolver returns the new path. Reassigning references fires the lifecycle events that clear
            //    the resolver's path cache, so the next change resolves the new canonical path.
            root.SecondChild = child;
            root.Child = null;

            // 3. Record a post-move sample. The store resolves the new path (/SecondChild/Pressure), sees it
            //    differ from the cached old path, and records a move leg /Child/Pressure -> /SecondChild/Pressure.
            var afterMove = await RecordAndWaitForValueAsync(
                store, "/SecondChild/Pressure", value => child.Pressure = value, 7.7);

            // Assert
            // Querying the new path must return BOTH samples: the post-move one directly, and the pre-move
            // one resolved across the recorded move chain. If the move leg were not recorded, the pre-move
            // sample would remain orphaned under /Child/Pressure and be absent here.
            await store.FlushNowAsync();
            var series = await store.QueryAsync(
                new HistoryQuery("/SecondChild/Pressure", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

            Assert.Contains(series.Points, point => point.Number == 3.3);
            Assert.Contains(series.Points, point => point.Number == 7.7);
            Assert.Equal(afterMove.PropertyPath, series.PropertyPath);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenChildWithTwoHistoryPropertiesReparented_ThenSiblingPropertyKeepsPreMoveHistory()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            await RecordAndWaitForValueAsync(
                store, "/Child/Pressure", value => child.Pressure = value, 3.3);
            await RecordAndWaitForValueAsync(
                store, "/Child/Humidity", value => child.Humidity = value, 4.4);

            root.SecondChild = child;
            root.Child = null;

            await RecordAndWaitForValueAsync(
                store, "/SecondChild/Pressure", value => child.Pressure = value, 7.7);
            await RecordAndWaitForValueAsync(
                store, "/SecondChild/Humidity", value => child.Humidity = value, 8.8);

            // Act
            await store.FlushNowAsync();
            var series = await store.QueryAsync(
                new HistoryQuery(
                    "/SecondChild/Humidity",
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

            // Assert
            Assert.Contains(series.Points, point => point.Number == 4.4);
            Assert.Contains(series.Points, point => point.Number == 8.8);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WhenRunning_ThenCoverageToAdvancesAndPriorityIsFifty()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var (store, databasePath) = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var beforeRecording = DateTimeOffset.UtcNow;
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 12.5);

            // Assert
            Assert.NotEmpty(series.Points);
            Assert.Equal(50, store.Priority);

            // Against a mark taken before the write, not a minute-wide window around "now", which
            // the coverage end would have to be badly stale to fall outside of.
            Assert.True(
                Assert.Single(store.CoverageRanges).To >= beforeRecording,
                "coverage did not advance past the instant before the recorded change");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
            DeleteDirectory(databasePath);
        }
    }
}
