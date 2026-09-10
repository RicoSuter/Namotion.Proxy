using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// The built-in <see cref="IInterceptorExecutor"/>, one per subject and published on first access
/// through <see cref="GetOrCreate"/>. It additionally owns the per-subject state the interface
/// cannot express: the terminal lock, the commit revision, and the attachment monitor.
/// </summary>
public sealed class InterceptorExecutor : IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// The subject this executor was constructed for. Exposed so the terminal write can assert that the
    /// executor threaded through a write context and the subject being locked are the same pairing its
    /// plain increment relies on.
    /// </summary>
    internal IInterceptorSubject Subject => _subject;

    /// <summary>
    /// The terminal lock that serializes backing-field access of the subject, taken by the chain
    /// terminals in <see cref="ReadInterceptorFactory{TProperty}"/> and
    /// <see cref="WriteInterceptorFactory{TProperty}"/>. One executor is published per subject, so
    /// this is a per-subject lock; without it a wide value type could be read while half written.
    /// The innermost lock of the structural write order (see the note on <see cref="_attachmentLock"/>).
    /// </summary>
    internal readonly object SyncRoot = new();

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while this executor's
    /// <see cref="SyncRoot"/> is held, so a plain increment is exclusive: no Interlocked needed. Dense
    /// over committed writes and never reset, so it stays comparable across detach and reattach.
    ///
    /// It records commit order, it does not establish it: the lock is what serializes the commits, and
    /// this counter labels them afterwards.
    ///
    /// Consumes a revision exactly when the terminal write runs. A vetoed write and a write stopped
    /// by the equality check never reach the terminal and consume nothing, but a derived property's
    /// recalculation does reach it (with a no-op write delegate) and takes a revision of its own.
    /// </summary>
    internal long Revision;

    // The exact attachment state, held as one immutable object. Writes are serialized by a private
    // monitor rather than SyncRoot so that transitioning the attachment never contends with
    // ordinary property writes; reads are lock-free (the context read is the ownership predicate
    // across the codebase, so it must cost a volatile load, not a monitor).
    //
    // One reference rather than three fields, because the three are decided on together: an anchor
    // is only meaningful against the context it anchors to, and the revision is the compare-and-swap
    // token for exactly the state it labels. Three separately volatile fields are each coherent and
    // jointly not, so a lock-free reader landing between two stores observes a combination no
    // committed state ever had, such as a non-None anchor with no context. Publishing the whole
    // triple with the single volatile store below leaves a reader the new state or the previous one
    // and nothing in between, which is also why the revision needs no Interlocked.Read on the
    // 32-bit runtimes netstandard2.0 reaches: it is a readonly field of an object that the
    // reference store already published.
    //
    // Lock order, stated once here and cross-referenced everywhere else: the structural write
    // protocol is lifecycle gate, then _attachmentLock, then SyncRoot (the terminal). The gate
    // must come first: a write that entered this monitor before the gate deadlocks against a
    // removal that holds the gate and releases this subject's claim through this same monitor.
    // The attachment transition (TryUpdateAttachment) takes _attachmentLock alone and runs no
    // foreign code while holding it, so it is a leaf acquisition, and nothing enters
    // _attachmentLock while holding a SyncRoot.
    private readonly object _attachmentLock = new();
    private volatile AttachmentState _attachment = AttachmentState.Unattached;

    /// <summary>
    /// Creates an executor for <paramref name="subject"/>. Prefer <see cref="GetOrCreate"/>, which
    /// publishes exactly one executor per subject; a second instance would split the commit
    /// revision and the terminal lock.
    /// </summary>
    /// <param name="subject">The subject this executor runs interception for.</param>
    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    /// <inheritdoc />
    public IInterceptorSubjectContext? AttachedContext => _attachment.Context;

    /// <inheritdoc />
    public SubjectAttachmentAnchorKind AttachmentAnchor => _attachment.Anchor;

    /// <inheritdoc />
    public long AttachmentRevision => _attachment.Revision;

    /// <inheritdoc />
    public bool TryUpdateAttachment(long expectedRevision, IInterceptorSubjectContext? context, SubjectAttachmentAnchorKind anchor, out long currentRevision)
    {
        if (context is null && anchor != SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException(
                $"Cannot apply the anchor '{anchor}' without an attached context.");
        }

        // Rejected loudly rather than attached uselessly: interceptor chains compile inside
        // InterceptorSubjectContext, so a foreign implementation of the interface would attach,
        // report itself through TryGetContext(), and intercept nothing.
        if (context is not (null or InterceptorSubjectContext))
        {
            throw new InvalidOperationException(
                $"The context of type '{context.GetType().FullName}' is not a context created by " +
                "InterceptorSubjectContext.Create(). IInterceptorSubjectContext cannot be implemented " +
                "independently: interceptor chains compile inside the built-in implementation, so a " +
                "foreign context would attach without any interception.");
        }

        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Revision != expectedRevision)
            {
                currentRevision = current.Revision;
                return false;
            }

            if (context is not null && current.Context is not null && !ReferenceEquals(current.Context, context))
            {
                throw new InvalidOperationException(
                    "Cannot attach the subject directly to a different context. Detach it to null first.");
            }

            currentRevision = current.Revision + 1;
            _attachment = new AttachmentState((InterceptorSubjectContext?)context, anchor, currentRevision);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetAttachment(out IInterceptorSubjectContext? context, out SubjectAttachmentAnchorKind anchor, out long revision)
    {
        var attachment = _attachment;
        context = attachment.Context;
        anchor = attachment.Anchor;
        revision = attachment.Revision;
        return context is not null;
    }

    /// <summary>
    /// Returns the subject's executor, publishing one on first access. Call it from the subject's
    /// <see cref="IInterceptorSubject.Executor"/> accessor, passing that subject's own backing field.
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

    /// <summary>
    /// The chain an unattached subject's scalar write runs: nothing intercepts, so this is the
    /// zero-interceptor chain, the terminal write with its commit bookkeeping. Reads and method
    /// invocations need no counterpart because their zero-interceptor chains are the plain
    /// operations.
    /// </summary>
    private static class UninterceptedChain<TProperty>
    {
        internal static readonly WriteAction<TProperty> Write =
            WriteInterceptorFactory<TProperty>.Create(ImmutableArray<IWriteInterceptor>.Empty);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            // The zero-interceptor read chain is the plain read, no terminal lock; see
            // ReadInterceptorFactory.
            return readValue(_subject);
        }

        var context = new PropertyReadContext<TProperty>(this, new PropertyReference(_subject, propertyName));
        return attachedContext.ExecuteInterceptedRead(ref context, readValue);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // The routing flag and the chain index are two fields of one per-type static class, read
        // together and threaded down, so a write pays for the generic statics access exactly once.
        var propertyTypeIndex = InterceptorSubjectContext.PropertyTypeIndex<TProperty>.Value;
        if (InterceptorSubjectContext.PropertyTypeIndex<TProperty>.CanContainSubjects)
        {
            return SetStructuralPropertyValue(propertyName, newValue, currentValue, writeValue, propertyTypeIndex);
        }

        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            UninterceptedChain<TProperty>.Write(ref context, writeValue);
        }
        else
        {
            attachedContext.ExecuteInterceptedWrite(propertyTypeIndex, ref context, writeValue);
        }

        return context.IsWritten;
    }

    /// <summary>
    /// The structural branch of the unified <c>SetPropertyValue</c> entry above: coordinates with the
    /// lifecycle that owns the subject so an attach or detach racing this write orders against it
    /// rather than failing it. Kept out of the unified entry's body so the scalar route stays
    /// small enough to inline.
    /// </summary>
    private bool SetStructuralPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, int propertyTypeIndex)
    {
        // Lock order and why the gate comes first: see the note on _attachmentLock. Holding the
        // monitor from before chain resolution through the terminal is what turns a racing
        // attachment transition into ordering: the transition waits on the monitor instead of
        // invalidating an in-flight write.
        //
        // The routing decision (is there a lifecycle to gate on?) and the write chain both derive
        // from one pinned context state. Pinning is what keeps them consistent: a chain resolved
        // from a second, fresh read could contain a lifecycle the routing did not see, and its
        // WriteProperty would take the gate inside the attachment monitor, inverting the lock
        // order above.
        while (true)
        {
            var attachedContext = _attachment.Context;
            var contextState = attachedContext?.PinState();
            var lifecycle = attachedContext?.TryGetServiceFromState<ILifecycleInterceptor>(contextState!);
            if (lifecycle is null)
            {
                // No lifecycle to order against: either the subject is unattached, or the pinned
                // state has no lifecycle. A lifecycle registered on the attached context after the
                // pin is invisible to this write as a whole, routing and chain alike, and is seen
                // by the next write, which pins a fresh state. One assumption remains: interceptors
                // resolved through a lifecycle-free state must not take another context's lifecycle
                // gate inside this chain.
                lock (_attachmentLock)
                {
                    if (ReferenceEquals(_attachment.Context, attachedContext))
                    {
                        return WriteStructuralValue(attachedContext, contextState, propertyName, newValue, currentValue, writeValue, propertyTypeIndex);
                    }
                }
            }
            else
            {
                lifecycle.EnterStructuralWriteGate();
                try
                {
                    lock (_attachmentLock)
                    {
                        // Revalidate under both locks: the attachment may have moved between the
                        // lock-free read above and the acquisitions. Falling out of the lock scopes
                        // releases both and retries against the fresh attachment.
                        if (ReferenceEquals(_attachment.Context, attachedContext))
                        {
                            return WriteStructuralValue(attachedContext, contextState, propertyName, newValue, currentValue, writeValue, propertyTypeIndex);
                        }
                    }
                }
                finally
                {
                    lifecycle.ExitStructuralWriteGate();
                }
            }
        }
    }

    /// <summary>
    /// Commits a structural write while the caller holds the attachment monitor (and the lifecycle
    /// gate when the routing found a lifecycle). <paramref name="attachedContext"/> and
    /// <paramref name="contextState"/> are the routing snapshot pair, revalidated by the caller
    /// under the monitor and null together exactly when the subject was unattached at routing
    /// time; the chain resolves from that pinned state so it cannot disagree with the routing.
    /// </summary>
    private bool WriteStructuralValue<TProperty>(
        InterceptorSubjectContext? attachedContext,
        InterceptorSubjectContext.ContextState? contextState,
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Action<IInterceptorSubject, TProperty> writeValue,
        int propertyTypeIndex)
    {
        if (attachedContext is null || contextState is null)
        {
            // Unattached: nothing intercepts this subject, so the write is a plain backing store,
            // as cheap as the pre-executor short circuit in the generated helper. It still runs
            // under the attachment monitor, so a concurrent attach either sees the committed value
            // when it seeds or waits until this write is done.
            writeValue(_subject, newValue);
            return true;
        }

        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        attachedContext.ExecuteInterceptedWrite(contextState, propertyTypeIndex, ref context, writeValue);
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

        // Deliberately never structural, unlike the public entry: the cascade targets derived
        // properties, whose values never establish edges, and the trigger may already hold the
        // attachment monitor, so entering the lifecycle gate here would invert the lock order.
        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            UninterceptedChain<TProperty>.Write(ref context, writeValue);
        }
        else
        {
            attachedContext.ExecuteInterceptedWrite(
                InterceptorSubjectContext.PropertyTypeIndex<TProperty>.Value, ref context, writeValue);
        }

        return context.IsWritten;
    }

    /// <inheritdoc />
    public void AddProperties(SubjectPropertyRegistration registration)
    {
        if (!ReferenceEquals(registration.Subject, _subject))
        {
            throw new InvalidOperationException(
                "The registration belongs to a different subject than this executor.");
        }

        // Same routing shape as SetStructuralPropertyValue: resolve the lifecycle from a lock-free
        // attachment read, let the lifecycle order the admission behind its own gate, and treat a
        // stale routing decision as a retry rather than an error. The unattached (or
        // lifecycle-free) arm publishes under the attachment monitor alone, so a concurrent attach
        // either sees the published metadata when it seeds or waits until the publication is done.
        // No state pin is needed here: that arm resolves no interceptor chain, so there is no
        // second state read that could disagree with the routing.
        while (true)
        {
            var attachedContext = _attachment.Context;
            var lifecycle = attachedContext?.TryGetService<ILifecycleInterceptor>();
            if (lifecycle is null)
            {
                lock (_attachmentLock)
                {
                    if (ReferenceEquals(_attachment.Context, attachedContext))
                    {
                        registration.Publish();
                        return;
                    }
                }
            }
            else if (lifecycle.TryAddProperties(registration))
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            // The zero-interceptor invoke chain is the direct invocation; see MethodInvocationFactory.
            return invokeMethod(_subject, parameters);
        }

        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return attachedContext.ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    /// <summary>
    /// The attachment triple as one immutable value: the exact attached context, the anchor that
    /// holds the subject there, and the revision labelling that state. Published by a single
    /// volatile reference store, which is what makes the three coherent for a lock-free reader.
    /// </summary>
    private sealed class AttachmentState
    {
        /// <summary>The state every executor starts in, shared because it carries no identity.</summary>
        internal static readonly AttachmentState Unattached = new(null, SubjectAttachmentAnchorKind.None, 0);

        internal AttachmentState(InterceptorSubjectContext? context, SubjectAttachmentAnchorKind anchor, long revision)
        {
            Context = context;
            Anchor = anchor;
            Revision = revision;
        }

        internal readonly InterceptorSubjectContext? Context;

        internal readonly SubjectAttachmentAnchorKind Anchor;

        internal readonly long Revision;
    }
}
