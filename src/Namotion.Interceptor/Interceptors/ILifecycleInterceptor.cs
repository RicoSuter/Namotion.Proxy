namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// The single authority that owns structural graph membership for one context. Core provides the
/// attachment mechanism (the exact context on the executor and its compare-and-swap transitions);
/// this seam owns the policy: which subjects a structural edge pulls into the context, when an
/// anchor is consumed, and when an unreachable subject is released.
/// </summary>
/// <remarks>
/// A third-party implementation may store any graph representation and choose its own
/// synchronization model. It observes structural writes through <see cref="IWriteInterceptor"/> and
/// applies ownership through the raw executor transitions, so it needs no Tracking internals.
///
/// The seam covers ownership transitions only. Everything layered on top of them, the parent
/// projection, the reference count, the attach and detach events, and therefore the registry and
/// the connectors, binds to the built-in implementation rather than to this interface. Replacing
/// it yields a working ownership model without those.
/// </remarks>
public interface ILifecycleInterceptor :
    IWriteInterceptor,
    ISingletonContextService<ILifecycleInterceptor>
{
    /// <summary>
    /// Enters the synchronization gate a structural write must hold before the subject's
    /// attachment monitor and before the write chain is resolved and executed. The gate is the
    /// outermost lock of the structural write order; the protocol is documented on
    /// <see cref="IInterceptorExecutor.SetPropertyValue{TProperty}"/>.
    /// </summary>
    /// <remarks>
    /// The gate must support reentrant acquisition on one thread (the lifecycle re-enters it from
    /// inside the write chain). The built-in lifecycle enters its per-context topology lock. An
    /// enter/exit pair rather than an exposed lock object, so a consumer cannot take the gate with
    /// an idiomatic-looking <c>lock</c> statement and hang the process against a structural write.
    ///
    /// Who enters it is a fixed convention: Core enters the gate around chain-executing
    /// operations (the structural write seam), while the lifecycle enters it itself inside its
    /// own entry points (attach, detach, property admission). A new lifecycle entry point follows
    /// the second form; a new chain-executing operation the first.
    /// </remarks>
    void EnterStructuralWriteGate();

    /// <summary>
    /// Exits the gate entered by <see cref="EnterStructuralWriteGate"/>. Call from a finally block
    /// paired with that enter.
    /// </summary>
    void ExitStructuralWriteGate();

    /// <summary>
    /// Attaches the subject to <paramref name="context"/> with the given root anchor, together with
    /// every subject its structural properties reach.
    /// </summary>
    /// <remarks>
    /// The whole prospective component is validated and claimed before any callback runs, so a
    /// rejected attach leaves no residue. A subject already owned by <paramref name="context"/> is
    /// promoted to <paramref name="anchor"/> without repeating its attach callbacks, except for a
    /// <see cref="SubjectAttachmentAnchorKind.Provisional"/> request, which is a construction-time
    /// default and never demotes an anchor already in place.
    /// </remarks>
    /// <param name="subject">The subject to attach.</param>
    /// <param name="context">The context to attach to; must be the context this interceptor owns.</param>
    /// <param name="anchor">The anchor to apply to <paramref name="subject"/>. Never
    /// <see cref="SubjectAttachmentAnchorKind.None"/>: an attach without an anchor would be released again by
    /// the next reachability decision.</param>
    /// <exception cref="InvalidOperationException">The subject already carries an explicit anchor,
    /// or the subject or part of its component is owned by a different context.</exception>
    void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor);

    /// <summary>
    /// Clears the subject's root anchor on <paramref name="context"/> and releases the subject when
    /// no structural edge and no other anchor still holds it.
    /// </summary>
    /// <param name="subject">The subject to detach.</param>
    /// <param name="context">The context to detach from; must be the context this interceptor owns.</param>
    /// <exception cref="InvalidOperationException">The subject carries no explicit anchor on that
    /// context.</exception>
    void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context);

    /// <summary>
    /// Admits an <see cref="IInterceptorSubject.AddProperties"/> batch for a subject attached to
    /// the context this lifecycle owns: rejects a cross-context callback before the input is
    /// enumerated, then, under the lifecycle's structural gate, materializes the batch once,
    /// classifies the initial ownership candidates (intercepted, non-derived, subject-capable
    /// declared type, getter available), invokes each qualifying getter exactly once, validates and
    /// claims the complete prospective subgraph, publishes the metadata atomically, invokes the
    /// property lifecycle callbacks in input order, and commits the captured values as ordinary
    /// structural assignments. If enumeration, duplicate validation, a getter, context validation
    /// or claiming fails, nothing is published and provisional claims are released before the
    /// failure escapes.
    /// </summary>
    /// <remarks>
    /// Ownership getters used during admission must be synchronous, stable, side-effect-free,
    /// callable before the metadata is published, and authoritative for the property's initial
    /// stored value; they must not mutate ownership or metadata. Later changes to the stored value
    /// must pass through the property's intercepted setter.
    /// </remarks>
    /// <param name="registration">The registration carrying the batch.</param>
    /// <returns>True when this lifecycle handled the batch. False when the subject was not (or no
    /// longer) attached to this lifecycle's context at admission time; the implementation must have
    /// published nothing in that case, and the caller re-routes against the fresh attachment.</returns>
    /// <exception cref="InvalidOperationException">The call happened inside a lifecycle callback of
    /// another context (rejected before enumeration, because taking a second lifecycle gate there
    /// can deadlock against opposing callbacks), a property name is duplicated, or part of the
    /// captured component belongs to a different context.</exception>
    bool TryAddProperties(SubjectPropertyRegistration registration);
}
