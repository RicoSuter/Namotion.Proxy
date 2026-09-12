using System.Text;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Integration.Testing;

/// <summary>
/// xUnit fixture that provides a shared ADS server for all integration tests.
/// Starts once per test collection, avoiding TCP port conflicts between tests.
/// The AMS TCP/IP router binds to a fixed port (48898) which enters TIME_WAIT
/// when disposed, so sharing a single server instance is essential for reliable tests.
/// </summary>
public class SharedAdsServerFixture : IAsyncLifetime
{
    private static readonly TestSymbol[] DefaultSymbols =
    [
        new TestSymbol("GVL.Temperature", typeof(double), 25.0),
        new TestSymbol("GVL.MachineName", typeof(string), "TestPLC"),
        new TestSymbol("GVL.WideName", typeof(string), "WidePLC", Encoding.Unicode),
        new TestSymbol("GVL.IsRunning", typeof(bool), true),
        new TestSymbol("GVL.Counter", typeof(int), 42),
        new TestSymbol("GVL.PolledCounter", typeof(int), 0),
    ];

    private AdsTestServer? _server;

    /// <summary>
    /// Gets the shared ADS test server instance.
    /// </summary>
    public AdsTestServer Server => _server ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        _server = new AdsTestServer(DefaultSymbols);
        await _server.StartAsync();
    }

    /// <summary>
    /// Resets all symbol values to their initial state.
    /// Call this at the start of each test to ensure test isolation.
    /// </summary>
    public void ResetSymbolValues()
    {
        Server.ResetSymbolValues();
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
