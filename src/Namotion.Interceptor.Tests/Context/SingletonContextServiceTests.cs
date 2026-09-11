namespace Namotion.Interceptor.Tests.Context;

public class SingletonContextServiceTests
{
    [Fact]
    public void WhenSecondSingletonContractIsAdded_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var existingService = new FirstContractAuthority();
        context.AddService(existingService);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => context.AddService(new CompetingFirstContractAuthority()));

        Assert.Contains(nameof(IFirstContract), exception.Message);
        Assert.Contains(nameof(FirstContractAuthority), exception.Message);
        Assert.Contains(nameof(CompetingFirstContractAuthority), exception.Message);

        // The rejected registration must leave the context untouched.
        Assert.Same(existingService, Assert.Single(context.GetServices<IFirstContract>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenSameSingletonInstanceIsAddedTwice_ThenRegistrationIsIdempotent(bool useFactory)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var service = new FirstContractAuthority();
        context.AddService(service);

        // Act
        if (useFactory)
        {
            Assert.False(context.TryAddService<object>(() => service, _ => false));
        }
        else
        {
            context.AddService<object>(service);
        }

        // Assert
        Assert.Same(service, Assert.Single(context.GetServices<IFirstContract>()));
        Assert.Same(service, Assert.Single(context.GetServices<object>()));
    }

    [Fact]
    public void WhenAFactoryRegistersItsOwnSingletonReentrantly_ThenTheOuterRegistrationIsIdempotent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var service = new DualContractAuthority();

        // Act
        var added = context.TryAddService<object>(() =>
        {
            context.AddService(service);
            return service;
        }, _ => false);

        // Assert
        Assert.False(added);
        Assert.Same(service, Assert.Single(context.GetServices<IFirstContract>()));
        Assert.Same(service, Assert.Single(context.GetServices<ISecondContract>()));
        Assert.Throws<InvalidOperationException>(() => context.AddService(new CompetingFirstContractAuthority()));
    }

    [Fact]
    public void WhenServiceImplementsTwoSingletonContracts_ThenBothAreReserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new DualContractAuthority());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.AddService(new CompetingFirstContractAuthority()));
        Assert.Throws<InvalidOperationException>(() => context.AddService(new SecondContractAuthority()));
    }

    [Fact]
    public void WhenTryAddPredicateMatches_ThenFactoryIsNotCalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new FirstContractAuthority());

        var factoryWasCalled = false;

        // Act
        var added = context.TryAddService<IFirstContract>(
            () =>
            {
                factoryWasCalled = true;
                return new CompetingFirstContractAuthority();
            },
            _ => true);

        // Assert
        Assert.False(added);
        Assert.False(factoryWasCalled);
    }

    [Fact]
    public void WhenTryAddFactoryConflicts_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var existingService = new FirstContractAuthority();
        context.AddService(existingService);

        // Act & Assert: the predicate does not match, so the factory runs and its product must
        // fail the contract validation.
        Assert.Throws<InvalidOperationException>(
            () => context.TryAddService<IFirstContract>(() => new CompetingFirstContractAuthority(), _ => false));

        Assert.Same(existingService, Assert.Single(context.GetServices<IFirstContract>()));
    }

    [Fact]
    public void WhenTryAddFactoryReentrantlyAddsContract_ThenRevalidationThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var reentrantService = new FirstContractAuthority();

        // Act & Assert: the reentrant registration wins the contract, so the factory's own
        // product must be validated against the state that registration published, not against
        // the state read before the factory ran.
        Assert.Throws<InvalidOperationException>(
            () => context.TryAddService<IFirstContract>(
                () =>
                {
                    context.AddService(reentrantService);
                    return new CompetingFirstContractAuthority();
                },
                _ => false));

        Assert.Same(reentrantService, Assert.Single(context.GetServices<IFirstContract>()));
    }

    [Fact]
    public void WhenSubjectsAreOwned_ThenSingletonCanStillBeAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var car = new Car(context)
        {
            Speed = 1
        };

        // Act
        var service = new FirstContractAuthority();
        context.AddService(service);

        // Assert
        Assert.Equal(1, car.Speed);
        Assert.Same(service, Assert.Single(context.GetServices<IFirstContract>()));
    }

    private interface IFirstContract;

    private interface ISecondContract;

    private sealed class FirstContractAuthority : ISingletonContextService<IFirstContract>, IFirstContract;

    private sealed class CompetingFirstContractAuthority : ISingletonContextService<IFirstContract>, IFirstContract;

    private sealed class SecondContractAuthority : ISingletonContextService<ISecondContract>, ISecondContract;

    private sealed class DualContractAuthority :
        ISingletonContextService<IFirstContract>,
        ISingletonContextService<ISecondContract>,
        IFirstContract,
        ISecondContract;
}
