using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Pins <c>OpcUaSubjectClientSource.RemoveItemsForSubject</c> to being lock-free against a client
/// that is actually connected.
/// <para>
/// <c>OpcUaSubjectLoaderFailureTests.WhenALoadFailsWhileHoldingTheStructureLock_ThenRollbackDoesNotDeadlock</c>
/// covers the same hazard from a unit fixture, but that fixture never builds a session manager, so
/// its <c>_sessionManager</c> field is null throughout. A reintroduction that guards the lock
/// acquisition on <c>_sessionManager is not null</c> therefore takes no lock there and every unit
/// test still passes, while in production the field is non-null for the whole of the load that
/// holds the structure lock. Only a real session, meaning a real server, makes that guard true, and
/// only then does the inline detach on the rollback path re-enter the non-reentrant semaphore that
/// the same thread already owns.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaLoadRollbackDeadlockTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Upper bound for the loader to reach its first collection element. Everything before that
    /// point is an ordinary connect and browse against a healthy server, so overshooting this means
    /// the scenario never set itself up rather than that the connector is slow.
    /// </summary>
    private static readonly TimeSpan StagingTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Upper bound for the interrupted load to fail and unwind. Once the server is gone the next
    /// browse fails within the operation timeout configured below, and the rollback that follows is
    /// pure in-memory work, so this is orders of magnitude more than the path needs.
    /// </summary>
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound for the connector to reconnect and complete a load after the restart.</summary>
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Backstop for the paused load, so a test that fails before it opens the gate cannot leave the
    /// connector's background loop parked for the rest of the run.
    /// </summary>
    private static readonly TimeSpan PauseTimeout = TimeSpan.FromMinutes(2);

    private const string DeadlockMessage =
        "The interrupted load never failed, so its rollback never returned. " +
        "OpcUaSubjectClientSource.RemoveItemsForSubject deadlocked the connector: it runs from the " +
        "synchronous subject-detach callback that a failed load's rollback raises inline, on the very " +
        "thread that already holds the non-reentrant _structureLock for the whole load. Taking that " +
        "lock there can never be granted, and because the thread blocks while holding the lifecycle " +
        "interceptor's attached-subject lock, every subject attach and detach in the process stalls " +
        "with it. Keep RemoveItemsForSubject lock-free.";

    public OpcUaLoadRollbackDeadlockTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenALoadOfAConnectedClientFailsAfterStagingSubjects_ThenTheConnectorRecovers()
    {
        // Arrange
        var logger = new TestLogger(_output);
        OpcUaTestServer<TestRoot>? server = null;
        OpcUaTestClient<TestRoot>? client = null;
        PortLease? port = null;

        // Pauses the load once it starts materializing collection elements, which is the window in
        // which the server has to disappear: the elements are staged right after they are created,
        // and the browse of their own nodes, the call that then fails, comes after all of them.
        var loadPause = new CollectionStagingPauseSubjectFactory();

        try
        {
            port = await OpcUaTestPortPool.AcquireAsync();

            server = new OpcUaTestServer<TestRoot>(logger);
            await server.StartAsync(
                context => new TestRoot(context),
                (context, root) =>
                {
                    root.Connected = true;
                    root.Name = "Initial";

                    // The client's People array starts empty, so every element exposed here is a
                    // subject the loader has to create and stage during the load. Those staged
                    // subjects are exactly what the rollback detaches, and the detach is the code
                    // path under test.
                    var people = new TestPerson[3];
                    for (var i = 0; i < people.Length; i++)
                    {
                        people[i] = new TestPerson(context)
                        {
                            FirstName = $"First{i}",
                            LastName = $"Last{i}"
                        };
                    }

                    root.People = people;
                },
                baseAddress: port.BaseAddress,
                certificateStoreBasePath: port.CertificateStoreBasePath);

            client = new OpcUaTestClient<TestRoot>(logger, config =>
            {
                config.SubjectFactory = loadPause;

                // Fail the browse that follows the server stop promptly, and retry the failed load
                // promptly, so the whole cycle fits comfortably inside the bounds above.
                config.OperationTimeout = TimeSpan.FromSeconds(5);
                config.RetryTime = TimeSpan.FromSeconds(2);
                config.SessionTimeout = TimeSpan.FromSeconds(10); // Minimum allowed by the server
                config.KeepAliveInterval = TimeSpan.FromSeconds(1);
            });

            // Started without the harness readiness wait: the load this test interrupts is the very
            // load that readiness waits for.
            await client.StartAsync(
                context => new TestRoot(context),
                isConnected: root => root.Connected,
                serverUrl: port.ServerUrl,
                certificateStoreBasePath: port.CertificateStoreBasePath,
                waitForInitialSync: false);

            Assert.NotNull(server.Root);
            Assert.NotNull(client.Root);
            Assert.NotNull(client.Source);

            // Counts the staged subjects the rollback actually sheds, so a scenario that quietly
            // stopped staging anything cannot pass this test without doing the work it claims to do.
            var lifecycle = client.Context.TryGetLifecycleInterceptor();
            Assert.NotNull(lifecycle);

            var detachedPeopleCount = 0;
            void OnSubjectDetaching(SubjectLifecycleChange change)
            {
                if (change.Subject is TestPerson)
                {
                    Interlocked.Increment(ref detachedPeopleCount);
                }
            }

            lifecycle.SubjectDetaching += OnSubjectDetaching;
            try
            {
                // Act: take the server away while the load holds the structure lock and has staged
                // subjects, so the browse that follows fails and the rollback runs its inline detach.
                await loadPause.StagingReached.WaitAsync(StagingTimeout);
                logger.Log("Loader reached collection staging, stopping the server underneath it");

                await server.StopAsync();
                loadPause.Resume();

                // Assert: the load unwinds. LastError is written by the catch in StartListeningAsync,
                // which only runs once the rollback and the structure lock release are both behind it,
                // so seeing it set proves the detach callback returned.
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Source!.Diagnostics.LastError is not null,
                    timeout: RollbackTimeout,
                    message: DeadlockMessage);
                logger.Log($"Load failed as expected: {client.Source!.Diagnostics.LastError?.Message}");

                Assert.True(Volatile.Read(ref detachedPeopleCount) > 0,
                    "The interrupted load did not detach any staged subject, so it never exercised the " +
                    "rollback path this test exists to guard. The server model or the pause point changed.");

                // Assert: the connector is still able to work, rather than being wedged behind a lock
                // it can never take.
                await server.RestartAsync();

                server.Root!.Name = "AfterRollback";
                await AsyncTestHelpers.WaitUntilAsync(
                    () => client.Source!.Diagnostics.MonitoredItemCount > 0 &&
                          client.Root!.Name == "AfterRollback",
                    timeout: RecoveryTimeout,
                    message: "The connector never completed a load after the rolled-back one. " + DeadlockMessage);

                Assert.Equal(3, client.Root!.People.Length);
                logger.Log("Connector recovered and reloaded the previously rolled-back subjects");
            }
            finally
            {
                lifecycle.SubjectDetaching -= OnSubjectDetaching;
            }
        }
        finally
        {
            // Opened unconditionally so a failure earlier in the test cannot leave the connector's
            // background loop parked inside the factory.
            loadPause.Resume();

            if (client is not null) await client.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
            port?.Dispose();
        }
    }

    /// <summary>
    /// Holds the loader still the first time it materializes a collection element, so the test can
    /// remove the server underneath a load that is about to stage subjects. Only the first element
    /// pauses: staging happens immediately after each element is created and the browse of the
    /// elements' own nodes comes after all of them, so one pause is enough for the failure to land
    /// with the whole collection staged.
    /// </summary>
    private sealed class CollectionStagingPauseSubjectFactory : OpcUaSubjectFactory
    {
        private readonly TaskCompletionSource _stagingReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CollectionStagingPauseSubjectFactory()
            : base(DefaultSubjectFactory.Instance)
        {
        }

        /// <summary>Completes once the loader is inside the first collection element creation.</summary>
        public Task StagingReached => _stagingReached.Task;

        /// <summary>Lets the paused load continue. Safe to call more than once.</summary>
        public void Resume() => _resume.TrySetResult();

        public override async Task<IInterceptorSubject> CreateCollectionSubjectAsync(
            RegisteredSubjectProperty collectionProperty,
            ReferenceDescription node,
            object? index,
            ISession session,
            CancellationToken cancellationToken)
        {
            var subject = await base.CreateCollectionSubjectAsync(
                collectionProperty, node, index, session, cancellationToken);

            if (_stagingReached.TrySetResult())
            {
                await _resume.Task.WaitAsync(PauseTimeout, cancellationToken);
            }

            return subject;
        }
    }
}
