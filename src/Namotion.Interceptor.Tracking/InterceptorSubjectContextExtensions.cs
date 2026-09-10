using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Registers full property tracking including equality checks, the graph lifecycle (context
    /// inheritance and parent tracking), derived property change detection, and property change
    /// subscriptions (observable, queue, and per-property).
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithFullPropertyTracking(this IInterceptorSubjectContext context)
    {
        // Lifecycle first, so a lifecycle conflict throws before any dependent service is
        // published.
        return context
            .WithLifecycle()
            .WithEqualityCheck()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();
    }

    /// <summary>
    /// Enables transaction support for the context.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithTransactions(this IInterceptorSubjectContext context)
    {
        context.TryAddService(() => new SubjectTransactionInterceptor(), _ => true);
        return context;
    }

    /// <summary>
    /// Registers an interceptor that checks if the new value is different from the current value and only calls inner interceptors when the property has changed.
    /// Uses <c>EqualityComparer&lt;T&gt;.Default</c> for all property types, including reference types.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithEqualityCheck(this IInterceptorSubjectContext context)
    {
        return context
            .WithService(() => new PropertyValueEqualityCheckHandler());
    }

    /// <summary>
    /// Registers the derived property change detection interceptor.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithDerivedPropertyChangeDetection(this IInterceptorSubjectContext context)
    {
        // Lifecycle first, so a lifecycle conflict throws before the handler is published. The
        // handler's chain position ahead of the lifecycle is pinned by its [RunsBefore] attributes.
        return context
            .WithLifecycle()
            .WithService(() => new DerivedPropertyChangeHandler());
    }

    /// <summary>
    /// Registers the read property recorder used to record property read invocations.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithReadPropertyRecorder(this IInterceptorSubjectContext context)
    {
        return context
            .WithService(() => new ReadPropertyRecorder());
    }

    /// <summary>
    /// Registers the property change interceptor, enabling both the Rx observable
    /// (<see cref="GetPropertyChangeObservable"/>) and the high-performance queue
    /// (<see cref="CreatePropertyChangeQueueSubscription"/>) channels, and per-property subscriptions.
    /// By itself, the interceptor does not suppress writes of equal values. Combine with
    /// <see cref="WithEqualityCheck"/> (or use
    /// <see cref="WithFullPropertyTracking"/>) to suppress values that compare equal according to
    /// <c>EqualityComparer&lt;T&gt;.Default</c>.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithPropertyChangeSubscriptions(this IInterceptorSubjectContext context)
    {
        context.TryAddService(() => new PropertyChangeInterceptor(), _ => true);
        return context;
    }

    /// <summary>
    /// Gets the property changed observable which is registered in the context.
    /// Under concurrent writes to the same property, notifications may arrive out of commit order because
    /// dispatch runs outside the subject lock; if you need the current value, re-read the property rather
    /// than relying on the delivered new value.
    /// Provided the downstream interceptor chain returns normally after the commit, a write that commits
    /// after Subscribe returns is always delivered while the subscription stays live and no earlier
    /// synchronous observer of the same write throws. A write that committed before may not be, and reading
    /// the property after subscribing observes that earlier state. OldValue is the value the setter observed
    /// when it started, including when the subscription raced the write. For a scheduler-based observer,
    /// delivered means accepted by the channel, not that the callback has already run.
    /// A dispatch already in flight may still invoke the observer after its subscription's Dispose returns.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="scheduler">The scheduler to run the callbacks on (defaults to Scheduler.Default).
    /// Use ImmediateScheduler.Instance for zero-allocation synchronous delivery.</param>
    /// <returns>The observable.</returns>
    public static IObservable<SubjectPropertyChange> GetPropertyChangeObservable(this IInterceptorSubjectContext context, IScheduler? scheduler = null)
    {
        // The interceptor is already a synchronized multicast observable, so every observer goes
        // through its guaranteed subscribe path directly; AsObservable only hides the concrete
        // implementation type from consumers.
        var observable = context
            .GetService<PropertyChangeInterceptor>()
            .AsObservable();

        if (scheduler == ImmediateScheduler.Instance)
        {
            // Skip ObserveOn for ImmediateScheduler - it's synchronous and ObserveOn adds allocation overhead
            return observable;
        }

        return observable.ObserveOn(scheduler ?? Scheduler.Default);
    }

    /// <summary>
    /// Creates a pull-based queue subscription over the property change interceptor registered in the context.
    /// Same ordering caveat and delivery contract as <see cref="GetPropertyChangeObservable"/>, with the
    /// guarantee anchored to this method returning.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The queue subscription.</returns>
    public static PropertyChangeQueueSubscription CreatePropertyChangeQueueSubscription(this IInterceptorSubjectContext context)
    {
        return context
            .GetService<PropertyChangeInterceptor>()
            .CreateQueueSubscription();
    }

    /// <summary>
    /// Registers the built-in graph lifecycle: context inheritance, parent tracking, subject
    /// attach/detach events, and support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <remarks>
    /// Idempotent for the default lifecycle. A custom <see cref="Interceptors.ILifecycleInterceptor"/>
    /// registered on the same context conflicts through its singleton contract, so this call then
    /// throws instead of silently running configuration against a foreign lifecycle.
    ///
    /// Registering the lifecycle behind an attach is rejected. A subject anchored while the context
    /// had no lifecycle never enters the ownership graph the lifecycle brings, and nothing later
    /// puts it there, so the graph would treat that root as unowned forever and let every structural
    /// write on it through without a claim, without validating the subjects it pulls in and without
    /// reconciling any edge. The check reads a flag the lifecycle-free attach path sets, so it sees
    /// an attach that has already landed; an attach still in flight on another thread is not
    /// ordered against this call and is not caught, which is the concurrent-configuration case
    /// documented in docs/design/tracking-lifecycle.md.
    /// </remarks>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    /// <exception cref="InvalidOperationException">A subject was already attached to the context
    /// while it had no lifecycle.</exception>
    public static IInterceptorSubjectContext WithLifecycle(this IInterceptorSubjectContext context)
    {
        if (context is InterceptorSubjectContext { WasAttachedWithoutLifecycle: true })
        {
            throw new InvalidOperationException(
                "A subject was already attached to this context while it had no lifecycle, and a " +
                "root anchored that way never enters the ownership graph this call would register: " +
                "its structural writes would silently skip claiming, validation and reconciliation. " +
                "Register the lifecycle (WithLifecycle, WithRegistry, WithFullPropertyTracking or " +
                "any feature that implies one) before attaching any subject to the context.");
        }

        // The lifecycle captures the context it is registered on: that context is the one exact
        // context it claims subjects for.
        return context
            .WithService(() => new LifecycleInterceptor(context));
    }
}
