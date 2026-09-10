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
/// waiting. Changing topology directly from here is rejected outright.
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
    /// Called after a collection property write has been fully reconciled
    /// (all detach/attach events processed). Allows handlers to refresh
    /// child index metadata from the live collection value.
    /// </summary>
    /// <param name="property">The collection property reference.</param>
    /// <param name="value">The current collection value.</param>
    void RefreshCollectionProperty(PropertyReference property, object? value) { }
}