using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// A custom WebApplicationFactory that uses Kestrel instead of TestServer.
/// Playwright requires a real HTTP endpoint to connect to.
/// Based on: https://danieldonbavand.com/2022/06/13/using-playwright-with-the-webapplicationfactory-to-test-a-blazor-application/
/// </summary>
/// <typeparam name="TProgram">The entry point class (usually Program)</typeparam>
public class WebTestingHostFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private IHost? _kestrelHost;
    private string? _serverAddress;

    public string ServerAddress
    {
        get
        {
            EnsureServer();
            return _serverAddress!;
        }
    }

    /// <summary>
    /// The services of the Kestrel host that serves the tests. This is a different container from
    /// <see cref="WebApplicationFactory{TProgram}.Services"/>, which belongs to the TestServer host
    /// the base class requires and which no request ever reaches.
    /// </summary>
    public IServiceProvider ServerServices
    {
        get
        {
            EnsureServer();
            return _kestrelHost!.Services;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseUrls("http://127.0.0.1:0");
        builder.UseEnvironment("Development");

        // Use test-specific root configuration to avoid loading HomeBlaze's Data folder
        builder.UseSetting("HomeBlaze:RootConfigFile", "testRoot.json");

        // Point to test-specific plugin configuration
        builder.UseSetting("PluginConfigurationPath", Path.Combine(AppContext.BaseDirectory, "TestData", "Plugins.json"));
    }

    private void EnsureServer()
    {
        if (_serverAddress is null)
        {
            // Force the server to start by accessing Services
            _ = Services;
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Set CWD to the test output directory so relative paths in test configuration
        // (testRoot.json, ./TestData) resolve correctly regardless of where dotnet test is run from.
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        // Create the standard TestServer host (required by base class)
        var testHost = builder.Build();

        // Reconfigure the builder to use Kestrel instead
        builder.ConfigureWebHost(webHostBuilder =>
            webHostBuilder.UseKestrel());

        // Build and start the Kestrel host
        _kestrelHost = builder.Build();

        // TypeProvider is deliberately not populated here. The entry point already registers every
        // assembly this factory used to add, and loads the same plugins, against the same singleton.
        // Doing it again from this thread while the entry point runs on its own put two writers into
        // one provider at once, which is what made the E2E fixture fail to initialise.

        _kestrelHost.Start();

        // Get the address from Kestrel
        var server = _kestrelHost.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();

        _serverAddress = addresses!.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not determine Kestrel server address");

        if (!_serverAddress.EndsWith('/'))
            _serverAddress += '/';

        // Start the TestServer host to satisfy the base class
        testHost.Start();
        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Give the host time to stop gracefully, including OPC UA server shutdown
            try
            {
                _kestrelHost?.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore timeout exceptions during shutdown
            }
            _kestrelHost?.Dispose();
        }
        base.Dispose(disposing);
    }
}
