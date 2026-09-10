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
/// waiting. Changing topology directly from here is rejected outright.
/// </remarks>
public interface ILifecycleHandler
{
    /// <summary>
    /// Called when a lifecycle event occurs for a subject.
    /// Check the IsAttached, IsPropertyReferenceAdded, IsPropertyReferenceRemoved, and IsDetached flags
    /// to determine which events occurred.
    /// </summary>
    void HandleLifecycleChange(SubjectLifecycleChange change);
}