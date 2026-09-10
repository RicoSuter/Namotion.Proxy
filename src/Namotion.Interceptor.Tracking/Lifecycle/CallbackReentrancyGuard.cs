namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Enforces the callback contract: a lifecycle callback (an <see cref="ILifecycleHandler"/>
/// invocation, a subject attach or detach event, a collection refresh, or a property lifecycle
/// callback) may evaluate anything and may change no graph topology: no structural property
/// write, and no explicit attach or detach. The lifecycle publishes callbacks while it holds the
/// topology gate in the middle of reconciling an edge set, so a topology change from a callback
/// would re-enter the reconciler on half-updated state. The depth is thread-local and shared
/// across built-in lifecycle instances, so a callback writing into another context's graph is
/// detected too, and the guard is live in every build because the silent failure mode is graph
/// corruption. Reaching a second context's topology gate is rejected separately, by the
/// one-transaction-per-thread rule in <see cref="LifecycleInterceptor"/>.
/// </summary>
/// <remarks>
/// The rule is uniform at every graph depth: the property lifecycle callbacks
/// (<see cref="IPropertyLifecycleHandler.AttachProperty"/> and
/// <see cref="IPropertyLifecycleHandler.DetachProperty"/>) are not exempt, so the derived-property
/// handler may evaluate user getters from its attach callback but may not write topology. Scalar
/// writes from callbacks stay allowed. The guard does not bind code running at callback depth zero
/// downstream of the lifecycle, such as a third-party write interceptor during <c>next</c>; the
/// ownership check at <see cref="StructuralReconciler"/> entry and the released-parent exits inside
/// its loops handle that shape.
/// </remarks>
internal static class CallbackReentrancyGuard
{
    [ThreadStatic]
    private static int _callbackDepth;

    [ThreadStatic]
    private static int _propertyCallbackDepth;

    /// <summary>
    /// Whether the current thread is executing any lifecycle callback of some built-in lifecycle,
    /// property lifecycle callbacks included.
    /// </summary>
    public static bool IsInsideAnyCallback => _callbackDepth > 0 || _propertyCallbackDepth > 0;

    /// <summary>
    /// Marks the thread as executing a lifecycle callback for the lifetime of the returned scope.
    /// A scope rather than a bare increment, so a violating callback that throws cannot poison the
    /// thread's guard state for later operations.
    /// </summary>
    public static CallbackScope EnterScope()
    {
        _callbackDepth++;
        return default;
    }

    /// <summary>Called on entry of every topology mutation: the structural write protocol, an
    /// explicit attach and an explicit detach.</summary>
    public static void ThrowIfInsideCallback()
    {
        if (IsInsideAnyCallback)
        {
            throw new LifecycleContractViolationException(
                "A lifecycle callback must not change graph topology: no structural " +
                "(subject-typed) property write, and no explicit attach or detach. The callback " +
                "runs while the lifecycle holds its topology gate mid-reconcile, so the change " +
                "would re-enter the reconciler on half-updated edge state, and reaching a second " +
                "lifecycle's gate from inside a callback can deadlock. Defer the change until the " +
                "triggering operation completes.");
        }
    }

    /// <inheritdoc cref="EnterScope"/>
    public static PropertyCallbackScope EnterPropertyCallbackScope()
    {
        _propertyCallbackDepth++;
        return default;
    }

    internal readonly struct CallbackScope : IDisposable
    {
        public void Dispose()
        {
            _callbackDepth--;
        }
    }

    internal readonly struct PropertyCallbackScope : IDisposable
    {
        public void Dispose()
        {
            _propertyCallbackDepth--;
        }
    }
}
