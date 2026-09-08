using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

public class OpcUaTestServer<TRoot> : IAsyncDisposable
    where TRoot : class, IInterceptorSubject
{
    private const string DefaultBaseAddress = "opc.tcp://localhost:4840/";

    private readonly TestLogger _logger;
    private IHost? _host;
    private IInterceptorSubjectContext? _context;
    private Func<IInterceptorSubjectContext, TRoot>? _createRoot;
    private Action<IInterceptorSubjectContext, TRoot>? _initializeDefaults;
    private string _baseAddress = DefaultBaseAddress;
    private string _certificateStoreBasePath = "pki";
    private TimeSpan _bufferTime = TimeSpan.FromMilliseconds(100);
    private int _disposed;

    public TRoot? Root { get; private set; }

    public IOpcUaSubjectServer? Server { get; private set; }

    public OpcUaTestServer(TestLogger logger)
    {
        _logger = logger;
    }

    public Task StartAsync(
        Func<IInterceptorSubjectContext, TRoot> createRoot,
        Action<IInterceptorSubjectContext, TRoot>? initializeDefaults = null,
        string? baseAddress = null,
        string? certificateStoreBasePath = null,
        TimeSpan? bufferTime = null)
    {
        _createRoot = createRoot;
        _initializeDefaults = initializeDefaults;
        _baseAddress = baseAddress ?? DefaultBaseAddress;
        _certificateStoreBasePath = certificateStoreBasePath ?? "pki";
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(100);

        return StartInternalAsync();
    }

    private async Task StartInternalAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var builder = Host.CreateApplicationBuilder();

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddXunit(_logger, "Server", LogLevel.Information);
        });

        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithDataAnnotationValidation()
            .WithHostedServices(builder.Services);

        Root = _createRoot!(_context);

        _initializeDefaults?.Invoke(_context, Root);

        builder.Services.AddSingleton(Root);
        builder.Services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<TRoot>(),
            sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var telemetryContext = DefaultTelemetry.Create(b =>
                    b.Services.AddSingleton(loggerFactory));

                return new OpcUaServerConfiguration
                {
                    RootName = "Root",
                    BaseAddress = _baseAddress,
                    ValueConverter = new OpcUaValueConverter(),
                    TelemetryContext = telemetryContext,
                    CleanCertificateStore = false,
                    AutoAcceptUntrustedCertificates = true,
                    CertificateStoreBasePath = _certificateStoreBasePath,

                    BufferTime = _bufferTime
                };
            });

        _host = builder.Build();

        Server = _host.Services.GetRequiredService<IOpcUaSubjectServer>();

        await _host.StartAsync();
        sw.Stop();
        _logger.Log($"Server started in {sw.ElapsedMilliseconds}ms");
    }

    public async Task StopAsync()
    {
        await StopInternalAsync();

        // Wait for TCP sockets to fully close before port can be reused
        await Task.Delay(500);
    }

    public async Task RestartAsync()
    {
        Interlocked.Exchange(ref _disposed, 0);
        _logger.Log("Restarting server...");
     
        // No delay needed for restart - same port will be reused immediately
        await StopInternalAsync();
        await StartInternalAsync();
    }

    private async Task StopInternalAsync()
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
            sw.Stop();
            _logger.Log($"Server stopped in {sw.ElapsedMilliseconds}ms");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await StopInternalAsync();

            // Wait for TCP sockets to fully close before port can be reused
            await Task.Delay(500);

            _logger.Log("Server disposed");
        }
        catch (Exception ex)
        {
            _logger.Log($"Error disposing server: {ex.Message}");
        }
    }
}
