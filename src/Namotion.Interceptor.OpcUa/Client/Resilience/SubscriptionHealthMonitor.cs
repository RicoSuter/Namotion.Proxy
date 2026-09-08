using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client.Resilience;

/// <summary>
/// Monitors OPC UA subscription health and automatically retries failed monitored items.
/// Periodically checks for unhealthy items and attempts to heal them by calling ApplyChanges.
/// </summary>
internal sealed class SubscriptionHealthMonitor
{
    private readonly ILogger _logger;
    private readonly Action<Exception> _reportError;
    private readonly Func<Subscription, CancellationToken, Task> _applyChangesAsync;

    public SubscriptionHealthMonitor(
        ILogger logger,
        Action<Exception> reportError,
        Func<Subscription, CancellationToken, Task>? applyChangesAsync = null)
    {
        _logger = logger;
        _reportError = reportError;
        _applyChangesAsync = applyChangesAsync ??
            (static (subscription, cancellationToken) => subscription.ApplyChangesAsync(cancellationToken));
    }

    public async Task CheckAndHealSubscriptionsAsync(IReadOnlyCollection<Subscription> subscriptions, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var subscription in subscriptions)
            {
                var unhealthyCount = GetUnhealthyCount(subscription);
                if (unhealthyCount == 0)
                {
                    continue;
                }

                try
                {
                    // Try to heal failed monitored items by reapplying the subscription changes
                    await _applyChangesAsync(subscription, cancellationToken).ConfigureAwait(false);

                    var stillUnhealthyCount = GetUnhealthyCount(subscription);
                    if (stillUnhealthyCount == 0)
                    {
                        _logger.LogInformation(
                            "OPC UA subscription {Id} healed successfully: All {Count} items now healthy.",
                            subscription.Id, unhealthyCount);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "OPC UA subscription {Id} healed partially: {Healthy}/{Total} items recovered.",
                            subscription.Id, unhealthyCount - stillUnhealthyCount, unhealthyCount);
                    }
                }
                catch (Exception ex)
                {
                    ReportErrorIfRunning(ex, cancellationToken);
                    _logger.LogError(ex, "Failed to heal OPC UA subscription {Id}.", subscription.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportErrorIfRunning(ex, cancellationToken);
            _logger.LogError(ex, "OPC UA subscription health check failed.");
        }
    }

    private void ReportErrorIfRunning(Exception error, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            _reportError(error);
        }
    }

    private static int GetUnhealthyCount(Subscription subscription)
    {
        var count = 0;
        foreach (var item in subscription.MonitoredItems)
        {
            if (IsUnhealthy(item) && IsRetryable(item))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Determines if a monitored item is unhealthy (not created or has bad status).
    /// </summary>
    internal static bool IsUnhealthy(MonitoredItem item)
    {
        var statusCode = item.Status?.Error?.StatusCode ?? StatusCodes.Good;
        return !item.Created || StatusCode.IsBad(statusCode);
    }

    /// <summary>
    /// Determines if a failed monitored item should be retried.
    /// Returns false for permanent design-time errors, true for transient errors.
    /// </summary>
    internal static bool IsRetryable(MonitoredItem item)
    {
        var statusCode = item.Status?.Error?.StatusCode ?? StatusCodes.Good;
        return OpcUaStatusCodeClassifier.IsTransientError(statusCode);
    }
}
