using Namotion.Interceptor.Testing;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

public class ContextOwnershipRouteTests
{
    [Fact]
    public void WhenOwnershipRouteIsInstalled_ThenServicesResolveAfterFallbacks()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var routeTarget = InterceptorSubjectContext.Create();
        routeTarget.AddService<IRouteService>(new RouteService("route"));

        var fallback = InterceptorSubjectContext.Create();
        fallback.AddService<IRouteService>(new RouteService("fallback"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        context.AddFallbackContext(fallback);

        var route = new InterceptorSubjectContext.ContextOwnershipRoute(routeTarget, ownershipDomain);

        // Act
        var installed = context.TryChangeOwnershipRoute(null, route);
        var names = context.GetServices<IRouteService>().Select(service => service.Name).ToArray();

        // Assert
        Assert.True(installed);
        Assert.Equal(["local", "fallback", "route"], names);
    }

    [Fact]
    public void WhenFallbackAndOwnershipRouteShareTarget_ThenTargetServicesResolveOnce()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        var service = new RouteService("target");
        target.AddService<IRouteService>(service);

        var context = InterceptorSubjectContext.Create();
        context.AddFallbackContext(target);
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);

        // Act
        Assert.True(context.TryChangeOwnershipRoute(null, route));
        var services = context.GetServices<IRouteService>();

