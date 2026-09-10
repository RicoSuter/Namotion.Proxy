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

}
