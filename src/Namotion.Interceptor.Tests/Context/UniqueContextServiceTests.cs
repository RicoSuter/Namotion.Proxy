using System.Reflection;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

public class UniqueContextServiceTests
{
    private static readonly MethodInfo GetServicesFromStateMethod = typeof(InterceptorSubjectContext)
        .GetMethod("GetServicesFromState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "InterceptorSubjectContext.GetServicesFromState was renamed, the context tests need updating.");

    [Fact]
    public void WhenNoUniqueServicesAreReachable_ThenOrderedServicesResolveNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService("first");
        context.AddService("second");

        // Act
        var services = context.GetServices<string>();

        // Assert
        Assert.Equal(["first", "second"], services.ToArray());
    }

    [Fact]
    public void WhenOneUniqueServiceIsReachable_ThenItResolvesNormally()
    {
        // Arrange
        var service = new UniqueAlpha("only");
        var context = InterceptorSubjectContext.Create();
        context.AddService(service);

        // Act
        var services = context.GetServices<UniqueAlpha>();

        // Assert
        Assert.Same(service, Assert.Single(services));
    }

    [Fact]
    public void WhenTwoDistinctUniqueServicesAreReachable_ThenAnUnrelatedQueryThrows()
    {
        // Arrange
        var first = InterceptorSubjectContext.Create();
        first.AddService(new UniqueAlpha("first"));
        var second = InterceptorSubjectContext.Create();
        second.AddService(new UniqueAlpha("second"));
        var root = InterceptorSubjectContext.Create();
        root.AddFallbackContext(first);
        root.AddFallbackContext(second);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => root.GetServices<string>());

