using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// The subject this executor was constructed for. Exposed so the terminal write can assert that the
    /// context's executor and the locked subject are the same pairing its plain increment relies on.
    /// </summary>
    internal IInterceptorSubject Subject => _subject;

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while the subject's
    /// SyncRoot is held, so a plain increment is exclusive: no Interlocked needed. Dense over
    /// committed writes and never reset, so it stays comparable across detach and reattach.
    ///
    /// It records commit order, it does not establish it: the lock is what serializes the commits, and
    /// this counter labels them afterwards. Consumers do order changes by comparing it (see the flush
    /// merging in docs/tracking.md), but nothing in the write path depends on its value.
    ///
    /// Consumes a revision exactly when the terminal write runs. A vetoed write and a write stopped
    /// by the equality check never reach the terminal and consume nothing, but a derived property's
    /// recalculation does reach it (with a no-op write delegate) and takes a revision of its own.
    /// </summary>
    internal long Revision;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    /// <summary>
    /// Returns the subject's executor, publishing one on first access. Call it from the subject's
    /// <see cref="IInterceptorSubject.Context"/> accessor, passing that subject's own backing field.
    /// Public because the source generator emits the call into the consumer's assembly.
    /// </summary>
    /// <remarks>
    /// Compare-and-swap rather than <c>??=</c>: a lazy assignment lets two threads racing the first
    /// access each publish an executor and discard one, along with everything that had been put on it,
    /// including the per-subject commit revision counter. It is also the store that safely publishes
    /// the new instance, which a plain assignment is not.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IInterceptorExecutor GetOrCreate(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        // The allocation sits in a separate non-inlined method so the accessor stays a load and a
        // branch, small enough to inline into its own callers.
        return context ?? CreateAndPublish(ref context, subject);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInterceptorExecutor CreateAndPublish(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        var created = new InterceptorExecutor(subject);
        return Interlocked.CompareExchange(ref context, created, null) ?? created;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var context = new PropertyReadContext<TProperty>(new PropertyReference(_subject, propertyName));
        return ExecuteInterceptedRead(ref context, readValue);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    /// <summary>
    /// Cascade re-entry path: skips the lazy-resolve machinery by pre-populating the new write
    /// context's timestamp cache. Lets the cascade share the trigger's captured time without
    /// pushing a <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope.
    /// The new value is already the stabilized getter output on this path, so publishing reuses it
    /// instead of invoking the getter again (see
    /// <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    /// <remarks>
    /// The attach callbacks run after the edge is published, and they must: they resolve their
    /// handlers through this executor, which finds nothing until the fallback is in place.
    /// </remarks>
    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        // Cast first, matching the base mutator, so a foreign context fails here rather than
        // after an arbitrary service walk.
        var contextImpl = (InterceptorSubjectContext)context;
        if (HasFallbackContext(contextImpl))
        {
            return false;
        }

        // Reads the fallback's chain, not this one, so it does not need the edge. Resolving
        // before publishing is what leaves nothing behind when it throws.
        var interceptors = contextImpl.GetServices<ILifecycleInterceptor>();

        var attachment = TryBeginFallbackAttachment(contextImpl, interceptors);
        if (attachment is null)
        {
            return false;
        }

        var invokedInterceptorCount = 0;
        try
        {
            for (var index = 0; index < interceptors.Length; index++)
            {
                // Counted before the call: a thrower may have mutated itself, so its detach still
                // has to run.
                invokedInterceptorCount = index + 1;
                interceptors[index].AttachSubjectToContext(_subject);
            }
        }
        finally
        {
            if (CompleteFallbackAttachment(attachment, invokedInterceptorCount))
            {
                // A remover arrived mid-attach and handed its removal over. It has already told
                // its caller the edge is gone, so this must happen even while an attach exception
                // is propagating, and must not replace that exception.
                try
                {
                    DetachAndCompleteRemoval(attachment);
                }
                catch (Exception)
                {
                    // Swallowed on both paths. The detach belongs to the remover, which has already
                    // returned, so surfacing its handler failure here would report it to a caller
                    // that only asked to add. The edge still comes out either way, so what is lost
                    // is the diagnostic and not the state.
                }
            }
        }

        return true;
    }

    /// <remarks>
    /// The detach callbacks run before the edge is removed, and they must: they resolve their
    /// handlers through this executor, which finds nothing once the fallback is gone.
    /// <para>
    /// Returning <c>true</c> means the removal is committed, not necessarily that the edge is
    /// already gone: when an add is still running its attach callbacks, the removal is handed to
    /// that thread and completes there. Waiting instead would deadlock, because the attaching
    /// thread is inside callbacks that take the lifecycle lock this caller may already hold.
    /// </para>
    /// </remarks>
    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        var outcome = TryTakeFallbackAttachment(contextImpl, out var attachment);
        if (outcome == FallbackRemovalOutcome.NotPresent)
        {
            return false;
        }

        // Deferred means the attaching thread runs the callbacks and the removal instead.
        if (outcome == FallbackRemovalOutcome.Claimed)
        {
            DetachAndCompleteRemoval(attachment!);
        }

        return true;
    }

    /// <summary>
    /// Runs the recorded detach callbacks and then drops the edge.
    /// </summary>
    private void DetachAndCompleteRemoval(FallbackAttachment attachment)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            var interceptors = attachment.Interceptors;
            for (var index = 0; index < attachment.InvokedInterceptorCount; index++)
            {
                try
                {
                    interceptors[index].DetachSubjectFromContext(_subject);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            // A handler failure must never block the removal, because a blocked removal is what
            // strands edges and retains subtrees.
            CompleteFallbackContextRemoval(attachment.Context);
        }

        failure?.Throw();
    }
}