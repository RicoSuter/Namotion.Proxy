using Namotion.Interceptor.Ads.Client;
using TwinCAT.Ads;
using TwinCAT.Ads.Configuration;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class EmbeddedRouterPoolTests
{
    private sealed class FakeRouter : IEmbeddedAdsRouter
    {
        public int LoopbackPort => 48899;
        public AmsNetId LocalNetId { get; } = AmsNetId.Parse("10.0.0.1.1.1");
        public int AddRouteCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void AddRoute(Route route) => AddRouteCount++;
        public void Dispose() => DisposeCount++;
    }

    private static Route CreateRoute() => new("plc", AmsNetId.Parse("192.168.1.100.1.1"), "192.168.1.100");

    [Fact]
    public void WhenAcquiredTwice_ThenRouterIsCreatedOnceAndRouteAddedEachTime()
    {
        // Arrange
        var factoryCount = 0;
        var router = new FakeRouter();
        var pool = new EmbeddedRouterPool(_ => { factoryCount++; return router; });

        // Act
        var lease1 = pool.Acquire(router.LocalNetId, CreateRoute());
        var lease2 = pool.Acquire(router.LocalNetId, CreateRoute());

        // Assert
        Assert.Equal(1, factoryCount);
        Assert.Equal(2, router.AddRouteCount);

        lease1.Dispose();
        lease2.Dispose();
    }

    [Fact]
    public void WhenAcquired_ThenLeaseExposesRouterLoopbackPortAndLocalNetId()
    {
        // Arrange
        var router = new FakeRouter();
        var pool = new EmbeddedRouterPool(_ => router);

        // Act
        var lease = pool.Acquire(router.LocalNetId, CreateRoute());

        // Assert
        Assert.Equal(router.LoopbackPort, lease.LoopbackPort);
        Assert.Equal(router.LocalNetId, lease.LocalNetId);

        lease.Dispose();
    }

    [Fact]
    public void WhenAllLeasesDisposed_ThenRouterIsDisposedAndNextAcquireCreatesFreshRouter()
    {
        // Arrange
        var factoryCount = 0;
        var routers = new List<FakeRouter>();
        var pool = new EmbeddedRouterPool(_ =>
        {
            factoryCount++;
            var router = new FakeRouter();
            routers.Add(router);
            return router;
        });

        // Act
        var lease1 = pool.Acquire(AmsNetId.Parse("10.0.0.1.1.1"), CreateRoute());
        var lease2 = pool.Acquire(AmsNetId.Parse("10.0.0.1.1.1"), CreateRoute());
        lease1.Dispose();
        lease2.Dispose();
        var lease3 = pool.Acquire(AmsNetId.Parse("10.0.0.1.1.1"), CreateRoute());

        // Assert
        Assert.Equal(2, factoryCount);
        Assert.Equal(1, routers[0].DisposeCount);
        Assert.Equal(0, routers[1].DisposeCount);

        lease3.Dispose();
        Assert.Equal(1, routers[1].DisposeCount);
    }

    [Fact]
    public void WhenLeaseDisposedTwice_ThenRouterIsNotOverReleased()
    {
        // Arrange
        var router = new FakeRouter();
        var pool = new EmbeddedRouterPool(_ => router);
        var lease1 = pool.Acquire(router.LocalNetId, CreateRoute());
        var lease2 = pool.Acquire(router.LocalNetId, CreateRoute());

        // Act - dispose the first lease twice; the second lease still holds a reference
        lease1.Dispose();
        lease1.Dispose();

        // Assert - over-decrement would have disposed the router while lease2 is active
        Assert.Equal(0, router.DisposeCount);

        lease2.Dispose();
        Assert.Equal(1, router.DisposeCount);
    }
}
