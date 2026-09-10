using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ContextConfigurationCompositionTests
{
    public enum LifecycleConfiguration
    {
        Lifecycle,
        DerivedPropertyChangeDetection,
        ContextInheritance,
        Parents,
        FullPropertyTracking
    }

    public enum NonUniqueConfiguration
    {
        EqualityCheck,
        ReadPropertyRecorder,
        PropertyChangeSubscriptions,
        PropertyValidation,
        DataAnnotationValidation
    }

    [Theory]
    [InlineData(LifecycleConfiguration.Lifecycle)]
    [InlineData(LifecycleConfiguration.DerivedPropertyChangeDetection)]
    [InlineData(LifecycleConfiguration.ContextInheritance)]
    [InlineData(LifecycleConfiguration.Parents)]
    [InlineData(LifecycleConfiguration.FullPropertyTracking)]
    public void WhenLifecycleEstablishingContextsAreConfiguredBeforeComposition_ThenResolutionThrows(
        LifecycleConfiguration configuration)
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        Configure(parent, configuration);
        Configure(child, configuration);
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(ILifecycleInterceptor).FullName!, exception.Message);
    }

    [Fact]
    public void WhenContextsAreComposedBeforeLifecycleConfiguration_ThenTheyShareOneAuthority()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithLifecycle();
        var child = InterceptorSubjectContext.Create();
        child.AddFallbackContext(parent);

        // Act
        child.WithParents();

        // Assert
        Assert.Same(
            parent.GetService<ILifecycleInterceptor>(),
            child.GetService<ILifecycleInterceptor>());
        Assert.Single(child.GetServices<ILifecycleInterceptor>());
        Assert.Single(child.GetServices<ParentTrackingHandler>());
    }

    [Fact]
    public void WhenLifecycleIsConfiguredAfterCompositionWithACustomAuthority_ThenItReusesThatAuthority()
    {
        // Arrange
        var lifecycle = new CustomLifecycleInterceptor();
        var parent = InterceptorSubjectContext.Create();
        parent.AddService<ILifecycleInterceptor>(lifecycle);
        var child = InterceptorSubjectContext.Create();
        child.AddFallbackContext(parent);

        // Act
        child.WithLifecycle();

        // Assert
        Assert.Same(lifecycle, child.GetService<ILifecycleInterceptor>());
        Assert.Single(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenTransactionContextsAreConfiguredBeforeComposition_ThenResolutionThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithTransactions();
        var child = InterceptorSubjectContext.Create().WithTransactions();
        child.AddFallbackContext(parent);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => child.GetServices<object>());

        // Assert
        Assert.Contains(typeof(SubjectTransactionInterceptor).FullName!, exception.Message);
    }

    [Fact]
    public async Task WhenTransactionsAreConfiguredAfterComposition_ThenTheyReuseTheCoordinatorAndCommit()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithTransactions();
        var child = InterceptorSubjectContext.Create();
        child.AddFallbackContext(parent);
        child.WithTransactions();
        var person = new Person(child);

        // Act
        using (var transaction = await child.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Assert
            Assert.Same(
                parent.GetService<SubjectTransactionInterceptor>(),
                child.GetService<SubjectTransactionInterceptor>());
            Assert.Single(transaction.GetPendingChanges());

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("John", person.FirstName);
    }

    [Theory]
    [InlineData(NonUniqueConfiguration.EqualityCheck, 2)]
    [InlineData(NonUniqueConfiguration.ReadPropertyRecorder, 2)]
    [InlineData(NonUniqueConfiguration.PropertyChangeSubscriptions, 2)]
    [InlineData(NonUniqueConfiguration.PropertyValidation, 2)]
    [InlineData(NonUniqueConfiguration.DataAnnotationValidation, 4)]
    public void WhenNonUniqueServiceContextsAreConfiguredBeforeComposition_ThenAllServicesResolve(
        NonUniqueConfiguration configuration,
        int expectedServiceCount)
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create();
        var child = InterceptorSubjectContext.Create();
        Configure(parent, configuration);
        Configure(child, configuration);
        child.AddFallbackContext(parent);

        // Act
        var services = child.GetServices<object>();

        // Assert
        Assert.Equal(expectedServiceCount, services.Length);
    }

    private static void Configure(
        IInterceptorSubjectContext context,
        LifecycleConfiguration configuration)
    {
        _ = configuration switch
        {
            LifecycleConfiguration.Lifecycle => context.WithLifecycle(),
            LifecycleConfiguration.DerivedPropertyChangeDetection =>
                context.WithDerivedPropertyChangeDetection(),
            LifecycleConfiguration.ContextInheritance => context.WithContextInheritance(),
            LifecycleConfiguration.Parents => context.WithParents(),
            LifecycleConfiguration.FullPropertyTracking => context.WithFullPropertyTracking(),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
    }

    private static void Configure(
        IInterceptorSubjectContext context,
        NonUniqueConfiguration configuration)
    {
        _ = configuration switch
        {
            NonUniqueConfiguration.EqualityCheck => context.WithEqualityCheck(),
            NonUniqueConfiguration.ReadPropertyRecorder => context.WithReadPropertyRecorder(),
            NonUniqueConfiguration.PropertyChangeSubscriptions =>
                context.WithPropertyChangeSubscriptions(),
            NonUniqueConfiguration.PropertyValidation => context.WithPropertyValidation(),
            NonUniqueConfiguration.DataAnnotationValidation => context.WithDataAnnotationValidation(),
            _ => throw new ArgumentOutOfRangeException(nameof(configuration))
        };
    }

    private sealed class CustomLifecycleInterceptor : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
        }
    }
}
