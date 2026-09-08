using Namotion.Interceptor.Testing;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

// Serialized collection membership: the late-consumer test depends on the idle fast path, which
// requires the process-wide subscription count to be zero.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PropertyChangeRevisionTests
{
    public PropertyChangeRevisionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSeveralWritesArePublished_ThenChangesCarryIncreasingRevisions()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        person.FirstName = "John";
        person.LastName = "Doe";
        person.FirstName = "Jane";

        // Assert
        Assert.Equal(3, changes.Count);
        Assert.All(changes, change => Assert.True(change.Revision > 0, "The published revision must be the commit revision, not the unstamped default."));

        for (var index = 1; index < changes.Count; index++)
        {
            Assert.True(
                changes[index].Revision > changes[index - 1].Revision,
                $"Revision {changes[index].Revision} must be strictly greater than the preceding {changes[index - 1].Revision}.");
        }
    }

    [Fact]
    public async Task WhenConsumerIsInstalledWhileIdleWriteInFlight_ThenLateDeliveredChangeCarriesTheRevision()
    {
        // Arrange: no consumers exist, so the writer passes the idle gate and lands in the late
        // dispatch path, then parks before the commit that stamps the revision.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var person = new Person(context);

        // The write parks in the interceptor chain, so it must not wait for a pool thread.
        var writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(
            () => { person.FirstName = "John"; });
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Guards that the write really took the idle branch, so this test covers the late dispatch
        // path rather than silently repeating the normal one.
        Assert.True(interceptor.IsIdle);

        // Act: the first queue subscription is created while the write is in flight, then released.
        using var subscription = context.CreatePropertyChangeQueueSubscription();
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.True(subscription.TryDequeue(out var change, new CancellationTokenSource(1000).Token));
        Assert.Equal("John", change.GetNewValue<string?>());
        Assert.True(change.Revision > 0, "The late dispatch path must publish the commit revision too.");
    }
}
