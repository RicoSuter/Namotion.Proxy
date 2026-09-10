using System.Linq.Expressions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathCurrentTests
{
    public PathCurrentTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenPathResolves_ThenCurrentHasLeafValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Act
        var current = subscription.Current;

        // Assert
        Assert.True(current.IsResolved);
        Assert.Equal("Joe", current.GetValueOrDefault());
    }

    [Fact]
    public void WhenResolvedLeafValueIsNull_ThenCurrentIsResolvedWithNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person() }; // FirstName left null
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Act
        var current = subscription.Current;

        // Assert
        Assert.True(current.IsResolved);
        Assert.Null(current.GetValueOrDefault());
    }

    [Fact]
    public void WhenIntermediateNull_ThenCurrentIsUnresolved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Father is null
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Act & Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenIndexOutOfRange_ThenCurrentIsUnresolvedNotThrow()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Children is empty
        using var subscription = person.SubscribeToPath(x => x.Children[3].FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Act & Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenDictionaryValueIsNull_ThenCurrentIsUnresolved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new Node(context);
        node.ByName = new Dictionary<string, Node> { ["key"] = null! };
        using var subscription = node.SubscribeToPath(x => x.ByName["key"].Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        // Act & Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenDisposed_ThenCurrentIsUnresolved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Act
        subscription.Dispose();

        // Assert
        Assert.False(subscription.Current.IsResolved);
    }

    [Fact]
    public void WhenSubscribeArgumentsNull_ThenThrowArgumentNullException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToPath(x => x.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPath((Expression<Func<Person, string?>>)null!, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPath(x => x.FirstName, (SubjectPathChangeCallback<string?>)null!, SubjectPathValidation.Full));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPath(x => x.FirstName, (ISubjectPathChangeObserver<string?>)null!, SubjectPathValidation.Full));
    }

    [Fact]
    public void WhenValidationModeIsNotDefined_ThenThrowArgumentOutOfRangeException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            person.SubscribeToPath(x => x.FirstName, (in SubjectPathChange<string?> _) => { }, (SubjectPathValidation)999));
    }

    [Fact]
    public async Task WhenSubscribeInActiveNonCommittingTransaction_ThenThrowInvalidOperationException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var person = new Person(context);

        // Act & Assert
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            Assert.Throws<InvalidOperationException>(
                () => person.SubscribeToPath(x => x.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full));
        }
    }
}
