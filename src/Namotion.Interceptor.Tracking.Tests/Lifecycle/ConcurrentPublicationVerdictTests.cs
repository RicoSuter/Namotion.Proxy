using System.Collections;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ConcurrentPublicationVerdictTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Concurrency")]
    public async Task WhenAnUnrelatedTopologyOperationIsInFlight_ThenAProjectionStillPublishesItsValue(bool attach)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new ScalarTriggeredOrphanSubject();
        subject.AttachToContext(context);
        var projected = new Person();
        subject.Orphan = projected;
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var unrelated = new EnumerableChildrenHolder();
        if (!attach) unrelated.AttachToContext(context);
        var values = new ParkingEnumerable(() =>
        {
            parked.Set();
            if (!release.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                throw new TimeoutException("Topology operation was not released");
        });
        var operation = Task.Run(() =>
        {
            unrelated.Children = values;
            if (attach) unrelated.AttachToContext(context);
        });

        try
        {
            Assert.True(parked.Wait(WriteProtocolAcceptance.RendezvousTimeout));

            // Act
            subject.Name = "trigger";

            // Assert
            var data = new PropertyReference(subject, nameof(ScalarTriggeredOrphanSubject.Current)).GetDerivedPropertyData();
            Assert.Same(projected, data.LastKnownValue);
            Assert.Null(projected.TryGetContext());
            Assert.Equal(0, projected.GetReferenceCount());
        }
        finally
        {
            release.Set();
            await operation.WaitAsync(WriteProtocolAcceptance.RendezvousTimeout);
        }

        Assert.Same(projected, subject.Current);
        subject.DetachFromContext(context);
        unrelated.DetachFromContext(context);
        Assert.Null(projected.TryGetContext());
    }

    private sealed class ParkingEnumerable(Action onFirstEnumeration) : IEnumerable<Person>
    {
        private int _enumerations;

        public IEnumerator<Person> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerations) == 1) onFirstEnumeration();
            return Enumerable.Empty<Person>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
