using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

public class WebSocketTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public TRoot? Root { get; private set; }

    public IInterceptorSubjectContext? Context { get; private set; }

    /// <summary>The client source under test, resolved from the host's hosted services.</summary>
    public WebSocketSubjectClientSource? Source =>
        _host?.Services.GetServices<IHostedService>().OfType<WebSocketSubjectClientSource>().FirstOrDefault();

    public WebSocketTestClient(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool>? isConnected = null,
        int port = 18080,
        Action<IInterceptorSubjectContext>? configureContext = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddConsole();
        });

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        configureContext?.Invoke(context);

        Context = context;
        Root = createRoot(context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddWebSocketSubjectClientSource<TRoot>(configuration =>
        {
            configuration.ServerUri = new Uri($"ws://localhost:{port}/ws");
        });

        _host = builder.Build();
        await _host.StartAsync();

        // Wait for connection and initial sync (if caller provides a predicate)
        if (isConnected != null)
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => isConnected(Root),
                timeout: TimeSpan.FromSeconds(10),
                message: "Client should establish connection");
        }
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
