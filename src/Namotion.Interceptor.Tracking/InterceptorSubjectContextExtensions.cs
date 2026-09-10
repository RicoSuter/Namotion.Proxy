using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Registers full property tracking including equality checks, context inheritance, derived property change detection, and property change subscriptions (observable, queue, and per-property).
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithFullPropertyTracking(this IInterceptorSubjectContext context)
    {
        return context
            .WithEqualityCheck()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions()
            .WithContextInheritance();
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
        context // must be before lifecycle!
            .WithService(() => new DerivedPropertyChangeHandler());

        return context
            .WithLifecycle();
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
    /// Adds automatic context assignment and <see cref="WithLifecycle"/>.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithContextInheritance(this IInterceptorSubjectContext context)
    {
        context
            .WithLifecycle()
            .WithService(() => new ContextInheritanceHandler());

        return context;
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithLifecycle(this IInterceptorSubjectContext context)
    {
        context.TryAddService<ILifecycleInterceptor>(() => new LifecycleInterceptor(), _ => true);
        return context;
    }
    
    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithParents(this IInterceptorSubjectContext context)
    {
        context
            .WithService(() => new ParentTrackingHandler());

        return context
            .WithLifecycle();
    }
}
