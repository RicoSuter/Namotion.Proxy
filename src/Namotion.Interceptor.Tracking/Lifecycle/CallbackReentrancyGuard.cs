namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Rejects topology writes from callbacks invoked inline during graph reconciliation. Queued
/// delivery permits same-context writes; the lifecycle gate separately rejects a second context.
/// </summary>
internal static class CallbackReentrancyGuard
{
    [ThreadStatic]
    private static int _deliveryDepth;

    public static DeliveryScope EnterDeliveryScope()
    {
        _deliveryDepth++;
        return default;
    }

    internal readonly struct DeliveryScope : IDisposable
    {
        public void Dispose() => _deliveryDepth--;
    }

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
        if (IsInsideAnyCallback && _deliveryDepth == 0)
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
