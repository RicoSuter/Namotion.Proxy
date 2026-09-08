using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// A derived recalculation already computes and stabilizes the value it hands to the write chain,
/// so the publish must reuse that value instead of invoking the getter a second time. Re-invoking
/// runs user code on the publish path and can return a later value, pairing it with an old value it
/// never coexisted with.
/// </summary>
public class DerivedFinalValueTests
{
    [Fact]
    public void WhenDerivedRecalculationIsPublished_ThenPublishedValueIsTheStabilizedValue()
    {
        // Arrange: the getter returns a different value on every invocation, so a re-invocation at
        // publish time is directly visible in the published value and in the invocation count.
        var invocationCount = 0;
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var sensor = new ExternalSensor(context);
        var property = new PropertyReference(sensor, nameof(ExternalSensor.CalibratedTemperature));

        // The attach-time evaluation still uses the default provider, so the counting provider is
        // installed afterwards and the first counted invocation is the recalculation itself.
        sensor.ExternalValueProvider = () => Interlocked.Increment(ref invocationCount);

        // Act
        property.RecalculateDerivedProperty();

        // Assert
        Assert.Single(changes);
        Assert.Equal(0.0, changes[0].GetOldValue<double>());
        Assert.Equal(1.0, changes[0].GetNewValue<double>());
        Assert.Equal(1, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public void WhenDependencyWriteCascadesToDerivedProperty_ThenPublishingAddsNoGetterInvocation()
    {
        // Arrange & Act: the same scenario is run with and without a change consumer. Only the
        // consumer run reaches the publish code, so any extra getter invocation it makes shows up
        // as a difference against the consumer-less baseline. The counter is reset after a warm-up
        // write so the measured write starts from an already stabilized dependency set.
        var invocationsWithoutConsumer = MeasureCascadeGetterInvocations(withConsumer: false);
        var invocationsWithConsumer = MeasureCascadeGetterInvocations(withConsumer: true);

        // Assert
        Assert.Equal(1, invocationsWithoutConsumer);
        Assert.Equal(invocationsWithoutConsumer, invocationsWithConsumer);
    }

    private static int MeasureCascadeGetterInvocations(bool withConsumer)
    {
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        using var subscription = withConsumer
            ? context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add)
            : null;

        var sensor = new CountingDerivedSensor(context);

        // Warm-up write: the dependency set is recorded at attach, so only from the second write on
        // is the getter guaranteed to be evaluated exactly once (no stabilization re-evaluation).
        sensor.RawValue = 1;
        sensor.DerivedGetterInvocations = 0;

        sensor.RawValue = 2;

        if (withConsumer)
        {
            Assert.Contains(changes, change => change.Property.Name == nameof(CountingDerivedSensor.ScaledValue));
        }

        return sensor.DerivedGetterInvocations;
    }
}

/// <summary>
/// Counts derived getter invocations. <see cref="DerivedGetterInvocations"/> is a plain (non-partial)
/// property, so it is not intercepted and incrementing it inside the getter records no dependency.
/// </summary>
[InterceptorSubject]
public partial class CountingDerivedSensor
{
    public int DerivedGetterInvocations { get; set; }

    public partial int RawValue { get; set; }

    [Derived]
    public int ScaledValue
    {
        get
        {
            DerivedGetterInvocations++;
            return RawValue * 2;
        }
    }
}
