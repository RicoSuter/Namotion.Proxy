using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// The Connectors configuration extensions are idempotent for their own default services, and the
/// singleton contracts on the transaction-writer slot and the monitor turn a competing
/// registration into an error. Each extension establishes its dependencies first, so a conflict
/// throws before the extension's own service is published.
/// </summary>
public class ConnectorsConfigurationTests
{
    [Fact]
    public void WhenSourceTransactionsAreConfiguredRepeatedly_ThenOneWriterIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithSourceTransactions()
            .WithSourceTransactions();

        // Assert
        Assert.Single(context.GetServices<ITransactionWriter>());
        Assert.Single(context.GetServices<SubjectTransactionInterceptor>());
    }

    [Fact]
    public void WhenACustomTransactionWriterIsRegistered_ThenWithSourceTransactionsKeepsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var customWriter = new StubTransactionWriter();
        context.AddService<ITransactionWriter>(customWriter);

        // Act: first writer wins, so no default writer is constructed beside the custom one and the
        // writer slot's singleton contract is never contested.
        context.WithSourceTransactions();

        // Assert
        Assert.Same(customWriter, Assert.Single(context.GetServices<ITransactionWriter>()));
        Assert.Single(context.GetServices<SubjectTransactionInterceptor>());
    }

    [Fact]
    public void WhenTheTransactionInterceptorContractIsClaimed_ThenWithSourceTransactionsPublishesNoWriter()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new TransactionInterceptorSlotClaimant());

        // Act & Assert: the conflict fires while establishing the transaction interceptor, before
        // the writer is published, so no half-configured transaction pipeline is left behind.
        Assert.Throws<InvalidOperationException>(() => context.WithSourceTransactions());
        Assert.Null(context.TryGetService<ITransactionWriter>());
    }

    [Fact]
    public void WhenSourceMonitoringIsConfiguredRepeatedly_ThenOneMonitorIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithSourceMonitoring()
            .WithSourceMonitoring();

        // Assert
        var monitor = Assert.Single(context.GetServices<SourceMonitor>());
        Assert.Same(monitor, context.GetSourceMonitor());
    }

    [Fact]
    public void WhenSourceMonitoringIsConfigured_ThenOneServiceObjectServesAllRoles()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithSourceMonitoring();

        // Assert: one registration, visible through every implemented role by assignability.
        var monitor = context.GetSourceMonitor();
        var handler = Assert.Single(context.GetServices<ILifecycleHandler>(), h => h is SourceMonitor);
        Assert.Same(monitor, handler);
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenWithSourceMonitoringPublishesNoMonitor()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.WithSourceMonitoring());
        Assert.Null(context.TryGetService<SourceMonitor>());
        Assert.Empty(context.GetServices<ILifecycleHandler>());
    }

    [Fact]
    public void WhenTheSourceMonitorContractIsClaimed_ThenWithSourceMonitoringThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new SourceMonitorSlotClaimant());

        // Act & Assert: the lifecycle is the extension's dependency and is established first, so
        // it survives the conflict; the extension's own service must not.
        Assert.Throws<InvalidOperationException>(() => context.WithSourceMonitoring());
        Assert.NotNull(context.TryGetService<ILifecycleInterceptor>());
        Assert.Null(context.TryGetService<SourceMonitor>());
    }

    private sealed class TransactionInterceptorSlotClaimant : ISingletonContextService<SubjectTransactionInterceptor>;

    private sealed class SourceMonitorSlotClaimant : ISingletonContextService<SourceMonitor>;

    private sealed class StubTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new SourceWriteResult([], [], [], null));

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new SourceRevertResult([], []));
    }

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
