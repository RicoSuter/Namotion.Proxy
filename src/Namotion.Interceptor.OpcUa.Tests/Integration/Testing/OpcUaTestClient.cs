using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

public class OpcUaTestClient<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultServerUrl = "opc.tcp://localhost:4840";

    private readonly TestLogger _logger;
    private readonly Action<OpcUaClientConfiguration>? _configureClient;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;
    private int _disposed; // 0 = not disposed, 1 = disposed

    public TRoot? Root { get; private set; }

    public IInterceptorSubjectContext Context => _context ?? throw new InvalidOperationException("Client not started.");

    public IOpcUaSubjectClientSource? Source { get; private set; }

    public OpcUaTestClient(TestLogger logger, Action<OpcUaClientConfiguration>? configureClient = null)
    {
        _logger = logger;
        _configureClient = configureClient;
    }

    // waitForInitialSync = false returns as soon as the host is running instead of waiting for
    // subscriptions and the first property sync. Tests that have to interfere with the very first
    // load need this: that load is what the readiness wait waits for, so waiting here would mean
    // waiting for the thing the test is about to break.
    public async Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Func<TRoot, bool> isConnected,
        string serverUrl = DefaultServerUrl,
        string? certificateStoreBasePath = null,
        bool waitForInitialSync = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var builder = Host.CreateApplicationBuilder();

        // Reduce shutdown timeout for faster test cleanup
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, "Client", LogLevel.Information);
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithSourceTransactions()
            .WithHostedServices(builder.Services);

        Root = createRoot(_context);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<TRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                var config = new OpcUaClientConfiguration
                {
                    ServerUrl = serverUrl,
                    RootPath = ["Root"],
                    TypeResolver = new OpcUaTypeResolver(sp.GetRequiredService<ILogger<OpcUaTypeResolver>>()),
                    ValueConverter = new OpcUaValueConverter(),
                    SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
                    TelemetryContext = telemetryContext,

                    ReconnectInterval = TimeSpan.FromSeconds(5),
                    ReconnectHandlerTimeout = TimeSpan.FromSeconds(5),
                    MaxReconnectDuration = TimeSpan.FromSeconds(15),
                    SubscriptionHealthCheckInterval = TimeSpan.FromSeconds(5),

                    // SessionTimeout must be >= server's MinSessionTimeout (10s), use 30s for margin
                    SessionTimeout = TimeSpan.FromSeconds(30),
                    KeepAliveInterval = TimeSpan.FromSeconds(5),
                    OperationTimeout = TimeSpan.FromSeconds(30),

                    BufferTime = TimeSpan.FromMilliseconds(100),

                    CertificateStoreBasePath = certificateStoreBasePath ?? "pki"
                };

                // Allow tests to override configuration
                _configureClient?.Invoke(config);

                return config;
            });

        _host = builder.Build();

        Source = _host.Services.GetRequiredService<IOpcUaSubjectClientSource>();

        await _host.StartAsync();
        _logger.Log($"Client host started in {sw.ElapsedMilliseconds}ms");

        if (!waitForInitialSync)
        {
            sw.Stop();
            return;
        }

        // First wait for OPC UA infrastructure (subscriptions set up) - this is reliable
        // because it's based on actual OPC UA state, not property propagation
        await AsyncTestHelpers.WaitUntilAsync(
            () => Source.Diagnostics.MonitoredItemCount > 0,
            timeout: TimeSpan.FromSeconds(60),
            message: "Client failed to create subscriptions");

        // Then wait actual connected
        // WaitUntilAsync includes memory barrier to ensure visibility across threads
        await AsyncTestHelpers.WaitUntilAsync(
            () => Root != null && isConnected(Root),
            timeout: TimeSpan.FromSeconds(60),
            message: "Client failed to sync initial property values");

        sw.Stop();
        _logger.Log($"Client connected in {sw.ElapsedMilliseconds}ms total");
    }

    public async Task StopAsync()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await host.StopAsync(TimeSpan.FromMinutes(5));
            }
            finally
            {
                host.Dispose();
            }

            // Wait for OPC UA session to fully close
            await Task.Delay(250);

            sw.Stop();
            _logger.Log($"Client stopped in {sw.ElapsedMilliseconds}ms");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // Already disposed
        }

        try
        {
            var host = Interlocked.Exchange(ref _host, null);
            if (host != null)
            {
                await host.StopAsync(TimeSpan.FromMinutes(5));
                host.Dispose();

                // Wait for OPC UA session to fully close
                await Task.Delay(250);

                _logger.Log("Client disposed");
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Error disposing client: {ex.Message}");
        }
    }
}
