using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Pins the resolved write chain. The order carries semantics that are otherwise only visible
/// through behavior: the change interceptor must sit outer of the lifecycle interceptor (so
/// dispatch happens after attach/detach reconciliation) and inner of the derived handler (so a
/// triggering write is announced before the recalculations it causes).
/// </summary>
public class WritePipelineOrderTests
{
    [Fact]
    public void WhenFullPropertyTrackingIsRegistered_ThenWriteChainHasTheExpectedOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act: index 0 is entered first, so it is the outermost interceptor.
        var chain = context.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();

        // Assert
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(DerivedPropertyChangeHandler),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }

    [Fact]
    public void WhenTransactionsAreRegistered_ThenTransactionInterceptorIsOuterOfTheChangeInterceptor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();

        // Act
        var chain = context.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();

        // Assert
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(SubjectTransactionInterceptor),
                typeof(DerivedPropertyChangeHandler),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }

    [Fact]
    public void WhenContextsAreAggregated_ThenEveryChangeInterceptorPrecedesEveryLifecycleInterceptor()
    {
        // Arrange: each context contributes its own non-unique interceptors under one shared
        // lifecycle authority, so ordering edges must bind across the complete resolved cone.
        var lifecycle = new LifecycleInterceptor();
        var parentContext = InterceptorSubjectContext.Create();
        var childContext = InterceptorSubjectContext.Create();
        parentContext.AddService(lifecycle);
        childContext.AddService(lifecycle);
        parentContext.WithFullPropertyTracking();
        childContext.WithFullPropertyTracking();
        childContext.AddFallbackContext(parentContext);

        // Act
        var chain = childContext.GetServices<IWriteInterceptor>().Select(interceptor => interceptor.GetType()).ToArray();

        // Assert: an exact array, because this is the only configuration where the derived-before-change
        // edge is load-bearing. A weaker "all change before all lifecycle" predicate is satisfied by
        // the interleaved order too, so removing that edge would not fail any test.
        Assert.Equal(
            [
                typeof(PropertyValueEqualityCheckHandler),
                typeof(PropertyValueEqualityCheckHandler),
                typeof(DerivedPropertyChangeHandler),
                typeof(DerivedPropertyChangeHandler),
                typeof(PropertyChangeInterceptor),
                typeof(PropertyChangeInterceptor),
                typeof(LifecycleInterceptor)
            ],
            chain);
    }
}
