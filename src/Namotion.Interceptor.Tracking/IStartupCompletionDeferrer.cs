namespace Namotion.Interceptor.Tracking;

/// <summary>
/// A service whose "everything has started" signal can be held open while another subsystem still
/// has queued work that has not run yet.
/// </summary>
/// <remarks>
/// Exists so two packages that do not reference each other can hand off. Namotion.Interceptor.Hosting
/// queues an attached hosted service's start rather than running it inline, and
/// Namotion.Interceptor.Connectors treats "every source has registered" as the point where waits may
/// complete; without this it would reach that point while a queued start was still pending.
/// <para>
/// Take a hold before queueing the work and dispose it once the work has run, including on failure.
/// Holds are counted. Taking one never un-completes a signal that has already fired.
/// </para>
/// <para>
/// <b>The constraint on an implementation.</b> Both halves can run under the lifecycle lock:
/// Namotion.Interceptor.Hosting calls <see cref="DeferCompletion"/> synchronously from a lifecycle
/// event, which fires inside <see cref="Lifecycle.LifecycleInterceptor"/>'s attach lock during a
/// property write, and it disposes the returned hold from that same place when the start it was taken
/// for is refused. So do not block, in either method, on anything that needs the lifecycle lock to
/// make progress, and take a lock of your own only where its order against the lifecycle lock is
/// already fixed, which means nothing held under that lock ever waits on anything that needs the
/// lifecycle lock. Two things in this repository need it: awaiting a hosted service transition, because
/// a transition that writes a subject typed property takes that lock, and attaching a hosted service at
/// all, because the attach takes it directly to record liveness. So a lock this deferrer takes must
/// never be held across either. A lock that a thread inside the lifecycle lock can wait for is allowed
/// under exactly that condition and forbidden without it, because without it the two can be acquired in
/// either order and a cycle closes that nothing resolves. That cycle is set out in
/// docs/design/hosting-service-ownership.md#4-a-deferrer-that-takes-a-lock-of-its-own, and its
/// blast radius is every structural property write in the graph rather than only the caller, because the
/// lifecycle lock is held throughout.
/// </para>
/// <para>
/// Taking a lock at all is avoidable on the take path: an interlocked increment returning a counted
/// handle is enough, which is what SourceMonitor in Namotion.Interceptor.Connectors does. Its release
/// path does take a lock, and that is allowed under the rule above because the same order is already
/// fixed elsewhere in that type.
/// </para>
/// </remarks>
public interface IStartupCompletionDeferrer
{
    /// <summary>
    /// Holds completion open until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// This method and the returned handle's dispose can both run under the lifecycle lock, so neither
    /// may block on anything that needs it. See the remarks on <see cref="IStartupCompletionDeferrer"/>.
    /// </remarks>
    IDisposable DeferCompletion();
}
