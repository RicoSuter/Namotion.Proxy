namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Manages a pool of ports for parallel OPC UA integration tests.
/// Uses round-robin port assignment to avoid port reuse within a test run,
/// preventing TIME_WAIT socket issues.
/// </summary>
public static class OpcUaTestPortPool
{
    private const int BasePort = 4850;   // Avoid conflict with shared server on 4840
    private const int PoolSize = 100;    // Large pool to avoid port reuse
    private const int MaxParallel = 2;   // Limit parallel lifecycle tests to reduce resource contention
    private const int AcquisitionTimeoutMs = 15 * 60 * 1000; // 15 minutes

    private static readonly SemaphoreSlim Semaphore = new(MaxParallel, MaxParallel);
    private static int _nextPortIndex = -1;

    /// <summary>
    /// Acquires a port from the pool using round-robin assignment.
    /// Limits concurrent tests to MaxParallel.
    /// </summary>
    /// <returns>A PortLease that must be disposed when the test is done.</returns>
    public static async Task<PortLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(AcquisitionTimeoutMs);

        try
        {
            await Semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Failed to acquire port from pool within {AcquisitionTimeoutMs}ms. " +
                $"All {MaxParallel} parallel slots may be in use. Ensure tests dispose their PortLease properly.");
        }

        // Round-robin port assignment - each test gets a unique port
        var index = Interlocked.Increment(ref _nextPortIndex) % PoolSize;
        return new PortLease(BasePort + index);
    }

    /// <summary>
    /// Releases a parallel slot back to the pool.
    /// </summary>
    internal static void Release()
    {
        Semaphore.Release();
    }
}

/// <summary>
/// Represents a leased port that must be disposed when the test completes.
/// Cleans up certificate store on disposal to prevent accumulation.
/// </summary>
public sealed class PortLease : IDisposable
{
    private bool _disposed;

    public int Port { get; }

    public string ServerUrl => $"opc.tcp://localhost:{Port}";

    public string BaseAddress => $"opc.tcp://localhost:{Port}/";

    /// <summary>
    /// Gets the port-specific certificate store path for test isolation. Absolute, and handed to the
    /// SDK in that same form: the SDK resolves a relative store path against the working directory, so
    /// under a runner that moves it the cleanup below and the server would use different directories.
    /// </summary>
    public string CertificateStoreBasePath => Path.Combine(AppContext.BaseDirectory, $"pki-{Port}");

    internal PortLease(int port)
    {
        Port = port;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            CleanupCertificateStore();
            OpcUaTestPortPool.Release();
        }
    }

    private void CleanupCertificateStore()
    {
        try
        {
            if (Directory.Exists(CertificateStoreBasePath))
            {
                Directory.Delete(CertificateStoreBasePath, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures - OS may lock directory
            // This is non-critical as each test uses isolated directories
        }
    }
}
