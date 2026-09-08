using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>Consumer-facing entry points for source monitoring.</summary>
public static class SourceMonitoringExtensions
{
    private const string NoMonitorMessage =
        "No SourceMonitor is reachable from this context. Call WithSourceMonitoring() on the tree root context.";

    /// <summary>
    /// Gets the property's synchronization state, derived from its owning source with no per-property storage.
    /// </summary>
    /// <remarks>
    /// Only fully meaningful once the branch containing the property has been awaited through
    /// WaitForSynchronizationAsync: before claiming has happened, Unclaimed cannot be distinguished
    /// from not-yet-claimed. After a claim it reports Synchronizing, so "will sync, still loading" is
    /// already distinguishable from "no source" even before the wait completes.
    /// </remarks>
    public static SourceState GetSourceState(this PropertyReference property)
    {
        return property.TryGetSource(out var source) ? source.State : SourceState.Unclaimed;
    }

    /// <summary>
    /// Resolves the single reachable monitor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable, or more than one is.</exception>
    public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context)
    {
        var monitors = context.GetSourceMonitors();
        return monitors.Length switch
        {
            1 => monitors[0],
            0 => throw new InvalidOperationException(NoMonitorMessage),
            _ => throw new InvalidOperationException(
                $"{monitors.Length} SourceMonitor instances are reachable from this context. " +
                "Combining them is a decision for the call site: use GetServices<SourceMonitor>() and choose explicitly.")
        };
    }

    internal static ImmutableArray<SourceMonitor> GetSourceMonitors(this IInterceptorSubjectContext context)
    {
        return context.GetServices<SourceMonitor>();
    }

    /// <summary>
    /// Declares that source registration is complete on every reachable monitor. Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static void CompleteSourceRegistration(this IInterceptorSubjectContext context)
    {
        ExceptionAggregation.ForEach(
            ResolveMonitorsOrThrow(context), monitor => monitor.CompleteSourceRegistration());
    }

    /// <summary>
    /// Blocks wait completion on every reachable monitor until the returned handle is disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static IDisposable DeferWaitCompletion(this IInterceptorSubjectContext context)
    {
        var monitors = ResolveMonitorsOrThrow(context);
        var holds = monitors.Select(monitor => monitor.DeferWaitCompletion()).ToArray();
        return new CompositeDisposable(holds);
    }

    private static ImmutableArray<SourceMonitor> ResolveMonitorsOrThrow(IInterceptorSubjectContext context)
    {
        var monitors = context.GetSourceMonitors();
        if (monitors.IsEmpty)
        {
            throw new InvalidOperationException(NoMonitorMessage);
        }

        return monitors;
    }

    private sealed class CompositeDisposable(IDisposable[] disposables) : IDisposable
    {
        public void Dispose() => ExceptionAggregation.ForEach(disposables, hold => hold.Dispose());
    }

    /// <summary>
    /// Waits until every source that can claim into this subject's branch has settled, and reports
    /// whether they all delivered their initial load and are all still live. The subject IS the
    /// scope, so waiting on the tree root means the whole tree.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable from the subject's context.</exception>
    public static Task<SourceSynchronizationResult> WaitForSynchronizationAsync(
        this IInterceptorSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // Outside the async helper, so a null subject and an unreachable monitor keep throwing
        // synchronously rather than surfacing on await.
        var monitors = ResolveMonitorsOrThrow(subject.Context);
        return monitors.Length == 1
            ? monitors[0].WaitForSynchronizationAsync(subject, cancellationToken)
            : WaitForAllMonitorsAsync(monitors, subject, cancellationToken);
    }

    /// <summary>
    /// Worst wins across monitors, which is a minimum over the result values.
    /// </summary>
    /// <remarks>
    /// Every wait is created before the first await: awaiting them one at a time would leave the
    /// later monitors unregistered until the earlier ones completed.
    /// </remarks>
    private static async Task<SourceSynchronizationResult> WaitForAllMonitorsAsync(
        ImmutableArray<SourceMonitor> monitors, IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        var waits = new Task<SourceSynchronizationResult>[monitors.Length];
        for (var index = 0; index < monitors.Length; index++)
        {
            waits[index] = monitors[index].WaitForSynchronizationAsync(subject, cancellationToken);
        }

        var results = await Task.WhenAll(waits).ConfigureAwait(false);

        var worst = SourceSynchronizationResult.Synchronized;
        foreach (var result in results)
        {
            if (result < worst)
            {
                worst = result;
            }
        }

        return worst;
    }
}
