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

namespace HomeBlaze.History.InMemory.Tests;

/// <summary>
/// Drives <see cref="InMemoryHistoryStoreSubject"/> against a real graph wired with the real
/// <see cref="SubjectPathResolver"/>, mutates [State] properties, and asserts that changes are
/// recorded under their canonical property paths.
/// </summary>
public class InMemoryHistoryStoreRecordingTests
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

    private static InMemoryHistoryStoreSubject CreateStore(IInterceptorSubjectContext sharedContext)
    {
        var store = new InMemoryHistoryStoreSubject(NullLogger<InMemoryHistoryStoreSubject>.Instance);

        // Attach to the shared graph context: the store's ChangeQueueProcessor subscription and
        // path resolver are resolved through it, so the store observes the whole graph.
        ((IInterceptorSubject)store).AttachToContext(sharedContext);

        return store;
    }

    /// <summary>
    /// Mutates <paramref name="propertyPath"/> to <paramref name="targetValue"/> and waits until a
    /// point with exactly that value is recorded under the canonical path. A warm-up phase re-applies
    /// a distinct sentinel value until any point appears, which deterministically bridges the brief
    /// startup gap before the change-queue subscription goes live (no fixed sleep). It then applies the
    /// target value and polls until it lands, so the asserted value is never lost to the startup race.
    /// </summary>
    private static async Task<HistorySeries> RecordAndWaitForValueAsync(
        InMemoryHistoryStoreSubject store, string propertyPath, Action<double> mutate, double targetValue)
    {
        // Warm-up: bridge the startup gap until the subscription is live and recording. Each iteration
        // uses a distinct negative value so the equality check never drops it as a no-op repeat. Driving
        // a new mutation on every poll is why this cannot be a plain WaitUntilAsync over a static condition;
        // the wait itself is expressed via WaitUntilAsync over the "any point landed" condition.
        var warmupValue = -1000.0;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                warmupValue -= 1.0;
                mutate(warmupValue);
                var warmup = QuerySeries(store, propertyPath);
                return warmup.Points.Length > 0;
            },
            message: $"Store never started recording under '{propertyPath}' (status='{store.Status}', recorded={store.RecordedCount}).");

        // Now the subscription is live; apply the asserted value and wait for it specifically.
        mutate(targetValue);
        await AsyncTestHelpers.WaitUntilAsync(
            () => QuerySeries(store, propertyPath).Points.Any(point => point.Number == targetValue),
            message: $"Value {targetValue} not recorded under '{propertyPath}' (recorded={store.RecordedCount}).");

        return QuerySeries(store, propertyPath);
    }

    private static HistorySeries QuerySeries(InMemoryHistoryStoreSubject store, string propertyPath) =>
        store.QueryAsync(
            new HistoryQuery(propertyPath, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void WhenConstructed_ThenTitleIsRenderedForInMemoryHistory()
    {
        // Arrange
        var store = new InMemoryHistoryStoreSubject(NullLogger<InMemoryHistoryStoreSubject>.Instance);

        // Act
        var title = store.Title;

        // Assert
        Assert.Equal("In-Memory History", title);
    }

    [Fact]
    public async Task WhenRootStatePropertyMutated_ThenRecordedUnderCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
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
        }
    }

    [Fact]
    public async Task WhenChildStatePropertyMutated_ThenRecordedUnderChildCanonicalPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var store = CreateStore(context);
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
        }
    }

    [Fact]
    public async Task WhenChildReparentedToNewSlot_ThenPreMoveAndPostMoveSamplesQueryableUnderNewPath()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var store = CreateStore(context);
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
        }
    }

    [Fact]
    public async Task WhenChildWithTwoHistoryPropertiesReparented_ThenSiblingPropertyKeepsPreMoveHistory()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var child = new TestChild(context);
        root.Child = child;

        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            // 1. Record a pre-move sample for BOTH history properties at the child's original path
            //    (/Child/...). This seeds the move-detection cache with the old path for each property.
            var pressureBefore = await RecordAndWaitForValueAsync(
                store, "/Child/Pressure", value => child.Pressure = value, 3.3);
            Assert.Contains(pressureBefore.Points, point => point.Number == 3.3);
            var humidityBefore = await RecordAndWaitForValueAsync(
                store, "/Child/Humidity", value => child.Humidity = value, 4.4);
            Assert.Contains(humidityBefore.Points, point => point.Number == 4.4);

            // 2. Reparent the same child subject so its canonical path changes from /Child to /SecondChild.
            //    Add the new reference first (the child keeps a parent throughout, so no context detach
            //    clears the cache), then clear the old slot so the resolver returns the new path.
            root.SecondChild = child;
            root.Child = null;

            // 3. Record a post-move sample for Pressure FIRST. This is the property that consumes the
            //    rename when moves are tracked per subject, which is exactly what must not starve Humidity.
            var pressureAfter = await RecordAndWaitForValueAsync(
                store, "/SecondChild/Pressure", value => child.Pressure = value, 7.7);
            Assert.Contains(pressureAfter.Points, point => point.Number == 7.7);

            // 4. Then record a post-move sample for the sibling Humidity. Per-property move tracking must
            //    independently detect the rename for Humidity and record its /Child -> /SecondChild leg.
            var humidityAfter = await RecordAndWaitForValueAsync(
                store, "/SecondChild/Humidity", value => child.Humidity = value, 8.8);
            Assert.Contains(humidityAfter.Points, point => point.Number == 8.8);

            // Assert
            // Querying the new Humidity path must return BOTH the pre-move (4.4) and post-move (8.8)
            // samples. With per-subject move tracking, Pressure consumes the rename and Humidity's move
            // leg is never recorded, so its pre-move sample stays orphaned under /Child/Humidity.
            var series = await store.QueryAsync(
                new HistoryQuery("/SecondChild/Humidity", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)),
                CancellationToken.None);

            Assert.Contains(series.Points, point => point.Number == 4.4);
            Assert.Contains(series.Points, point => point.Number == 8.8);
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenRunning_ThenCoverageToAdvancesAndPriorityIsHundred()
    {
        // Arrange
        var (context, root, _) = CreateGraph();
        var store = CreateStore(context);
        var hostedService = (IHostedService)store;
        await hostedService.StartAsync(CancellationToken.None);
        try
        {
            // Act
            var beforeRecording = DateTimeOffset.UtcNow;
            var series = await RecordAndWaitForValueAsync(store, "/Temperature", value => root.Temperature = value, 12.5);

            // Assert
            Assert.NotEmpty(series.Points);
            Assert.Equal(100, store.Priority);

            // Against a mark taken before the write, not a minute-wide window around "now", which
            // the coverage end would have to be badly stale to fall outside of.
            Assert.True(
                Assert.Single(store.CoverageRanges).To >= beforeRecording,
                "coverage did not advance past the instant before the recorded change");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }
}
