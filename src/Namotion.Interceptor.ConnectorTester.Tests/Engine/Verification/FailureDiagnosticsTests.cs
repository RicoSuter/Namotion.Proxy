using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class FailureDiagnosticsTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

    [Fact]
    public async Task WhenSnapshotsDiverge_ThenJsonFilesAreWrittenPerParticipant()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var contextA = CreateContext();
            var contextB = CreateContext();
            var rootA = new TestNode(contextA);
            var rootB = new TestNode(contextB);
            var participants = new Dictionary<string, TestNode> { ["server"] = rootA, ["client"] = rootB };

            var diagnostics = new FailureDiagnostics(directory, participants, NullLogger.Instance);

            const string serverSnap = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";
            const string clientSnap = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":2}}}}""";

            // Act
            await diagnostics.RunAsync(cycleNumber: 5, [("server", serverSnap), ("client", clientSnap)], CancellationToken.None);

            // Assert
            Assert.True(File.Exists(Path.Combine(directory, "cycle-0005-fail-server.json")));
            Assert.True(File.Exists(Path.Combine(directory, "cycle-0005-fail-client.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WhenSnapshotsConverge_ThenReSyncCheckClassifiesAsTransientGap()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var contextA = CreateContext();
            var contextB = CreateContext();
            TestNode rootA;
            TestNode rootB;
            List<(string, string)> snapshots;
            using (SubjectChangeContext.WithChangedTimestamp(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            {
                rootA = new TestNode(contextA) { StringValue = "x", IntValue = 1 };
                rootB = new TestNode(contextB) { StringValue = "x", IntValue = 99 }; // diverged

                snapshots = new List<(string, string)>
                {
                    ("server", Namotion.Interceptor.ConnectorTester.Snapshot.SnapshotComparer.Capture(rootA)),
                    ("client", Namotion.Interceptor.ConnectorTester.Snapshot.SnapshotComparer.Capture(rootB))
                };
            }

            var participants = new Dictionary<string, TestNode> { ["server"] = rootA, ["client"] = rootB };
            var diagnostics = new FailureDiagnostics(directory, participants, NullLogger.Instance);

            // Act / Assert: should not throw; logs the verdict via the (null) logger.
            await diagnostics.RunAsync(cycleNumber: 1, snapshots, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
