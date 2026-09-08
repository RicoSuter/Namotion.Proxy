using System.Reflection;
using HueApi.BridgeLocator;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeClientLifecycleTests
{
    [Fact]
    public async Task WhenUsedClientIsReset_ThenRetryUsesFreshHttpClient()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.AppKey = "test-key";
        SetPrivateField(bridge, "_bridge", new LocatedBridge("test-bridge-id", "127.0.0.1", null));
        bridge.IsConnected = true; // the connection loop's precondition for serving operations

        var firstClient = bridge.GetOrCreateClient();
        var firstHttpClient = GetPrivateField<HttpClient>(bridge, "_httpClient");
        using var request = new HttpRequestMessage(HttpMethod.Get, "clip/v2/resource/device");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstHttpClient.SendAsync(request, new CancellationToken(canceled: true)));

        // Act
        bridge.ResetClient(firstClient);
        var secondClient = bridge.GetOrCreateClient();
        var secondHttpClient = GetPrivateField<HttpClient>(bridge, "_httpClient");

        // Assert
        Assert.NotSame(firstClient, secondClient);
        Assert.NotSame(firstHttpClient, secondHttpClient);

        // The identity assertions alone hold whether or not the old HttpClient was released: a reset
        // that only nulled the field would still yield a fresh one. Releasing it is the point.
        Assert.Throws<ObjectDisposedException>(() => firstHttpClient.Timeout = TimeSpan.FromSeconds(5));

        bridge.ResetClient(secondClient);
    }

    [Fact]
    public void WhenAStaleClientIsReset_ThenTheLiveOneIsUntouched()
    {
        // Arrange - ResetClient is called from the connection loop, from Dispose and after a failure,
        // so it can be handed a client that has already been replaced.
        var bridge = TestHelpers.CreateTestBridge();
        bridge.AppKey = "test-key";
        SetPrivateField(bridge, "_bridge", new LocatedBridge("test-bridge-id", "127.0.0.1", null));
        bridge.IsConnected = true; // the connection loop's precondition for serving operations

        var stale = bridge.GetOrCreateClient();
        bridge.ResetClient(stale);
        var live = bridge.GetOrCreateClient();
        var liveHttpClient = GetPrivateField<HttpClient>(bridge, "_httpClient");

        // Act
        bridge.ResetClient(stale);

        // Assert - the live client survives, still usable.
        Assert.Same(live, bridge.GetOrCreateClient());
        liveHttpClient.Timeout = TimeSpan.FromSeconds(5);

        bridge.ResetClient(live);
    }

    [Fact]
    public void WhenTheBridgeIsDisposed_ThenNoFurtherClientCanBeBuilt()
    {
        // Arrange - an operation can be in flight while the host shuts the bridge down. Without a
        // disposed flag, GetOrCreateClient happily built a replacement that nothing was left to
        // release, so the HttpClient leaked for the lifetime of the process.
        var bridge = TestHelpers.CreateTestBridge();
        bridge.AppKey = "test-key";
        SetPrivateField(bridge, "_bridge", new LocatedBridge("test-bridge-id", "127.0.0.1", null));
        bridge.IsConnected = true; // the connection loop's precondition for serving operations

        var client = bridge.GetOrCreateClient();
        var httpClient = GetPrivateField<HttpClient>(bridge, "_httpClient");

        // Act
        bridge.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => httpClient.Timeout = TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => bridge.GetOrCreateClient());
        Assert.NotNull(client);
    }

    private static T GetPrivateField<T>(HueBridge bridge, string fieldName) where T : class
    {
        return (T)(typeof(HueBridge)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(bridge)
            ?? throw new InvalidOperationException($"Field '{fieldName}' has no value."));
    }

    private static void SetPrivateField<T>(HueBridge bridge, string fieldName, T value)
    {
        var field = typeof(HueBridge).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(bridge, value);
    }

    [Fact]
    public void WhenTheBridgeIsNotConnected_ThenAnOperationIsRefusedRatherThanHalfWorking()
    {
        // Arrange - discovery can keep failing while the bridge is still reachable at its last known
        // address. An operation that built its own client then physically succeeded, but nothing was
        // streaming or polling, so every derived value stayed at its pre-command reading indefinitely.
        var bridge = TestHelpers.CreateTestBridge();
        bridge.AppKey = "test-key";
        SetPrivateField(bridge, "_bridge", new LocatedBridge("test-bridge-id", "127.0.0.1", null));

        // Act & Assert
        Assert.False(bridge.IsConnected);
        Assert.Throws<InvalidOperationException>(() => bridge.GetOrCreateClient());
    }
}
