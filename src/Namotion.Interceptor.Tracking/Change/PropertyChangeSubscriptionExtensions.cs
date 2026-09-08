using System.Linq.Expressions;
using System.Reactive.Concurrency;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Change;

public static class PropertyChangeSubscriptionExtensions
{
    /// <summary>
    /// Subscribes an observer to changes of a single property (subject instance + name). Delivery is
    /// inline, on the writing thread, and dormant while the subject is not attached to a context
    /// with a <see cref="PropertyChangeInterceptor"/>. See <see cref="IPropertyChangeObserver"/> for the contract.
    /// </summary>
    /// <remarks>
    /// Disposing the returned handle is mandatory: the subject holds a strong reference, so a dropped
    /// handle keeps the observer alive and permanently disables the process-wide idle write fast path.
    /// Dispatches already in flight may still invoke the observer after Dispose returns.
    /// Under concurrent writes to the same property, notifications may arrive out of commit order because
    /// dispatch runs outside the subject lock; if you need the current value, re-read the property rather
    /// than relying on the delivered new value.
    /// Provided the downstream interceptor chain returns normally after the commit, a write that commits
    /// after SubscribeInline returns is always delivered while the subscription stays live and no earlier
    /// synchronous observer of the same write throws. A write that committed before may not be, and reading
    /// the property after subscribing observes that earlier state. OldValue is the value the setter observed
    /// when it started, including when the subscription raced the write.
    /// </remarks>
    public static IDisposable SubscribeInline(this PropertyReference property, IPropertyChangeObserver observer)
    {
        // A null observer would install a silent never-firing subscription that still opens the process-wide gate.
        ArgumentNullException.ThrowIfNull(observer);

        var metadata = property.Metadata; // throws InvalidOperationException when the name is not a known property
        if (!(metadata.IsIntercepted || metadata.IsDerived))
        {
            throw new ArgumentException(
                $"Property '{property.Name}' on {property.Subject.GetType().Name} cannot be subscribed to: it is not an intercepted or derived property, so its changes never enter the interception chain.",
                nameof(property));
        }

        return PropertyChangeSubscription.Create(property, observer);
    }

    /// <summary>Delegate overload of <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable SubscribeInline(this PropertyReference property, PropertyChangeCallback callback)
    {
        // A null callback wrapped in DelegateObserver would fail on a writer thread at dispatch time.
        ArgumentNullException.ThrowIfNull(callback);
        return property.SubscribeInline(new DelegateObserver(callback));
    }

    /// <summary>
    /// Strongly-typed subscription to a direct property of <paramref name="subject"/>, for example
    /// <c>subject.SubscribeToPropertyInline(x => x.Temperature, observer)</c>. Only a direct property access on
    /// the lambda parameter is accepted; chained, captured, static, field, and method selectors throw.
    /// </summary>
    /// <remarks>
    /// Same ownership, concurrency, and delivery contract as
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>.
    /// </remarks>
    public static IDisposable SubscribeToPropertyInline<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(propertySelector);

