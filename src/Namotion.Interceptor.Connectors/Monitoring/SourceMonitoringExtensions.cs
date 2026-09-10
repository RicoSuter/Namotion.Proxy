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
    /// Resolves the monitor. The singleton contract makes a second monitor on one context a
    /// registration error, so there is at most one.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<SourceMonitor>()
            ?? throw new InvalidOperationException(NoMonitorMessage);
    }

    /// <summary>
    /// Declares that source registration is complete. Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static void CompleteSourceRegistration(this IInterceptorSubjectContext context)
    {
        context.GetSourceMonitor().CompleteSourceRegistration();
    }

    /// <summary>
    /// Blocks wait completion until the returned handle is disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static IDisposable DeferWaitCompletion(this IInterceptorSubjectContext context)
    {
        return context.GetSourceMonitor().DeferWaitCompletion();
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

        // Resolved before the wait is created, so a null subject and an unreachable monitor keep
        // throwing synchronously rather than surfacing on await.
        return subject.GetContext().GetSourceMonitor().WaitForSynchronizationAsync(subject, cancellationToken);
    }
}