        // Assert
        Assert.Contains(typeof(IUniqueAlpha).FullName!, exception.Message);
        Assert.Contains(typeof(UniqueAlpha).FullName!, exception.Message);
    }

    [Fact]
    public void WhenDistinctUniqueServicesCompareEqual_ThenReferenceIdentityStillConflicts()
    {
        // Arrange
        var first = new UniqueAlpha("first");
        var second = new UniqueAlpha("second");
        var root = InterceptorSubjectContext.Create();
        root.AddService(first);
        root.AddService(second);

        // Act
        var areEqual = first.Equals(second);
        var exception = Record.Exception(() => root.GetServices<UniqueAlpha>());

        // Assert
        Assert.True(areEqual);
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void WhenAnUnrelatedUniqueAuthorityConflicts_ThenTryGetServiceThrows()
    {
        // Arrange
        var root = InterceptorSubjectContext.Create();
        root.AddService(new UniqueAlpha("first"));
        root.AddService(new UniqueAlpha("second"));
        root.AddService("unrelated");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => root.TryGetService<string>());

        // Assert
        Assert.Contains(typeof(IUniqueAlpha).FullName!, exception.Message);
    }

    [Fact]
    public void WhenTheSameUniqueInstanceIsReachedThroughADiamond_ThenItResolvesOnce()
    {
        // Arrange
        var sharedService = new UniqueAlpha("shared");
        var shared = InterceptorSubjectContext.Create();
        shared.AddService(sharedService);
        var left = InterceptorSubjectContext.Create();
        left.AddFallbackContext(shared);
        var right = InterceptorSubjectContext.Create();
        right.AddFallbackContext(shared);
        var root = InterceptorSubjectContext.Create();
        root.AddFallbackContext(left);
        root.AddFallbackContext(right);

        // Act
        var services = root.GetServices<UniqueAlpha>();

        // Assert
        Assert.Single(services);
        Assert.Same(sharedService, services[0]);
    }

    [Fact]
    public void WhenOneServiceImplementsTwoUniqueContracts_ThenBothContractsAreConstrained()
    {
        // Arrange
        var alphaRoot = InterceptorSubjectContext.Create();
        alphaRoot.AddService(new UniqueAlphaAndBeta());
        alphaRoot.AddService(new UniqueAlpha("alpha"));

        var betaRoot = InterceptorSubjectContext.Create();
        betaRoot.AddService(new UniqueAlphaAndBeta());
        betaRoot.AddService(new UniqueBeta());

        // Act
        var alphaException = Assert.Throws<InvalidOperationException>(
            () => alphaRoot.GetServices<object>());
        var betaException = Assert.Throws<InvalidOperationException>(
            () => betaRoot.GetServices<object>());

        // Assert
        Assert.Contains(typeof(IUniqueAlpha).FullName!, alphaException.Message);
        Assert.Contains(typeof(IUniqueBeta).FullName!, betaException.Message);
    }

    [Fact]
    public void WhenAValidatedTopologyGainsAConflictingService_ThenTheNextQueryThrows()
    {
        // Arrange
        var root = InterceptorSubjectContext.Create();
        root.AddService(new UniqueAlpha("first"));
        _ = root.GetServices<object>();

        var conflicting = InterceptorSubjectContext.Create();
        conflicting.AddService(new UniqueAlpha("second"));

        // Act
        root.AddFallbackContext(conflicting);
        var exception = Assert.Throws<InvalidOperationException>(
            () => root.GetServices<object>());

        // Assert
        Assert.Contains(typeof(IUniqueAlpha).FullName!, exception.Message);
    }

    [Fact]
    public void WhenAValidatedRootSeesAReachableDescendantGainAConflictingAuthority_ThenTheNextQueryThrows()
    {
        // Arrange
        var descendant = InterceptorSubjectContext.Create();
        descendant.AddService(new UniqueAlpha("first"));
        var root = InterceptorSubjectContext.Create();
        // Keep validation on the root's own state so this detects removal of upstream-state invalidation.
        root.AddService("root");
        root.AddFallbackContext(descendant);
        _ = root.GetServices<object>();

        // Act
        descendant.AddService(new UniqueAlpha("second"));
        var exception = Assert.Throws<InvalidOperationException>(
            () => root.GetServices<object>());

        // Assert
        Assert.Contains(typeof(IUniqueAlpha).FullName!, exception.Message);
    }

    [Fact]
    public async Task WhenOldConflictingStateIsValidatedWhileRemovalPublishesARepairedState_ThenOnlyTheRepairedStateSucceeds()
    {
        // Arrange
        var first = InterceptorSubjectContext.Create();
        first.AddService(new UniqueAlpha("first"));
        var conflicting = InterceptorSubjectContext.Create();
        conflicting.AddService(new UniqueAlpha("second"));
        var root = InterceptorSubjectContext.Create();
        root.AddFallbackContext(first);
        root.AddFallbackContext(conflicting);

        var conflictingState = GetState(root);
        using var start = new Barrier(2);
        Exception? validationException = null;
        object? repairedState = null;

        var failedValidation = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            validationException = Assert.Throws<TargetInvocationException>(
                () => GetServicesFromStateMethod
                    .MakeGenericMethod(typeof(string))
                    .Invoke(root, [conflictingState]));
        }, TaskCreationOptions.LongRunning);

        var repair = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            Assert.True(root.RemoveFallbackContext(conflicting));
            repairedState = GetState(root);
            _ = GetServicesFromStateMethod
                .MakeGenericMethod(typeof(string))
                .Invoke(root, [repairedState]);
        }, TaskCreationOptions.LongRunning);

        // Act
        await Task.WhenAll(failedValidation, repair).WaitAsync(TimeSpan.FromSeconds(15));
        var services = GetServicesFromStateMethod
            .MakeGenericMethod(typeof(UniqueAlpha))
            .Invoke(root, [repairedState!]);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(validationException!.InnerException);
        Assert.Contains(typeof(IUniqueAlpha).FullName!, exception.Message);
        Assert.NotSame(conflictingState, repairedState);
        var service = Assert.IsType<System.Collections.Immutable.ImmutableArray<UniqueAlpha>>(services);
        Assert.Single(service);
    }

    private interface IUniqueAlpha
    {
    }

    private interface IUniqueBeta
    {
    }

    private sealed class UniqueAlpha(string name) :
        IUniqueContextService<IUniqueAlpha>
    {
        public override bool Equals(object? obj) => obj is UniqueAlpha;

        public override int GetHashCode() => 0;

        public override string ToString() => name;
    }

    private sealed class UniqueBeta : IUniqueContextService<IUniqueBeta>
    {
    }

    private sealed class UniqueAlphaAndBeta :
        IUniqueContextService<IUniqueAlpha>,
        IUniqueContextService<IUniqueBeta>
    {
    }
}