        var name = ResolveDirectPropertyName(propertySelector);
        return new PropertyReference(subject, name).SubscribeInline(observer);
    }

    /// <summary>Delegate overload of <see cref="SubscribeToPropertyInline{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable SubscribeToPropertyInline<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback)
        where TSubject : IInterceptorSubject
    {
        // Wrapping first would bypass the observer null guard and fail on a writer thread at dispatch time.
        ArgumentNullException.ThrowIfNull(callback);
        return subject.SubscribeToPropertyInline(propertySelector, new DelegateObserver(callback));
    }

    /// <summary>
    /// Subscribes to changes of one property and delivers them serially on <paramref name="scheduler"/>.
    /// Observer and scheduler exceptions do not escape to the writer and are reported to
    /// <paramref name="onError"/> when supplied.
    /// </summary>
    /// <remarks>
    /// Serialization is per subscription. An observer shared by several subscriptions may be invoked
    /// concurrently. The queue is unbounded, and disposal drops queued work. A delivery already in flight may
    /// still invoke the observer or finish after Dispose returns. Changes queued before a subject detaches
    /// still drain.
    /// Observer failures invoke <paramref name="onError"/> on the scheduler execution thread. A synchronous
    /// <see cref="IScheduler.Schedule{TState}(TState, Func{IScheduler, TState, IDisposable})"/> failure invokes it
    /// immediately on the thread calling the scheduler. When scheduling occurs while accepting a change, this
    /// is the writing thread and the handler completes before the setter returns, so a slow handler delays the
    /// setter. Error handlers shared by subscriptions may be invoked concurrently and must be thread-safe.
    /// Exceptions thrown by <paramref name="onError"/> are swallowed. A synchronous scheduling failure faults
    /// the subscription only if it wins the terminal transition; concurrent or reentrant disposal may win
    /// instead, while the failure is still reported and <see cref="ScheduledPropertySubscription.IsFaulted"/>
    /// remains <see langword="false"/>.
    /// <see cref="ImmediateScheduler.Instance"/> and <see cref="CurrentThreadScheduler.Instance"/> are rejected,
    /// but a custom or wrapped scheduler may still invoke work inline and cannot be detected. In that case the
    /// observer runs inside the setter, its latency affects the writer, and it sees the writer's current ambient
    /// state. For asynchronous work, the writer's
    /// <see cref="ExecutionContext"/> flow is suppressed instead of captured, but suppression does not clear
    /// ambient state already present on the scheduler's worker thread.
    /// </remarks>
    public static ScheduledPropertySubscription Subscribe(
        this PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scheduler);
        ThrowIfSynchronous(scheduler);

        return ScheduledPropertySubscription.Create(property, observer, scheduler, onError);
    }

    /// <summary>
    /// Delegate overload of
    /// <see cref="Subscribe(PropertyReference, IPropertyChangeObserver, IScheduler, Action{Exception})"/>.
    /// </summary>
    public static ScheduledPropertySubscription Subscribe(
        this PropertyReference property,
        PropertyChangeCallback callback,
        IScheduler scheduler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return property.Subscribe(new DelegateObserver(callback), scheduler, onError);
    }

    /// <summary>
    /// Strongly-typed scheduled subscription to a direct property of <paramref name="subject"/>.
    /// </summary>
    /// <remarks>
    /// This has the same delivery contract as
    /// <see cref="Subscribe(PropertyReference, IPropertyChangeObserver, IScheduler, Action{Exception})"/>
    /// and the same selector restrictions as
    /// <see cref="SubscribeToPropertyInline{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.
    /// </remarks>
    public static ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError = null)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(propertySelector);

        var name = ResolveDirectPropertyName(propertySelector);
        return new PropertyReference(subject, name).Subscribe(observer, scheduler, onError);
    }

    /// <summary>
    /// Delegate overload of
    /// <see cref="SubscribeToProperty{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver, IScheduler, Action{Exception})"/>.
    /// </summary>
    public static ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback,
        IScheduler scheduler,
        Action<Exception>? onError = null)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(callback);
        return subject.SubscribeToProperty(propertySelector, new DelegateObserver(callback), scheduler, onError);
    }

    private static void ThrowIfSynchronous(IScheduler scheduler)
    {
        if (ReferenceEquals(scheduler, ImmediateScheduler.Instance) ||
            ReferenceEquals(scheduler, CurrentThreadScheduler.Instance))
        {
            throw new ArgumentException(
                "Use SubscribeInline for writer-thread delivery. Scheduled subscriptions require an asynchronous scheduler.",
                nameof(scheduler));
        }
    }

    private static string ResolveDirectPropertyName<TSubject, TValue>(Expression<Func<TSubject, TValue>> propertySelector)
    {
        var body = propertySelector.Body;

        // Unwrap a Convert/ConvertChecked boxing or numeric-cast node (e.g. Expression<Func<T, object>>).
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member
            || member.Member is not PropertyInfo property
            || member.Expression != propertySelector.Parameters[0])
        {
            throw new ArgumentException(
                "Only a direct property access on the lambda parameter is supported, for example x => x.Foo. " +
                "Chained (x => x.Child.Foo), captured-variable, static, field, and method selectors are not allowed.",
                nameof(propertySelector));
        }

        return property.Name;
    }

    private sealed class DelegateObserver(PropertyChangeCallback callback) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => callback(in change);
    }
}
