namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Runs a subject's interception and owns its exact context attachment: the one nullable attached
/// context, the anchor, and the attachment revision with its compare-and-swap transitions. The
/// executor is not a service container; services live on the attached
/// <see cref="IInterceptorSubjectContext"/>, reachable through <see cref="AttachedContext"/>.
/// </summary>
/// <remarks>
/// Not independently implementable: the chain terminals, the terminal lock and the commit
/// revision live on <see cref="InterceptorExecutor"/>, and library paths cast to it. Subject
/// implementations publish one through <see cref="InterceptorExecutor.GetOrCreate"/>.
/// </remarks>
public interface IInterceptorExecutor
{
    /// <summary>
    /// Gets the one exact context this subject is attached to, or null when the subject is
    /// unattached.
    /// </summary>
    IInterceptorSubjectContext? AttachedContext { get; }

    /// <summary>
    /// Gets what anchors the subject to <see cref="AttachedContext"/>. Always
    /// <see cref="SubjectAttachmentAnchorKind.None"/> when <see cref="AttachedContext"/> is null.
    /// </summary>
    SubjectAttachmentAnchorKind AttachmentAnchor { get; }

    /// <summary>
    /// Gets the attachment revision: monotonic per executor, incremented on every successful
    /// <see cref="TryUpdateAttachment"/> call, and never reset, so it stays comparable across
    /// detach and reattach. This is a separate counter from the per-subject commit revision.
    /// </summary>
    long AttachmentRevision { get; }

    /// <summary>
    /// Applies an attachment transition with compare-and-swap semantics: succeeds only when
    /// <paramref name="expectedRevision"/> still equals <see cref="AttachmentRevision"/>, applies
    /// <see cref="AttachedContext"/> and <see cref="AttachmentAnchor"/> atomically and bumps the revision.
    /// This is the raw transition seam for lifecycle implementations outside this assembly.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="context"/> requires <see cref="SubjectAttachmentAnchorKind.None"/>, and a
    /// direct swap from one non-null context to a different non-null context is illegal (detach to
    /// null first); both are rejected before any state changes. Every successful call bumps the
    /// revision, even when it applies the values already in place.
    /// </remarks>
    /// <param name="expectedRevision">The attachment revision the caller observed.</param>
    /// <param name="context">The exact context to attach, or null to detach.</param>
    /// <param name="anchor">The anchor to apply alongside the context.</param>
    /// <param name="currentRevision">The revision after the transition on success, or the current
    /// revision on failure so the caller can retry or give up.</param>
    /// <returns>True when the transition was applied; false when the expected revision was stale.</returns>
    /// <exception cref="InvalidOperationException">The requested state shape is illegal.</exception>
    bool TryUpdateAttachment(long expectedRevision, IInterceptorSubjectContext? context, SubjectAttachmentAnchorKind anchor, out long currentRevision);

    /// <summary>
    /// Reads <see cref="AttachedContext"/>, <see cref="AttachmentAnchor"/> and
    /// <see cref="AttachmentRevision"/> as one coherent snapshot: all three come from the same
    /// attachment state, so they can never be combined across a transition. Lock-free, and the
    /// only way to observe more than one of the three coherently, since reading the individual
    /// getters in sequence pairs values from whichever states happened to be current. Use it to
    /// observe the state a <see cref="TryUpdateAttachment"/> call should be based on.
    /// </summary>
    /// <param name="context">The attached context, or null when unattached.</param>
    /// <param name="anchor">The anchor belonging to <paramref name="context"/>.</param>
    /// <param name="revision">The attachment revision the snapshot belongs to.</param>
    /// <returns>True when a context is attached. The out values are valid either way.</returns>
    bool TryGetAttachment(out IInterceptorSubjectContext? context, out SubjectAttachmentAnchorKind anchor, out long revision);

    /// <summary>
    /// Gets a property value through the interceptor chain.
    /// </summary>
    /// <param name="propertyName">The name of the property to read.</param>
    /// <param name="readValue">A delegate that reads the backing field value from the subject.</param>
    TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue);

    /// <summary>
    /// Sets a property value through the interceptor chain with the current value already known.
    /// The write routes at runtime on <typeparamref name="TProperty"/>: a type that can contain
    /// subjects takes the structural write protocol, coordinating with the lifecycle that owns the
    /// subject so an attach or detach racing this write orders against it rather than failing it;
    /// any other type writes without that synchronization. Only a persistent conflict, a subject
    /// genuinely owned by another context, throws, and it throws before the backing field is
    /// written. The classification follows <typeparamref name="TProperty"/> alone: a
    /// <typeparamref name="TProperty"/> that can contain subjects routes structurally, so a boxed
    /// <c>object</c> fails closed to the structural side, while explicitly narrowing
    /// <typeparamref name="TProperty"/> below the declared property type routes scalar and
    /// forfeits this entry's pre-chain coordination (the lifecycle still takes its own gate inside
    /// the chain, so ownership stays consistent). Callers whose values travel boxed instantiate
    /// this entry with the declared property type via a cached typed delegate instead. The lock order
    /// and the context-state pinning the structural route relies on are documented once, under
    /// "The Write Protocol" in docs/design/tracking-lifecycle.md, rather than restated here where
    /// they drift out of date.
    /// </summary>
    /// <param name="propertyName">The name of the property to write.</param>
    /// <param name="newValue">The new value to set.</param>
    /// <param name="currentValue">The current value of the property.</param>
    /// <param name="writeValue">A delegate that writes the new value to the backing field.</param>
    /// <returns>True if the value was written; false if the write was suppressed by an interceptor.</returns>
    bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue);

    /// <summary>
    /// Invokes a method through the interceptor chain.
    /// </summary>
    /// <param name="methodName">The name of the method to invoke.</param>
    /// <param name="parameters">The method parameters.</param>
    /// <param name="invokeMethod">A delegate that performs the actual method invocation on the subject.</param>
    /// <returns>The return value of the method invocation.</returns>
    object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod);

    /// <summary>
    /// Routes an <see cref="IInterceptorSubject.AddProperties"/> call. When the subject is attached
    /// to a context with an <see cref="ILifecycleInterceptor"/>, the batch is handed to that
    /// lifecycle through <see cref="ILifecycleInterceptor.TryAddProperties"/> so metadata,
    /// ownership edges and property callbacks publish as one admission; a stale routing decision
    /// retries against the fresh attachment, so a racing attachment transition orders against the
    /// call instead of failing it. An unattached subject (or one attached to a lifecycle-free
    /// context) publishes the metadata directly under the attachment monitor, with no ownership
    /// work, which is what serializes the publication against a concurrent attach.
    /// </summary>
    /// <param name="registration">The registration carrying the batch; its subject must be the
    /// subject this executor belongs to.</param>
    void AddProperties(SubjectPropertyRegistration registration);
}