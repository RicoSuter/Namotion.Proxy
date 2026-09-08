namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle's outbound notification surface: the two subject events and the ordered handler
/// fan-out, for one context.
/// </summary>
/// <remarks>
/// Separate from <see cref="LifecycleInterceptor"/> so that what the publishing classes are allowed
/// to do is a property of a constructor signature: handing them the interceptor would put the write
/// protocol and both attach entry points within reach of code running under the topology lock.
///
/// Every publication marks the thread through <see cref="CallbackReentrancyGuard.EnterScope"/>,
/// which is what lets the structural write protocol reject a callback that writes a structural
/// property.
/// </remarks>
internal sealed class LifecycleNotifier(IInterceptorSubjectContext context, OwnershipGraph graph)
{
    private enum NotificationKind { Attached, Detaching, AddedHandlers, RemovedHandlers, Refresh, AttachProperty, DetachProperty, ReleaseClaim }
    private readonly record struct Notification(NotificationKind Kind, SubjectLifecycleChange Change, PropertyReference Property = default, object? Value = null);
    private readonly List<Notification> _notifications = [];
    private bool _draining;

    public event Action<SubjectLifecycleChange>? SubjectAttached;
    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void RaiseSubjectAttached(SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.Attached, change));
    public void RaiseSubjectDetaching(SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.Detaching, change));
    public void InvokeAddedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.AddedHandlers, change));
    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.RemovedHandlers, change));
    public void RefreshCollectionProperty(PropertyReference property, object? value) => _notifications.Add(new(NotificationKind.Refresh, default, property, value));
    public void QueueProperty(PropertyReference property, bool attach) => _notifications.Add(new(attach ? NotificationKind.AttachProperty : NotificationKind.DetachProperty, default, property));
    public void QueueRelease(IInterceptorSubject subject) => _notifications.Add(new(NotificationKind.ReleaseClaim, new SubjectLifecycleChange { Subject = subject, ReferenceCount = 0 }));

    public void PublishEdgeRemoved(IInterceptorSubject subject, PropertyReference property, object? index, int referenceCount)
    {
        InvokeRemovedLifecycleHandlers(subject, new SubjectLifecycleChange
        {
            Subject = subject, Property = property, Index = index,
            ReferenceCount = referenceCount, IsPropertyReferenceRemoved = true
        });
    }

    public void Drain()
    {
        if (_draining) return;
        _draining = true;
        List<Exception>? failures = null;
        try
        {
            using var scope = CallbackReentrancyGuard.EnterDeliveryScope();
            for (var index = 0; index < _notifications.Count; index++)
            {
                var notification = _notifications[index];
                switch (notification.Kind)
                {
                    case NotificationKind.Attached:
                    case NotificationKind.Detaching:
                        var callbacks = notification.Kind == NotificationKind.Attached ? SubjectAttached : SubjectDetaching;
                        if (callbacks is not null)
                        {
                            foreach (Action<SubjectLifecycleChange> callback in callbacks.GetInvocationList())
                            {
                                try { callback(notification.Change); }
                                catch (Exception exception) { (failures ??= []).Add(exception); }
                            }
                        }
                        break;
                    case NotificationKind.AddedHandlers:
                    case NotificationKind.RemovedHandlers:
                        var removed = notification.Kind == NotificationKind.RemovedHandlers;
                        if (removed) InvokeSubjectHandler(notification.Change, ref failures);
                        foreach (var handler in context.GetServices<ILifecycleHandler>())
                        {
                            if (handler is LifecycleInterceptor) continue;
                            try { handler.HandleLifecycleChange(notification.Change); }
                            catch (Exception exception) { (failures ??= []).Add(exception); }
                        }
                        if (!removed) InvokeSubjectHandler(notification.Change, ref failures);
                        break;
                    case NotificationKind.Refresh:
                    case NotificationKind.AttachProperty:
                    case NotificationKind.DetachProperty:
                        foreach (var handler in context.GetServices<IPropertyLifecycleHandler>())
                        {
                            InvokePropertyHandler(handler, notification, ref failures);
                        }
                        if (notification.Kind != NotificationKind.Refresh && notification.Property.Subject is IPropertyLifecycleHandler subjectHandler)
                        {
                            InvokePropertyHandler(subjectHandler, notification, ref failures);
                        }
                        break;
                    case NotificationKind.ReleaseClaim:
                        if (!graph.IsOwned(notification.Change.Subject)) graph.ReleaseClaim(notification.Change.Subject);
                        graph.ClearReleasing(notification.Change.Subject);
                        break;
                }
            }
        }
        finally
        {
            _notifications.Clear();
            _draining = false;
        }
        if (failures is not null) throw new AggregateException(failures);
    }

    private static void InvokeSubjectHandler(SubjectLifecycleChange change, ref List<Exception>? failures)
    {
        if (change.Subject is not ILifecycleHandler handler) return;
        try { handler.HandleLifecycleChange(change); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }

    private static void InvokePropertyHandler(IPropertyLifecycleHandler handler, Notification notification, ref List<Exception>? failures)
    {
        try
        {
            var change = new SubjectPropertyLifecycleChange(notification.Property.Subject, notification.Property);
            switch (notification.Kind)
            {
                case NotificationKind.AttachProperty: handler.AttachProperty(change); break;
                case NotificationKind.DetachProperty: handler.DetachProperty(change); break;
                default: handler.RefreshCollectionProperty(notification.Property, notification.Value); break;
            }
        }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }
}
