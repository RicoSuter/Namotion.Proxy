using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Disposal of a <see cref="SubscriptionManager"/> is terminal: the owning session manager disposes
/// at most once and never replaces its subscription manager. The shutdown flag that suppresses
/// inbound data change callbacks must therefore be monotonic, so a reconnect racing disposal cannot
/// resume callbacks on a disposed manager.
/// </summary>
public class SubscriptionManagerShutdownTests
{
    [Fact]
    public async Task WhenDisposedManagerRunsSubscriptionSetup_ThenCallbacksStaySuppressed()
    {
        // Arrange
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };

        var subject = new TestPerson(InterceptorSubjectContext.Create().WithLifecycle());
        var source = new OpcUaSubjectClientSource(subject, configuration, NullLogger.Instance);
        var manager = new SubscriptionManager(
            source,
            new SubjectPropertyWriter(source, NullLogger.Instance),
            pollingManager: null,
            readAfterWriteManager: null,
            configuration,
            source.ReportBackgroundError,
            NullLogger.Instance);

        await manager.DisposeAsync();

        // Act - a reconnect that starts after disposal runs subscription setup again. Passing no
        // monitored items makes the null session safe: the batching loop that dereferences the
        // session never executes for an empty item list.
        await manager.CreateBatchedSubscriptionsAsync([], null!, CancellationToken.None);

        // Assert
        Assert.True(manager.AreCallbacksSuppressedForTesting);
    }
}
