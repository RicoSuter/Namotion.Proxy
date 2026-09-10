namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Delegate form of <see cref="IPropertyChangeObserver"/>. Inline subscriptions invoke it on the writing
/// thread and may do so concurrently. Scheduled subscriptions invoke it on the scheduler and serialize it
/// within one subscription, but not across subscriptions sharing the delegate.
/// </summary>
public delegate void PropertyChangeCallback(in SubjectPropertyChange change);
