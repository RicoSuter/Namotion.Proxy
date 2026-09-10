using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Opc.Ua.Client;
using Xunit.Abstractions;
using Xunit.Extensions.AssemblyFixture;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Covers what the OPC UA client reports about itself without a server on the other end.
/// </summary>
public class OpcUaClientDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree plus the defaults a fresh <c>SourceMetrics</c> and a
    /// null session manager report, not behavioural coverage.
    /// </summary>
    [Fact]
    public async Task WhenNeverConnected_ThenEveryGetterAnswersWithoutThrowing()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        var diagnostics = source.Diagnostics;
        NodeId? sessionId = diagnostics.SessionId;

        // Assert
        Assert.Null(diagnostics.IsOperational);
        Assert.Null(diagnostics.OperationalChangeTime);
        Assert.Null(diagnostics.LastError);
        Assert.Null(diagnostics.StartTime);
        Assert.False(diagnostics.IsReconnecting);
        Assert.Null(sessionId);
        Assert.Equal(0, diagnostics.SubscriptionCount);
        Assert.Equal(0, diagnostics.MonitoredItemCount);
        Assert.Equal(0, diagnostics.ClaimedPropertyCount);
        Assert.Null(diagnostics.Polling);
        Assert.Null(diagnostics.ReadAfterWrite);
        Assert.Equal(0, diagnostics.Reconnects.TotalAttempts);
        Assert.Null(diagnostics.Reconnects.LastConnectionTime);
    }

    [Fact]
    public async Task WhenCurrentSessionChanges_ThenSessionFacadesFollowItAndReuseCachedObjects()
    {
        // Arrange
        await using var source = CreateClientSource();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var sessionManager = new SessionManager(
            source,
            propertyWriter,
            CreateConfiguration(),
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            NullLogger.Instance);
        await AsyncTestHelpers.WaitUntilAsync(
            () => sessionManager.PollingManager!.IsRunning,
            message: "The polling manager should start with the session manager");
        SetSessionManager(source, sessionManager);

        // Act
        var pollingBeforeSession = source.Diagnostics.Polling;
        var readAfterWriteBeforeSession = source.Diagnostics.ReadAfterWrite;

        // Assert
        Assert.Null(pollingBeforeSession);
        Assert.Null(readAfterWriteBeforeSession);

        PollingDiagnostics polling;
        ReadAfterWriteDiagnostics readAfterWrite;
        var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));

        try
        {
            // Act
            SetCurrentSession(sessionManager, session);
            polling = source.Diagnostics.Polling!;
            readAfterWrite = source.Diagnostics.ReadAfterWrite!;

            // Assert
            Assert.NotNull(polling);
            Assert.NotNull(readAfterWrite);
            Assert.Same(sessionManager.PollingDiagnostics, polling);
            Assert.Same(sessionManager.ReadAfterWriteDiagnostics, readAfterWrite);
        }
        finally
        {
            // Act
            SetCurrentSession(sessionManager, null);
        }

        // Assert
        Assert.Null(source.Diagnostics.Polling);
        Assert.Null(source.Diagnostics.ReadAfterWrite);

        try
        {
            // Act
            SetCurrentSession(sessionManager, session);
            var pollingAfterRestore = source.Diagnostics.Polling;
            var readAfterWriteAfterRestore = source.Diagnostics.ReadAfterWrite;

            // Assert
            Assert.Same(polling, pollingAfterRestore);
            Assert.Same(readAfterWrite, readAfterWriteAfterRestore);
        }
        finally
        {
            // Act
            SetCurrentSession(sessionManager, null);
        }

        // Act
        await sessionManager.DisposeAsync();
        var pollingAfterDisposal = source.Diagnostics.Polling;
        var readAfterWriteAfterDisposal = source.Diagnostics.ReadAfterWrite;

        // Assert
        Assert.Null(pollingAfterDisposal);
        Assert.Null(readAfterWriteAfterDisposal);
    }

    [Fact]
    public async Task WhenStorageLocksAreHeld_ThenCountDiagnosticsRemainNonBlocking()
    {
        // Arrange
        await using var source = CreateClientSource();
        var propertyWriter = new SubjectPropertyWriter(source, NullLogger.Instance);
        await using var sessionManager = new SessionManager(
            source,
            propertyWriter,
            CreateConfiguration(),
            source.PollingMetrics,
            source.ReadAfterWriteMetrics,
            NullLogger.Instance);
        await AsyncTestHelpers.WaitUntilAsync(
            () => sessionManager.PollingManager!.IsRunning,
            message: "The polling manager should start with the session manager");
        SetSessionManager(source, sessionManager);

        var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetCurrentSession(sessionManager, session);
        try
        {
            var pollingDiagnostics = source.Diagnostics.Polling!;
            var cases = new (object Owner, string FieldName, Func<int> Getter)[]
            {
                (sessionManager.SubscriptionManager, "_subscriptions", () => source.Diagnostics.SubscriptionCount),
                (sessionManager.SubscriptionManager, "_monitoredItems", () => source.Diagnostics.MonitoredItemCount),
                (sessionManager.PollingManager!, "_pollingItems", () => pollingDiagnostics.ItemCount)
            };

            // Act
            var (completedWhileLocked, results) = await ReadCountsWhileDictionaryLocksAreHeldAsync(cases);

            // Assert
            Assert.True(completedWhileLocked, "A diagnostics count getter waited for a ConcurrentDictionary lock.");
            Assert.Equal([0, 0, 0], results);
        }
        finally
        {
            SetCurrentSession(sessionManager, null);
        }
    }

    [Fact]
    public async Task WhenReadThroughTheSourceBase_ThenItIsTheSameDiagnosticsObject()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        var throughTheBase = ((SubjectSourceBase)source).Diagnostics;

        // Assert - a covariant override rather than a second, unrelated view.
        Assert.Same(source.Diagnostics, throughTheBase);
    }

    [Fact]
    public async Task WhenThroughputCountersAreWritten_ThenDiagnosticsReadTheSameCounters()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        source.IncomingThroughput.Add(60);
        source.OutgoingThroughput.Add(120);
        var throughput = source.Diagnostics.Throughput;

        // Assert
        Assert.Equal(1.0, throughput.IncomingPerSecond!.Value);
        Assert.Equal(2.0, throughput.OutgoingPerSecond!.Value);
    }

    [Fact]
    public async Task WhenTheSessionBecomesHealthy_ThenLivenessRisesAndItsTimestampIsStamped()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act
        source.NotifySessionHealthy();

        // Assert
        Assert.True(source.Diagnostics.IsOperational);
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenLivenessFallsAndItsTimestampMoves()
    {
        // Arrange
        await using var source = CreateClientSource();
        source.NotifySessionHealthy();
        var upAt = source.Diagnostics.OperationalChangeTime;

        // Act
        WaitForClockTick();
        source.NotifyConnectionLost();

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
        Assert.True(source.Diagnostics.OperationalChangeTime > upAt);
    }

    /// <summary>
    /// Runs the real client rather than a <see cref="SourceMetrics"/> instance of its own, so it
    /// fails if the retry loop stops writing to the shared metrics.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenTheClientCannotConnectAndLaterRecovers_ThenLastErrorSurvives()
    {
        // Arrange - a port nothing is listening on, so the connect fails inside the retry loop, which
        // swallows the exception rather than letting the base class see it.
        await using var source = CreateClientSource(configuration: CreateConfiguration(
            serverUrl: $"opc.tcp://localhost:{GetFreeTcpPort()}",
            certificateStoreBasePath: "pki-client-diagnostics"));

        // Act
        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.LastError is not null,
                timeout: TimeSpan.FromSeconds(60),
                message: "A client that cannot reach its server should report the failure.");

            // The recorded failure is the connect itself, not a configuration the client rejected
            // before it reached the wire.
            var error = source.Diagnostics.LastError;
            Assert.Contains("Failed to discover OPC UA endpoints", error!.Message);

            // Sticky: recovering must not erase the only evidence the failure happened.
            source.NotifySessionHealthy();

            // Assert
            Assert.Same(error, source.Diagnostics.LastError);
            Assert.True(source.Diagnostics.IsOperational);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenPropertiesAreClaimed_ThenClaimedPropertyCountFollowsTheOwnershipManager()
    {
        // Arrange
        await using var source = CreateClientSource();
        var property = ((TestRoot)source.RootSubject).GetPropertyReference(nameof(TestRoot.Name));

        // Act
        var claimed = source.Ownership.ClaimSource(property);
        var whileClaimed = source.Diagnostics.ClaimedPropertyCount;
        source.Ownership.ReleaseSource(property);
        var afterRelease = source.Diagnostics.ClaimedPropertyCount;

        // Assert
        Assert.True(claimed);
        Assert.Equal(1, whileClaimed);
        Assert.Equal(0, afterRelease);
    }

    [Fact]
    public async Task WhenDisposedWhileHostedExecutionIsActive_ThenCleanupWaitsForExecutionToExit()
    {
        // Arrange
        await using var source = CreateClientSource();
        var property = ((TestRoot)source.RootSubject).GetPropertyReference(nameof(TestRoot.Name));
        Assert.True(source.Ownership.ClaimSource(property));

        await using var executionGate = HostedExecutionGate.Install(source);
        await executionGate.Started.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        var disposal = source.DisposeAsync().AsTask();
        try
        {
            await executionGate.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, source.Ownership.Count);
        }
        finally
        {
            executionGate.AllowExit();
            await disposal;
        }

        Assert.Equal(0, source.Ownership.Count);
        Assert.Null(source.Diagnostics.LastError);
    }

    [Fact]
    public async Task WhenReconnectionsAreRecorded_ThenTheReconnectBlockReportsThem()
    {
        // Arrange
        await using var source = CreateClientSource();

        // Act - distinct counts per outcome, so a getter wired to the wrong counter is caught.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            source.ReconnectionMetrics.RecordAttemptStart();
        }

        source.ReconnectionMetrics.RecordSuccess();
        source.ReconnectionMetrics.RecordFailure();
        source.ReconnectionMetrics.RecordFailure();
        source.ReconnectionMetrics.RecordAbandoned();
        source.ReconnectionMetrics.RecordAbandoned();
        source.ReconnectionMetrics.RecordAbandoned();

        // Assert
        var reconnects = source.Diagnostics.Reconnects;
        Assert.Equal(6, reconnects.TotalAttempts);
        Assert.Equal(1, reconnects.TotalSucceeded);
        Assert.Equal(2, reconnects.TotalFailed);
        Assert.Equal(3, reconnects.TotalAbandoned);
        Assert.NotNull(reconnects.LastConnectionTime);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void SetSessionManager(OpcUaSubjectClientSource source, SessionManager sessionManager) =>
        typeof(OpcUaSubjectClientSource)
            .GetField("_sessionManager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(source, sessionManager);

    private static void SetCurrentSession(SessionManager sessionManager, Session? session) =>
        typeof(SessionManager)
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sessionManager, session);

    private static async Task<(bool CompletedWhileLocked, int[] Results)> ReadCountsWhileDictionaryLocksAreHeldAsync(
        (object Owner, string FieldName, Func<int> Getter)[] cases)
    {
        using var locksAcquired = new CountdownEvent(cases.Length);
        using var releaseLocks = new ManualResetEventSlim(false);
        using var gettersStarted = new CountdownEvent(cases.Length);
        using var gettersCompleted = new CountdownEvent(cases.Length);

        // Every holder blocks while holding a lock and every getter then blocks on one of those
        // locks, so up to twice the case count is parked at once. The pool cannot promise that many
        // threads on a low core agent, and a holder that is never scheduled hangs locksAcquired.
        var lockHolders = cases
            .Select(testCase => DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
            {
                lock (GetFirstConcurrentDictionaryLock(testCase.Owner, testCase.FieldName))
                {
                    locksAcquired.Signal();
                    if (!releaseLocks.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The test did not release a ConcurrentDictionary lock.");
                    }
                }
            }))
            .ToArray();

        Task<int>[] getterTasks = [];
        bool completedWhileLocked;
        try
        {
            Assert.True(locksAcquired.Wait(TimeSpan.FromSeconds(5)), "The dictionary locks were not acquired.");
            getterTasks = cases
                .Select(testCase => DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() =>
                {
                    gettersStarted.Signal();
                    try
                    {
                        return testCase.Getter();
                    }
                    finally
                    {
                        gettersCompleted.Signal();
                    }
                }))
                .ToArray();

            Assert.True(gettersStarted.Wait(TimeSpan.FromSeconds(5)), "The diagnostics getters did not start.");
            completedWhileLocked = gettersCompleted.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseLocks.Set();
            await Task.WhenAll(lockHolders);
            await Task.WhenAll(getterTasks);
        }

        return (completedWhileLocked, getterTasks.Select(task => task.Result).ToArray());
    }

    private static object GetFirstConcurrentDictionaryLock(object owner, string fieldName)
    {
        var dictionary = owner.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        var tables = dictionary.GetType()
            .GetField("_tables", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dictionary)!;
        return ((object[])tables.GetType()
            .GetField("_locks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tables)!)[0];
    }

    /// <summary>
    /// Spins until the wall clock reports a new tick, so a timestamp stamped after this call cannot
    /// land on the same value as one stamped before it. A condition rather than a fixed delay,
    /// because the clock resolution is coarse on Windows.
    /// </summary>
    private static void WaitForClockTick()
    {
        var start = DateTimeOffset.UtcNow.UtcTicks;

        SpinWait spin = default;
        while (DateTimeOffset.UtcNow.UtcTicks == start)
        {
            spin.SpinOnce();
        }
    }
}

/// <summary>
/// Covers session-backed diagnostics against the shared OPC UA server.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaClientSessionDiagnosticsTests : IAssemblyFixture<SharedOpcUaServerFixture>
{
    private readonly SharedOpcUaServerFixture _serverFixture;
    private readonly ITestOutputHelper _output;

    public OpcUaClientSessionDiagnosticsTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
    {
        _serverFixture = serverFixture;
        _output = output;
    }

    [Fact]
    public async Task WhenConnected_ThenSessionIdIsTheCurrentSessionNodeId()
    {
        // Arrange
        await using var client = await _serverFixture.CreateClientAsync(new TestLogger(_output));
        var source = client.Source!;

        // Act
        NodeId? sessionId = source.Diagnostics.SessionId;

        // Assert
        Assert.Same(source.CurrentSession!.SessionId, sessionId);
    }
}
