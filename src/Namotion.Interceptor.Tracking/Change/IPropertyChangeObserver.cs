namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Per-property change observer. The required concurrency and failure behavior depends on how it is
/// subscribed.
/// <para>
/// An inline subscription invokes <see cref="OnChange"/> on the writing thread, inside the write and outside
/// the subject lock. Implementations must be thread-safe because concurrent writers may invoke them
/// concurrently, and an exception propagates out of the setter.
/// </para>
/// <para>
/// A scheduled subscription invokes <see cref="OnChange"/> on its scheduler and serializes calls within that
/// subscription. An observer shared by several subscriptions may still be invoked concurrently. Observer
/// exceptions are isolated and reported to that subscription's error handler.
/// </para>
/// Deliveries may arrive out of commit order under concurrent writes. Re-read the property when the current
/// value is required.
/// </summary>
public interface IPropertyChangeObserver
{
    /// <summary>
    /// Invoked when a subscribed property changes, either on the writing thread or on the subscription's
    /// scheduler.
    /// </summary>
    /// <param name="change">The property change that occurred.</param>
    void OnChange(in SubjectPropertyChange change);
}
