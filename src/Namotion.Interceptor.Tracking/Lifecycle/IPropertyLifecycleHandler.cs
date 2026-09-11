namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// A property lifecycle handler that is called when a property is attached/detached from the subject tree
/// or when a collection property's children have changed.
/// The handler can be registered in the subject context and applies to the subject and all its children.
/// A subject can also implement this interface directly to handle its own property lifecycle changes.
/// </summary>
/// <remarks>
/// Runs while the lifecycle holds its topology gate. Never hand structural work to another thread
/// and wait for it from here: a dispatched structural write, attach or detach needs the same gate
/// this thread is holding, so the two wait on each other. Dispatching a read, a scalar write or
/// input and output and waiting for it is safe, and so is handing structural work off without
/// waiting. Same-context topology changes are supported during queued delivery; their notifications
/// are deferred. Entering another context's topology gate is rejected.
/// </remarks>
public interface IPropertyLifecycleHandler
{
    /// <summary>
    /// Called when a property is attached to the subject tree.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    public void AttachProperty(SubjectPropertyLifecycleChange change);

    /// <summary>
    /// Called when a property is detached from the subject tree.
    /// </summary>
    /// <param name="change">The lifecycle change information.</param>
    public void DetachProperty(SubjectPropertyLifecycleChange change);

    /// <summary>
    /// Queued after this reconciliation's lifecycle notifications to refresh child index metadata.
    /// The supplied value is captured for that reconciliation and may differ from current storage
    /// when delivered. Reentrant work can queue further lifecycle notifications after this refresh.
    /// </summary>
    /// <param name="property">The collection property reference.</param>
    /// <param name="value">The collection value captured for this reconciliation.</param>
    void RefreshCollectionProperty(PropertyReference property, object? value) { }
}