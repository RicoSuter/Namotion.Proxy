using Namotion.Interceptor.Testing;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Serialized collection membership: these tests depend on the idle fast path, which requires the
// process-wide subscription count to be zero.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PropertyChangeLateChannelDeliveryTests
{
    public PropertyChangeLateChannelDeliveryTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public async Task WhenQueueSubscriptionCreatedWhileIdleWriteInFlight_ThenChangeIncludesStartingValue()
    {
        // Arrange: no consumers exist, so the writer passes the idle gate without taking a local
        // old-value snapshot, then parks before the commit.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act: create the first queue subscription while the write is in flight, then release.
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: the write committed after the subscription was created, so it is delivered with
        // the starting value retained in the write context.
        Assert.True(subscription.TryDequeue(out var change, new CancellationTokenSource(1000).Token));
        Assert.Equal("John", change.GetNewValue<string?>());
        Assert.Null(change.GetOldValue<string?>());
    }

    [Fact]
    public async Task WhenFirstObserverSubscribedWhileIdleWriteInFlight_ThenChangeIsDelivered()
    {
        // Arrange
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        SubjectPropertyChange? captured = null;

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act: the first observer joins while the write is in flight, then the commit is released.
        using var observer = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => captured = change);
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: delivered with the starting value retained in the write context.
        Assert.NotNull(captured);
        Assert.Equal("John", captured.Value.GetNewValue<string?>());
        Assert.Null(captured.Value.GetOldValue<string?>());
    }
}
