using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Opc.Ua;
using Opc.Ua.Client;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionHealthMonitorTests
{
    [Fact]
    public void WhenItemIsNotCreated_ThenIsUnhealthyReturnsTrue()
    {
        // Arrange
        var item = new MonitoredItem(NullTelemetryContext.Instance);

        // Act
        var isUnhealthy = SubscriptionHealthMonitor.IsUnhealthy(item);

        // Assert
        Assert.True(isUnhealthy);
    }

    [Fact]
    public void WhenItemIsCreatedWithGoodStatus_ThenIsUnhealthyReturnsFalse()
    {
        // Arrange
        var item = CreateMonitoredItem(StatusCodes.Good, created: true);

        // Act
        var isUnhealthy = SubscriptionHealthMonitor.IsUnhealthy(item);

        // Assert
        Assert.False(isUnhealthy);
    }

    [Fact]
    public void WhenItemHasBadStatus_ThenIsUnhealthyReturnsTrue()
    {
        // Arrange
        var item = CreateMonitoredItem(StatusCodes.BadNodeIdUnknown, created: true);

        // Act
        var isUnhealthy = SubscriptionHealthMonitor.IsUnhealthy(item);

        // Assert
        Assert.True(isUnhealthy);
    }

    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadNodeIdInvalid)]
    [InlineData(StatusCodes.BadAttributeIdInvalid)]
    [InlineData(StatusCodes.BadIndexRangeInvalid)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    [InlineData(StatusCodes.BadNotWritable)]
    [InlineData(StatusCodes.BadWriteNotSupported)]
    public void WhenItemHasPermanentBadStatus_ThenIsRetryableReturnsFalse(uint statusCode)
    {
        // Arrange
        var item = CreateMonitoredItem(statusCode, created: true);

        // Act
        var isRetryable = SubscriptionHealthMonitor.IsRetryable(item);

        // Assert
        Assert.False(isRetryable);
    }

    [Theory]
    [InlineData(StatusCodes.BadTooManyMonitoredItems)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadCommunicationError)]
    // Access-scoped codes stay retryable: a server-side permission or AccessLevel change can make
    // the same monitored item succeed without a reconnect, and the health monitor is what notices.
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotReadable)]
    [InlineData(StatusCodes.BadNotImplemented)]
    public void WhenItemHasTransientBadStatus_ThenIsRetryableReturnsTrue(uint statusCode)
    {
        // Arrange
        var item = CreateMonitoredItem(statusCode, created: true);

        // Act
        var isRetryable = SubscriptionHealthMonitor.IsRetryable(item);

        // Assert
        Assert.True(isRetryable);
    }

    [Fact]
    public void WhenItemHasGoodStatus_ThenIsRetryableReturnsFalse()
    {
        // Arrange
        var item = CreateMonitoredItem(StatusCodes.Good, created: true);

        // Act
        var isRetryable = SubscriptionHealthMonitor.IsRetryable(item);

        // Assert
        Assert.False(isRetryable);
    }

    [Fact]
    public async Task WhenApplyChangesThrows_ThenSourceReportsFailureOnceAndKeepsItAfterRecovery()
    {
        // Arrange
        await using var source = CreateClientSource();
        var error = new InvalidOperationException("subscription heal failed");
        var reportCount = 0;

        void ReportError(Exception exception)
        {
            Interlocked.Increment(ref reportCount);
            source.ReportBackgroundError(exception);
        }

        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(CreateMonitoredItem(StatusCodes.BadTimeout, created: true));

        var monitor = new SubscriptionHealthMonitor(
            NullLogger.Instance,
            ReportError,
            applyChangesAsync: (_, _) => Task.FromException(error));

        // Act
        await monitor.CheckAndHealSubscriptionsAsync([subscription], CancellationToken.None);

        // Assert
        Assert.Equal(1, Volatile.Read(ref reportCount));
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    private static MonitoredItem CreateMonitoredItem(uint statusCode, bool created)
    {
        var item = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("ns=2;s=TestNode"),
            AttributeId = Opc.Ua.Attributes.Value,
            DisplayName = "TestItem"
        };

        if (created)
        {
            // Setting ServerId makes Status.Id != 0, which flips Created to true.
            item.ServerId = 1;
        }

        if (statusCode != StatusCodes.Good)
        {
            item.SetError(new ServiceResult(statusCode));
        }

        return item;
    }
}
