using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class SubscribeToPropertyResolverTests
{
    public SubscribeToPropertyResolverTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenDirectPropertySelector_ThenResolvesAndFires()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        string? captured = null;
        using var subscription = person.SubscribeToPropertyInline(p => p.FirstName, (in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenChainedSelector_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context) { Mother = new Person(context) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToPropertyInline(p => p.Mother!.FirstName, (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenMethodSelector_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToPropertyInline(p => p.ToString(), (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenDerivedPropertySelector_ThenSubscribesWithoutThrowing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act
        using var subscription = person.SubscribeToPropertyInline(p => p.FullName, (in SubjectPropertyChange _) => { });

        // Assert
        Assert.NotNull(subscription);
    }

    [Fact]
    public void WhenCapturedVariableSelector_ThenThrows()
    {
        // Arrange: the selector accesses a captured local rather than the lambda parameter, so the
        // member's instance expression is a closure field access, not propertySelector.Parameters[0].
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var other = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToPropertyInline(_ => other.FirstName, (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenStaticMemberSelector_ThenThrows()
    {
        // Arrange: a static property access has a null instance expression (Expression != parameter).
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToPropertyInline(_ => DateTime.Now, (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenFieldSelector_ThenThrows()
    {
        // Arrange: a field access resolves to a FieldInfo member (Member is not PropertyInfo).
        // DateTime.MinValue is a static readonly field, so the compiler keeps it as a field access.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToPropertyInline(_ => DateTime.MinValue, (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenPropertyIsNeitherInterceptedNorDerived_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var holder = new PlainPropertyHolder(context);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            holder.SubscribeToPropertyInline(p => p.PlainProperty, (in SubjectPropertyChange _) => { }));
    }
}
