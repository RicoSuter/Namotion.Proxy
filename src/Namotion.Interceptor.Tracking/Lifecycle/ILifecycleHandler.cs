namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A lifecycle handler that is called when a subject enters/leaves the object graph
/// and when property references are added/removed.
/// </summary>
/// <remarks>
/// Runs while the lifecycle holds its topology gate. Never hand structural work to another thread
/// and wait for it from here: a dispatched structural write, attach or detach needs the same gate
/// this thread is holding, so the two wait on each other. Dispatching a read, a scalar write or
/// input and output and waiting for it is safe, and so is handing structural work off without
/// waiting. Same-context topology changes are supported during queued delivery; their notifications
/// are deferred. Entering another context's topology gate is rejected.
/// </remarks>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a lifecycle event occurs for a subject.
    /// Check the IsContextAttach, IsPropertyReferenceAdded, IsPropertyReferenceRemoved, and IsContextDetach flags
    /// to determine which events occurred.
    /// </summary>
    void HandleLifecycleChange(SubjectLifecycleChange change);
}