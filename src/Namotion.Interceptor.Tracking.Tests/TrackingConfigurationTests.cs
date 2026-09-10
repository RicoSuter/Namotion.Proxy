using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Recorder;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests;

/// <summary>
/// Every Tracking configuration extension is idempotent for its own default service, and each
/// default's singleton contract turns a competing registration for the same slot into an error
/// instead of a silent second authority. Extensions that depend on the lifecycle establish it
/// first, so a lifecycle conflict throws before any dependent service is published.
/// </summary>
public class TrackingConfigurationTests
{
    [Fact]
    public void WhenFullPropertyTrackingIsConfiguredRepeatedly_ThenEachServiceIsRegisteredOnce()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithFullPropertyTracking();

        // Assert
        Assert.Single(context.GetServices<PropertyValueEqualityCheckHandler>());
        Assert.Single(context.GetServices<DerivedPropertyChangeHandler>());
        Assert.Single(context.GetServices<PropertyChangeInterceptor>());
        Assert.Single(context.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenFullPropertyTrackingPublishesNothing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert: the lifecycle conflict must surface before any dependent service lands,
        // so the failed configuration leaves no half-installed tracking pipeline behind.
        Assert.Throws<InvalidOperationException>(() => context.WithFullPropertyTracking());
        Assert.Null(context.TryGetService<PropertyValueEqualityCheckHandler>());
        Assert.Null(context.TryGetService<DerivedPropertyChangeHandler>());
        Assert.Null(context.TryGetService<PropertyChangeInterceptor>());
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenDerivedPropertyChangeDetectionPublishesNoHandler()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.WithDerivedPropertyChangeDetection());
        Assert.Null(context.TryGetService<DerivedPropertyChangeHandler>());
    }

    [Fact]
    public void WhenEqualityCheckIsConfiguredRepeatedly_ThenOneHandlerIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithEqualityCheck()
            .WithEqualityCheck();

        // Assert
        Assert.Single(context.GetServices<PropertyValueEqualityCheckHandler>());
    }

    [Fact]
    public void WhenTheEqualityCheckContractIsClaimed_ThenWithEqualityCheckThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new EqualityCheckSlotClaimant());

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithEqualityCheck());
        Assert.Contains("singleton contract", exception.Message);
        Assert.Null(context.TryGetService<PropertyValueEqualityCheckHandler>());
    }

    [Fact]
    public void WhenPropertyChangeSubscriptionsAreConfiguredRepeatedly_ThenOneInterceptorIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions()
            .WithPropertyChangeSubscriptions();

        // Assert
        Assert.Single(context.GetServices<PropertyChangeInterceptor>());
    }

    [Fact]
    public void WhenThePropertyChangeContractIsClaimed_ThenWithPropertyChangeSubscriptionsThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new PropertyChangeSlotClaimant());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.WithPropertyChangeSubscriptions());
        Assert.Null(context.TryGetService<PropertyChangeInterceptor>());
    }

    [Fact]
    public void WhenASecondPropertyChangeInterceptorIsRegisteredDirectly_ThenItThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();

        // Act & Assert: a second instance would split the change stream between two authorities,
        // with every consumer silently hearing only the one it happened to resolve.
        var exception = Assert.Throws<InvalidOperationException>(
            () => context.AddService(new PropertyChangeInterceptor()));
        Assert.Contains("singleton contract", exception.Message);
    }

    [Fact]
    public void WhenReadPropertyRecorderIsConfiguredRepeatedly_ThenOneRecorderIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithReadPropertyRecorder()
            .WithReadPropertyRecorder();

        // Assert
        Assert.Single(context.GetServices<ReadPropertyRecorder>());
    }

    [Fact]
    public void WhenTheReadPropertyRecorderContractIsClaimed_ThenWithReadPropertyRecorderThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new ReadPropertyRecorderSlotClaimant());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.WithReadPropertyRecorder());
        Assert.Null(context.TryGetService<ReadPropertyRecorder>());
    }

    [Fact]
    public void WhenTransactionsAreConfiguredRepeatedly_ThenOneInterceptorIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithTransactions()
            .WithTransactions();

        // Assert
        Assert.Single(context.GetServices<SubjectTransactionInterceptor>());
    }

    [Fact]
    public void WhenTheTransactionInterceptorContractIsClaimed_ThenWithTransactionsThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new TransactionInterceptorSlotClaimant());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.WithTransactions());
        Assert.Null(context.TryGetService<SubjectTransactionInterceptor>());
    }

    [Fact]
    public void WhenTheDerivedPropertyChangeContractIsClaimed_ThenTheLifecycleStaysAndNoHandlerIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new DerivedPropertyChangeSlotClaimant());

        // Act & Assert: the lifecycle is the extension's dependency and is established first, so
        // it survives the conflict; the extension's own service must not.
        Assert.Throws<InvalidOperationException>(() => context.WithDerivedPropertyChangeDetection());
        Assert.NotNull(context.TryGetService<ILifecycleInterceptor>());
        Assert.Null(context.TryGetService<DerivedPropertyChangeHandler>());
    }

    private sealed class EqualityCheckSlotClaimant : ISingletonContextService<PropertyValueEqualityCheckHandler>;

    private sealed class PropertyChangeSlotClaimant : ISingletonContextService<PropertyChangeInterceptor>;

    private sealed class ReadPropertyRecorderSlotClaimant : ISingletonContextService<ReadPropertyRecorder>;

    private sealed class TransactionInterceptorSlotClaimant : ISingletonContextService<SubjectTransactionInterceptor>;

    private sealed class DerivedPropertyChangeSlotClaimant : ISingletonContextService<DerivedPropertyChangeHandler>;

    private sealed class CustomLifecycle : ILifecycleInterceptor
    {
        public void EnterStructuralWriteGate()
        {
        }

        public void ExitStructuralWriteGate()
        {
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
            => throw new NotSupportedException();

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
            => throw new NotSupportedException();

        public bool TryAddProperties(SubjectPropertyRegistration registration)
            => throw new NotSupportedException();

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
            => next(ref context);
    }
}