        // Assert
        Assert.Single(services);
        Assert.Same(service, services[0]);
    }

    [Fact]
    public void WhenOldDescriptorClearsSameTargetGeneration_ThenNewGenerationRemains()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target"));
        var context = InterceptorSubjectContext.Create();

        var first = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        var second = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, first));
        Assert.True(context.TryChangeOwnershipRoute(first, second));

        // Act
        var staleClear = context.TryChangeOwnershipRoute(first, null);

        // Assert
        Assert.False(staleClear);
        Assert.Single(context.GetServices<IRouteService>());
        Assert.True(context.TryChangeOwnershipRoute(second, null));
        Assert.Empty(context.GetServices<IRouteService>());
    }

    [Fact]
    public void WhenServicesAreAddedReentrantlyAfterRouteInstall_ThenRouteSurvivesEveryStatePublication()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("route"));

        var context = InterceptorSubjectContext.Create();
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, route));

        // Act
        var added = context.TryAddService<IRouteService>(
            () =>
            {
                context.AddService<IRouteService>(new RouteService("reentrant"));
                return new RouteService("added");
            },
            _ => false);

        var names = context.GetServices<IRouteService>().Select(service => service.Name).ToArray();

        // Assert
        Assert.True(added);
        Assert.Equal(["reentrant", "added", "route"], names);
    }

    [Fact]
    public void WhenRouteTargetMutatesAfterResolution_ThenInvalidatedStateRetainsRoute()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target-1"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, route));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        // Act
        target.AddService<IRouteService>(new RouteService("target-2"));

        // Assert
        Assert.Equal(3, context.GetServices<IRouteService>().Length);
    }

    [Fact]
    public void WhenRouteTransfersToDifferentTarget_ThenReverseDependenciesFollowPublishedRelationships()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var targetA = InterceptorSubjectContext.Create();
        var targetB = InterceptorSubjectContext.Create();
        targetA.AddService<IRouteService>(new RouteService("a-1"));
        targetB.AddService<IRouteService>(new RouteService("b-1"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        var routeA = new InterceptorSubjectContext.ContextOwnershipRoute(targetA, ownershipDomain);
        var routeB = new InterceptorSubjectContext.ContextOwnershipRoute(targetB, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, routeA));
        Assert.True(context.AddFallbackContext(targetA));
        Assert.Equal(["local", "a-1"],
            context.GetServices<IRouteService>().Select(service => service.Name).ToArray());

        // Act: transfer the route away from A while the fallback still depends on A.
        Assert.True(context.TryChangeOwnershipRoute(routeA, routeB));

        // Assert
        Assert.Equal(["local", "a-1", "b-1"],
            context.GetServices<IRouteService>().Select(service => service.Name).ToArray());

        var stateWithA = GetState(context);
        targetA.AddService<IRouteService>(new RouteService("a-2"));

        // Assert
        Assert.NotSame(stateWithA, GetState(context));
        Assert.Equal(4, context.GetServices<IRouteService>().Length);

        // Act: removing the fallback ends the final relationship to A.
        Assert.True(context.RemoveFallbackContext(targetA));
        Assert.Equal(["local", "b-1"],
            context.GetServices<IRouteService>().Select(service => service.Name).ToArray());

        var stateWithoutA = GetState(context);
        targetA.AddService<IRouteService>(new RouteService("a-3"));

        // Assert
        Assert.Same(stateWithoutA, GetState(context));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        var stateWithB = GetState(context);
        targetB.AddService<IRouteService>(new RouteService("b-2"));
        Assert.NotSame(stateWithB, GetState(context));
        Assert.Equal(3, context.GetServices<IRouteService>().Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenFallbackAndRouteShareTarget_ThenRemovingOneKeepsInvalidationThroughTheOther(
        bool removeFallbackFirst)
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target-1"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        context.AddFallbackContext(target);
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, route));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        // Act
        if (removeFallbackFirst)
        {
            Assert.True(context.RemoveFallbackContext(target));
        }
        else
        {
            Assert.True(context.TryChangeOwnershipRoute(route, null));
        }

        target.AddService<IRouteService>(new RouteService("target-2"));

        // Assert
        Assert.Equal(3, context.GetServices<IRouteService>().Length);

        // Act
        if (removeFallbackFirst)
        {
            Assert.True(context.TryChangeOwnershipRoute(route, null));
        }
        else
        {
            Assert.True(context.RemoveFallbackContext(target));
        }

        Assert.Single(context.GetServices<IRouteService>());
        var stateAfterFinalRemoval = GetState(context);
        target.AddService<IRouteService>(new RouteService("target-3"));

        // Assert
        Assert.Same(stateAfterFinalRemoval, GetState(context));
        Assert.Single(context.GetServices<IRouteService>());
    }

    [Fact]
    public void WhenOwnershipRoutesFormDelegationCycle_ThenExceptionGuidesToRemoveOwnershipRoute()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var routeA = new InterceptorSubjectContext.ContextOwnershipRoute(contextB, ownershipDomain);
        var routeB = new InterceptorSubjectContext.ContextOwnershipRoute(contextA, ownershipDomain);
        Assert.True(contextA.TryChangeOwnershipRoute(null, routeA));
        Assert.True(contextB.TryChangeOwnershipRoute(null, routeB));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => contextA.GetServices<IRouteService>());

        // Assert
        Assert.Contains("delegation cycle", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ownership-route registration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenSharedTargetFallbackAndOwnershipRoutesFormDelegationCycle_ThenExceptionGuidesToRemoveBothRegistrations()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        contextA.AddFallbackContext(contextB);
        contextB.AddFallbackContext(contextA);
        Assert.True(contextA.TryChangeOwnershipRoute(
            null,
            new InterceptorSubjectContext.ContextOwnershipRoute(contextB, ownershipDomain)));
        Assert.True(contextB.TryChangeOwnershipRoute(
            null,
            new InterceptorSubjectContext.ContextOwnershipRoute(contextA, ownershipDomain)));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => contextA.GetServices<IRouteService>());

        // Assert
        Assert.Contains(
            "both the fallback-context and ownership-route registrations from a shared-target hop",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTwoThreadsInstallFirstRoute_ThenExactlyOneDescriptorWins()
    {
        const int attempts = 500;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Arrange
            var ownershipDomain = InterceptorSubjectContext.Create();
            var firstTarget = InterceptorSubjectContext.Create();
            var secondTarget = InterceptorSubjectContext.Create();
            var firstService = new RouteService("first");
            var secondService = new RouteService("second");
            firstTarget.AddService<IRouteService>(firstService);
            secondTarget.AddService<IRouteService>(secondService);

            var context = InterceptorSubjectContext.Create();
            var firstRoute = new InterceptorSubjectContext.ContextOwnershipRoute(firstTarget, ownershipDomain);
            var secondRoute = new InterceptorSubjectContext.ContextOwnershipRoute(secondTarget, ownershipDomain);
            using var start = new Barrier(2);
            var results = new bool[2];

            var installers = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryChangeOwnershipRoute(null, firstRoute);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryChangeOwnershipRoute(null, secondRoute);
                }, TaskCreationOptions.LongRunning)
            };

            // Act: event-driven so completed attempts do not wait for a polling interval, while
            // the timeout still turns a deadlock into a useful failure.
            try
            {
                await Task.WhenAll(installers).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Concurrent ownership-route installation did not complete on attempt {attempt} of {attempts}.",
                    exception);
            }

            // Assert
            Assert.NotEqual(results[0], results[1]);
            var service = Assert.Single(context.GetServices<IRouteService>());
            Assert.Same(results[0] ? firstService : secondService, service);
        }
    }

    [Fact]
    public async Task WhenTwoThreadsTransferSameRoute_ThenExactlyOneReplacementWins()
    {
        const int attempts = 500;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Arrange
            var ownershipDomain = InterceptorSubjectContext.Create();
            var initialTarget = InterceptorSubjectContext.Create();
            var firstTarget = InterceptorSubjectContext.Create();
            var secondTarget = InterceptorSubjectContext.Create();
            var firstService = new RouteService("first");
            var secondService = new RouteService("second");
            firstTarget.AddService<IRouteService>(firstService);
            secondTarget.AddService<IRouteService>(secondService);

            var context = InterceptorSubjectContext.Create();
            var initialRoute = new InterceptorSubjectContext.ContextOwnershipRoute(initialTarget, ownershipDomain);
            var firstRoute = new InterceptorSubjectContext.ContextOwnershipRoute(firstTarget, ownershipDomain);
            var secondRoute = new InterceptorSubjectContext.ContextOwnershipRoute(secondTarget, ownershipDomain);
            Assert.True(context.TryChangeOwnershipRoute(null, initialRoute));

            using var start = new Barrier(2);
            var results = new bool[2];
            var transfers = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryChangeOwnershipRoute(initialRoute, firstRoute);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryChangeOwnershipRoute(initialRoute, secondRoute);
                }, TaskCreationOptions.LongRunning)
            };

            // Act: event-driven so completed attempts do not wait for a polling interval, while
            // the timeout still turns a deadlock into a useful failure.
            try
            {
                await Task.WhenAll(transfers).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Concurrent ownership-route transfer did not complete on attempt {attempt} of {attempts}.",
                    exception);
            }

            // Assert
            Assert.NotEqual(results[0], results[1]);
            var service = Assert.Single(context.GetServices<IRouteService>());
            Assert.Same(results[0] ? firstService : secondService, service);
        }
    }

    [Fact]
    public async Task WhenRouteAndTargetMutateConcurrently_ThenQuiescentResolutionSeesAllTargetServices()
    {
        // Arrange
        const int mutations = 200;
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target-initial"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        var initialRoute = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, initialRoute));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        using var start = new Barrier(3);
        using var routeMidpoint = new ManualResetEventSlim(false);
        using var readerObservedMidpoint = new ManualResetEventSlim(false);
        var activeWriters = 2;

        var routeWriter = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                var current = initialRoute;
                for (var index = 0; index < mutations; index++)
                {
                    Assert.True(context.TryChangeOwnershipRoute(current, null));
                    current = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
                    Assert.True(context.TryChangeOwnershipRoute(null, current));

                    if (index == mutations / 2)
                    {
                        routeMidpoint.Set();
                        readerObservedMidpoint.Wait();
                    }
                }
            }
            finally
            {
                routeMidpoint.Set();
                Interlocked.Decrement(ref activeWriters);
            }
        }, TaskCreationOptions.LongRunning);

        var serviceWriter = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                for (var index = 0; index < mutations; index++)
                {
                    target.AddService<IRouteService>(new RouteService($"target-{index}"));
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeWriters);
            }
        }, TaskCreationOptions.LongRunning);

        var reader = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                while (!routeMidpoint.IsSet)
                {
                    _ = context.GetServices<IRouteService>();
                }

                _ = context.GetServices<IRouteService>();
            }
            finally
            {
                readerObservedMidpoint.Set();
            }

            while (Volatile.Read(ref activeWriters) != 0)
            {
                _ = context.GetServices<IRouteService>();
            }
        }, TaskCreationOptions.LongRunning);

        // Act
        var workers = new[] { routeWriter, serviceWriter, reader };
        await AsyncTestHelpers.WaitUntilAsync(
            () => workers.All(worker => worker.IsCompleted),
            message: "Concurrent route, target, and resolution work did not complete");
        await Task.WhenAll(workers);

        // Assert
        Assert.Equal(mutations + 2, context.GetServices<IRouteService>().Length);
    }

    private interface IRouteService
    {
        string Name { get; }
    }

    private sealed class RouteService(string name) : IRouteService
    {
        public string Name { get; } = name;
    }
}
