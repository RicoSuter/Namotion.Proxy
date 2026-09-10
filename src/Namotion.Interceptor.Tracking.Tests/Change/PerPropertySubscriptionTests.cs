using Namotion.Interceptor.Testing;
using System.Linq.Expressions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)] // serialized; see Task 4
public class PerPropertySubscriptionTests
{
    public PerPropertySubscriptionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenPropertyChanges_ThenListenerIsInvokedByRef()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        string? captured = null;
        using var subscription = property.SubscribeInline((in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenOtherPropertyChanges_ThenListenerIsNotInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var invoked = false;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange _) => invoked = true);

        // Act
        person.LastName = "Doe";

        // Assert
        Assert.False(invoked);
    }

    [Fact]
    public void WhenSubscriptionDisposed_ThenListenerStopsAndCountReturnsToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var count = 0;
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange _) => count++);

        // Act
        person.FirstName = "John";
        subscription.Dispose();
        person.FirstName = "Jane";

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenUnknownMember_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            new PropertyReference(person, "DoesNotExist").SubscribeInline((in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenAnySubscribeArgumentIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert: rejected before install, so no silent never-firing subscription and no
        // permanently opened idle gate. The typed callback overload wraps before delegating, so
        // it needs its own guard and its own assertion here.
        Assert.Throws<ArgumentNullException>(() => property.SubscribeInline((IPropertyChangeObserver)null!));
        Assert.Throws<ArgumentNullException>(() => property.SubscribeInline((PropertyChangeCallback)null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPropertyInline(x => x.FirstName, (IPropertyChangeObserver)null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPropertyInline(x => x.FirstName, (PropertyChangeCallback)null!));
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToPropertyInline(x => x.FirstName, (in SubjectPropertyChange _) => { }));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToPropertyInline((Expression<Func<Person, string?>>)null!, (in SubjectPropertyChange _) => { }));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenReservedDataKeyHoldsForeignValue_ThenSubscribeThrowsInsteadOfSpinning()
    {
        // Arrange: occupy the reserved listeners key with a foreign value.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetPropertyData(PropertyChangeSubscription.ListenersKey, "foreign");

        // Act & Assert: fails loud instead of livelocking, and the count increment is rolled back.
        Assert.Throws<InvalidOperationException>(() =>
            property.SubscribeInline((in SubjectPropertyChange _) => { }));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public async Task WhenSubscriptionInstalledWhileWriteInFlight_ThenWriteDeliversStartingAndFinalValues()
    {
        // Arrange: the blocker sits AFTER PropertyChangeInterceptor in the chain, so the writer
        // passes the idle pre-commit gate (count zero, no local old-value snapshot), then parks
        // before the commit. Pins post-commit listener resolution and late delivery.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        var received = new List<(string? OldValue, string? NewValue)>();

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act: install while the write is in flight (post-gate, pre-commit), then release the commit.
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange change) =>
                received.Add((change.GetOldValue<string?>(), change.GetNewValue<string?>())));
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the racing write was delivered (commit happened after the install), including the
        // starting value retained in the write context.
        var change = Assert.Single(received);
        Assert.Equal("John", change.NewValue);
        Assert.Null(change.OldValue);
    }

    [Fact]
    public void WhenWriteCommitsBeforeInstall_ThenSubscriberSeesCommittedValueByReading()
    {
        // Arrange: the write completes fully before the subscription exists.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        person.FirstName = "John";
        var hits = 0;

        // Act: a write that committed before the install is not delivered; the documented recovery
        // is reading the property after Subscribe returns.
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange _) => hits++);

        // Assert
        Assert.Equal(0, hits);
        Assert.Equal("John", person.FirstName);
    }

}
