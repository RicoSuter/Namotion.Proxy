using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// xUnit fixture that provides a shared OPC UA server for Category A tests.
/// Starts once per test collection, enables unlimited parallel test execution.
/// </summary>
public class SharedOpcUaServerFixture : IAsyncLifetime
{
    private const int Port = 4840;
    private const string CertificateStoreName = "pki-shared";

    private static readonly string ClientCertificateStoreRoot = Path.Combine(AppContext.BaseDirectory, "pki-clients");

    private OpcUaTestServer<SharedTestModel>? _server;
    private readonly TestLogger _logger;

    public SharedOpcUaServerFixture()
    {
        _logger = new TestLogger(new NullTestOutputHelper());
    }

    /// <summary>
    /// A no-op test output helper for when no output is available (like in fixture initialization).
    /// </summary>
    private sealed class NullTestOutputHelper : Xunit.Abstractions.ITestOutputHelper
    {
        public void WriteLine(string message) { }
        public void WriteLine(string format, params object[] args) { }
    }

    /// <summary>
    /// The server's root model with partitioned test areas.
    /// </summary>
    public SharedTestModel ServerRoot => _server?.Root ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// The resolved server instance backing the shared fixture.
    /// </summary>
    public IOpcUaSubjectServer Server => _server?.Server ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// The OPC UA server endpoint URL.
    /// </summary>
    public string ServerUrl => $"opc.tcp://localhost:{Port}";

    public async Task InitializeAsync()
    {
        // Absolute, and handed to the server in that same form. The SDK resolves a relative store path
        // against the working directory, so under a runner that moves it the wipe below and the server
        // would use different directories.
        var certificateStorePath = Path.Combine(AppContext.BaseDirectory, CertificateStoreName);

        // Stale self-signed server certificates left in bin/ from a prior run can cause
        // the SDK to reject the server on startup (no usable application certificate),
        // which blocks every shared-server test. Wipe the assembly-scoped cert store
        // before bringing the server up so each assembly run starts from a clean slate.
        WipeCertificateStore(certificateStorePath);

        // Per-client stores are named per client and nothing deletes them individually, so the whole
        // root goes here, before the first client of this assembly run creates one under it.
        WipeCertificateStore(ClientCertificateStoreRoot);

        _server = new OpcUaTestServer<SharedTestModel>(_logger);
        await _server.StartAsync(
            createRoot: context => new SharedTestModel(context),
            initializeDefaults: InitializeTestData,
            baseAddress: $"opc.tcp://localhost:{Port}/",
            certificateStoreBasePath: certificateStorePath);
    }

    private void WipeCertificateStore(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a file may be held by another process.
            // Logged so a "every shared-server test fails" symptom can be traced back to a stale cert.
            _logger.Log($"Failed to wipe cert store '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void InitializeTestData(IInterceptorSubjectContext context, SharedTestModel root)
    {
        // Set Connected flag for client connection detection
        root.Connected = true;

        // Initialize ReadWrite test areas
        root.ReadWrite = new ReadWriteTestArea(context)
        {
            BasicSync = new BasicSyncArea(context)
            {
                Name = "Initial",
                Number = 0m
            },
            ArraySync = new ArraySyncArea(context)
            {
                ScalarNumbers = [10, 20, 30, 40, 50],
                ScalarStrings = ["Server", "Test", "Array"]
            }
        };

        // Initialize Transaction test areas
        root.Transactions = new TransactionTestArea(context)
        {
            SingleProperty = new TransactionSinglePropertyArea(context)
            {
                Name = "Initial Server Value"
            },
            MultiProperty = new TransactionMultiPropertyArea(context)
            {
                Name = "Initial",
                Number = 0m
            }
        };

        // Initialize Nested structure test areas
        root.Nested = new NestedTestArea(context)
        {
            Person = new NestedPerson(context)
            {
                FirstName = "John",
                LastName = "Smith",
                Scores = [1, 2],
                Address = new NestedAddress(context) { City = "Seattle", ZipCode = "98101" }
            },
            People =
            [
                new NestedPerson(context)
                {
                    FirstName = "John",
                    LastName = "Doe",
                    Scores = [85.5, 92.3],
                    Address = new NestedAddress(context) { City = "Portland", ZipCode = "97201" }
                },
                new NestedPerson(context)
                {
                    FirstName = "Jane",
                    LastName = "Doe",
                    Scores = [88.1, 95.7],
                    Address = new NestedAddress(context) { City = "Vancouver", ZipCode = "98660" }
                }
            ],
            PeopleByName = new Dictionary<string, NestedPerson>
            {
                ["john"] = new NestedPerson(context)
                {
                    FirstName = "John",
                    LastName = "Dict",
                    Address = new NestedAddress(context) { City = "Boston", ZipCode = "02101" }
                },
                ["jane"] = new NestedPerson(context)
                {
                    FirstName = "Jane",
                    LastName = "Dict",
                    Address = new NestedAddress(context) { City = "Chicago", ZipCode = "60601" }
                }
            },
            Sensor = new NestedSensor(context)
            {
                Value = 25.5,
                Unit = "°C",
                MinValue = -40.0,
                MaxValue = 85.0
            },
            Number = 42,
            Number_Unit = "count"
        };

        // Initialize DataTypes test area
        root.DataTypes = new DataTypesTestArea(context)
        {
            BoolValue = true,
            IntValue = 42,
            LongValue = 9876543210L,
            DateTimeValue = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            ByteArray = [0x01, 0x02, 0x03]
        };

        // Initialize Collections test area
        root.Collections = new CollectionsTestArea(context)
        {
            IntArray = [1, 2, 3]
        };

        // Initialize MultiClient test area
        root.MultiClient = new MultiClientTestArea(context)
        {
            SharedValue = "initial",
            Counter = 0,
            LastWriter = null
        };
    }

    /// <summary>
    /// Creates a new client connected to the shared server.
    /// Each test should create its own client for isolation.
    /// </summary>
    public async Task<OpcUaTestClient<SharedTestModel>> CreateClientAsync(TestLogger logger)
    {
        var client = new OpcUaTestClient<SharedTestModel>(logger);
        await client.StartAsync(
            createRoot: context => new SharedTestModel(context),
            isConnected: root => root.Connected,
            serverUrl: ServerUrl,
            certificateStoreBasePath: CreateClientCertificateStorePath());
        return client;
    }

    /// <summary>
    /// Creates a new dynamic client that discovers properties from the shared server.
    /// </summary>
    public async Task<OpcUaTestClient<Dynamic.DynamicSubject>> CreateDynamicClientAsync(TestLogger logger)
    {
        var client = new OpcUaTestClient<Dynamic.DynamicSubject>(logger);
        await client.StartAsync(
            createRoot: context => new Dynamic.DynamicSubject(context),
            isConnected: root => root.TryGetRegisteredProperty(nameof(SharedTestModel.Connected))?.GetValue() is true,
            serverUrl: ServerUrl,
            certificateStoreBasePath: CreateClientCertificateStorePath());
        return client;
    }

    private static string CreateClientCertificateStorePath() =>
        Path.Combine(ClientCertificateStoreRoot, Guid.NewGuid().ToString("N"));

    public async Task DisposeAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
