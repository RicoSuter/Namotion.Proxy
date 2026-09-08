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
    private static int _propertyCallbackDepth;

    /// <summary>Called on entry of every topology mutation: the structural write protocol, an
    /// explicit attach and an explicit detach.</summary>
    public static void ThrowIfInsideCallback()
    {
        if (_propertyCallbackDepth > 0 && _deliveryDepth == 0)
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

    public static PropertyCallbackScope EnterPropertyCallbackScope()
    {
        _propertyCallbackDepth++;
        return default;
    }

    internal readonly struct PropertyCallbackScope : IDisposable
    {
        public void Dispose()
        {
            _propertyCallbackDepth--;
        }
    }
}
