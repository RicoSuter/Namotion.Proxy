using Namotion.Interceptor.Tracking.Change;
using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle's outbound notification surface: the two subject events and the ordered handler
/// fan-out, for one context.
/// </summary>
/// <remarks>
/// Graph descent runs inline. Other handlers and events are queued in their declared order and
/// delivered at the outer gate boundary. Nested writes update ownership before returning, while
/// their notifications join the queue. Callback failures do not roll back committed ownership.
/// </remarks>
internal sealed class LifecycleNotifier(IInterceptorSubjectContext context, OwnershipGraph graph, ILifecycleHandler descentHandler)
{
    private enum NotificationKind { Attached, Detaching, LifecycleHandler, PropertyChange, Refresh, AttachProperty, DetachProperty, ReleaseClaim }
    private readonly record struct Notification(NotificationKind Kind, SubjectLifecycleChange Change, PropertyReference Property = default, object? Value = null);
    private readonly List<PropertyChangeInterceptor.Publication> _propertyChanges = [];
    private readonly List<Notification> _notifications = [];
    private bool _draining;

    public event Action<SubjectLifecycleChange>? SubjectAttached;
    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void RaiseSubjectAttached(SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.Attached, change));
    public void RaiseSubjectDetaching(SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.Detaching, change));
    public void InvokeAddedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        QueueLifecycleHandlers(change);
        if (subject is ILifecycleHandler handler) _notifications.Add(new(NotificationKind.LifecycleHandler, change, Value: handler));
    }

    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        if (subject is ILifecycleHandler handler) _notifications.Add(new(NotificationKind.LifecycleHandler, change, Value: handler));
        QueueLifecycleHandlers(change);
    }

    private void QueueLifecycleHandlers(SubjectLifecycleChange change)
    {
        foreach (var handler in context.GetServices<ILifecycleHandler>())
        {
            if (ReferenceEquals(handler, descentHandler)) handler.HandleLifecycleChange(change);
            else _notifications.Add(new(NotificationKind.LifecycleHandler, change, Value: handler));
        }
    }

    public void QueuePropertyChange(PropertyChangeInterceptor.Publication publication)
    {
        _propertyChanges.Add(publication);
        _notifications.Add(new(NotificationKind.PropertyChange, default));
    }
    public void RefreshCollectionProperty(PropertyReference property, object? value) => _notifications.Add(new(NotificationKind.Refresh, default, property, value));
    public void QueueProperty(PropertyReference property, bool attach) => _notifications.Add(new(attach ? NotificationKind.AttachProperty : NotificationKind.DetachProperty, default, property));
    public void QueueRelease(IInterceptorSubject subject, SubjectOwnership ownership) => _notifications.Add(new(NotificationKind.ReleaseClaim, new SubjectLifecycleChange { Subject = subject, ReferenceCount = 0 }, Value: ownership));

    public void PublishEdgeRemoved(IInterceptorSubject subject, PropertyReference property, object? index, int referenceCount)
    {
        InvokeRemovedLifecycleHandlers(subject, new SubjectLifecycleChange
        {
            Subject = subject, Property = property, Index = index,
            ReferenceCount = referenceCount, IsPropertyReferenceRemoved = true
        });
    }

    public void Drain(Exception? operationFailure = null)
    {
        if (_draining) return;
        _draining = true;
        List<Exception>? failures = null;
        try
        {
            using var scope = CallbackReentrancyGuard.EnterDeliveryScope();
            var propertyChangeIndex = 0;
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
                    case NotificationKind.LifecycleHandler:
                        try { ((ILifecycleHandler)notification.Value!).HandleLifecycleChange(notification.Change); }
                        catch (Exception exception) { (failures ??= []).Add(exception); }
                        break;
                    case NotificationKind.PropertyChange:
                        try { _propertyChanges[propertyChangeIndex++].Dispatch(); }
                        catch (Exception exception) { (failures ??= []).Add(exception); }
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
                        var subject = notification.Change.Subject;
                        var ownership = (SubjectOwnership)notification.Value!;
                        if (graph.IsCurrentRelease(subject, ownership))
                        {
                            if (!graph.IsOwned(subject)) graph.ReleaseClaim(subject);
                            graph.ClearReleasing(subject, ownership);
                        }
                        break;
                }
            }
        }
        finally
        {
            _notifications.Clear();
            _propertyChanges.Clear();
            _draining = false;
        }
        if (failures is not null)
        {
            if (operationFailure is not null) failures.Insert(0, operationFailure);
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException(failures);
        }
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
