using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Delivery is decided by commit order: a change is dropped only when a later non-source commit will
/// carry the settled value in its place. These pin cases where a value-based rule went wrong and the
/// commit-order rule must not: a stored value that is not the value that was written, a derived value
/// the getter recomputes, and a newer value that arrives from a different source. The value cases no
/// longer exercise the decision itself, since it never inspects a value, but they still pin that such
/// properties are delivered rather than filtered out on some other ground.
/// </summary>
public class ChangeDeliveryFilterTests
{
    [Fact]
    public async Task WhenAHookClampsTheWrittenValue_ThenTheStoredValueIsStillDelivered()
    {
        // Arrange: OnValueChanging rewrites 150 to 100, so the value written and the value stored differ.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new ClampingDevice(context);
        var written = new ConcurrentQueue<int>();

        using var processor = CreateProcessor(context, change => written.Enqueue(change.GetNewValue<int>()));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act
        subject.Value = 150;

        // Assert: the clamped value reaches the source rather than being dropped as not-current.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains(100));
        Assert.Equal(100, subject.Value);

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public async Task WhenADerivedValueIsRecomputed_ThenTheSettledValueIsDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var subject = new Person(context);
        var written = new ConcurrentQueue<string?>();

        using var processor = CreateProcessor(context, change =>
        {
            if (change.Property.Name == nameof(Person.FullName))
            {
                written.Enqueue(change.GetNewValue<string?>());
            }
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act: two writes, so an earlier derived recomputation is superseded by a later one.
        subject.FirstName = "A";
        subject.LastName = "B";

        // Assert: whatever intermediate derived values are dropped, the settled one is delivered.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains(subject.FullName));

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public async Task WhenADerivedGetterReturnsAFreshInstance_ThenItsChangeIsStillDeliveredWhenBuffered()
    {
        // Arrange: the getter recomputes, so what it hands back is never reference-equal to the value
        // the change carries. Staleness is unprovable here, and dropping would be permanent: the
        // transition that would re-enqueue the value is the change being dropped. Buffered on purpose,
        // because that is the path the flush-time check runs on, and it is every connector's default.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var subject = new DerivedCollectionDevice(context);
        var written = new ConcurrentQueue<string>();

        using var processor = CreateProcessor(context, change =>
        {
            if (change.Property.Name == nameof(DerivedCollectionDevice.Pair))
            {
                written.Enqueue(string.Join(",", change.GetNewValue<int[]>()));
            }
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act
        subject.First = 1;
        subject.Second = 2;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("1,2"));

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public async Task WhenANewerValueArrivesFromAnotherSource_ThenThisSourceStillReceivesIt()
    {
        // Arrange: two sources on one property. A pending write for source A is superseded by a value
        // that source B applied, and A is owed that value because A did not send it.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var sourceA = new object();
        var sourceB = new object();
        var written = new ConcurrentQueue<string?>();

        var firstName = new PropertyReference(subject, nameof(Person.FirstName));

        using var processor = new ChangeQueueProcessor(
            source: sourceA,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    written.Enqueue(change.GetNewValue<string?>());
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        // Both commit before processing starts, so the loop meets the local write with the model
        // already holding source B's value.
        subject.FirstName = "LocalPending";

        using (PendingOrigin.Set(firstName, ChangeOrigin.FromSource(sourceB), "FromB"))
        {
            subject.FirstName = "FromB";
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(cancellation.Token);

        // Assert: source A receives B's value, and never the value the model moved past.
        await AsyncTestHelpers.WaitUntilAsync(() => written.Contains("FromB"));
        Assert.DoesNotContain("LocalPending", written);

        await StopAsync(cancellation, processing);
    }

    [Fact]
    public void WhenARuntimeRegisteredPropertyHasCommittedANewerWrite_ThenTheEarlierChangeIsSuperseded()
    {
        // Arrange: a property registered at runtime, which is the shape the OPC UA client loader
        // creates for every node it browses. Its getter is caller supplied and need not read what the
        // write stored, so comparing values could never establish staleness here. Comparing the
        // property's own commit revision can.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var subject = new Person(context);

        object? stored = null;
        var property = subject
            .TryGetRegisteredSubject()!
            .AddProperty("Dynamic", typeof(int), _ => stored, (_, value) => stored = value);

        property.SetValue(1);
        Assert.True(property.Reference.TryGetWriteState(includeSourceCommitsInRevision: false, out var earlierRevision, out _));

        property.SetValue(2);
        Assert.True(property.Reference.TryGetWriteState(includeSourceCommitsInRevision: false, out var settledRevision, out _));
        Assert.True(settledRevision > earlierRevision);

        var earlier = CreateChange(property.Reference, 0, 1, earlierRevision);
        var settled = CreateChange(property.Reference, 1, 2, settledRevision);

        // Act & Assert
        Assert.False(ChangeDeliveryFilter.IsCurrent(in earlier, ChangeDeliveryRule.SourceValuesMayBeStale));
        Assert.True(ChangeDeliveryFilter.IsCurrent(in settled, ChangeDeliveryRule.SourceValuesMayBeStale));
    }

    [Fact]
    public void WhenAGeneratedPropertyHasCommittedANewerWrite_ThenTheEarlierChangeIsSuperseded()
    {
        // Arrange
        var subject = new DerivedCollectionDevice(InterceptorSubjectContext.Create()) { First = 1 };
        var property = new PropertyReference(subject, nameof(DerivedCollectionDevice.First));

        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var earlierRevision, out _));

        subject.First = 2;
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var settledRevision, out _));

        var earlier = CreateChange(property, 0, 1, earlierRevision);
        var settled = CreateChange(property, 1, 2, settledRevision);

        // Act & Assert
        Assert.False(ChangeDeliveryFilter.IsCurrent(in earlier, ChangeDeliveryRule.SourceValuesMayBeStale));
        Assert.True(ChangeDeliveryFilter.IsCurrent(in settled, ChangeDeliveryRule.SourceValuesMayBeStale));
    }

    [Fact]
    public void WhenAChangeCarriesNoRevision_ThenItIsDelivered()
    {
        // Arrange: constructed outside a write terminal, so it orders against nothing. A derived
        // recomputation is the common case, and dropping one would be permanent.
        var subject = new DerivedCollectionDevice(InterceptorSubjectContext.Create()) { First = 1 };
        var property = new PropertyReference(subject, nameof(DerivedCollectionDevice.First));

        subject.First = 2;

        var change = CreateChange(property, 0, 1, revision: 0);

        // Act & Assert
        Assert.True(ChangeDeliveryFilter.IsCurrent(in change, ChangeDeliveryRule.SourceValuesMayBeStale));
    }

    [Fact]
    public void WhenThePropertyHasNeverBeenWritten_ThenItsChangeIsDelivered()
    {
        // Arrange: nothing has committed, so nothing can have superseded the change.
        var subject = new DerivedCollectionDevice(InterceptorSubjectContext.Create());
        var property = new PropertyReference(subject, nameof(DerivedCollectionDevice.First));

        Assert.False(property.TryGetWriteState(includeSourceCommitsInRevision: false, out _, out _));

        var change = CreateChange(property, 0, 1, revision: 7);

        // Act & Assert
        Assert.True(ChangeDeliveryFilter.IsCurrent(in change, ChangeDeliveryRule.SourceValuesMayBeStale));
    }

    private static SubjectPropertyChange CreateChange(
        PropertyReference property, int oldValue, int newValue, long revision)
    {
        return SubjectPropertyChange.Create(
            property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, oldValue, newValue, revision);
    }

    /// <summary>
    /// The two rules differ on exactly one input: a commit applied from a source. Pinned here on one
    /// property so that neither can be changed without the other's case failing.
    /// </summary>
    [Fact]
    public void WhenASourceCommitFollowsALocalOne_ThenOnlyTheServerRuleTreatsItAsSuperseding()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        var source = new object();

        subject.FirstName = "local";
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var localRevision, out _));

        using (PendingOrigin.Set(property, ChangeOrigin.FromSource(source), "from source"))
        {
            subject.FirstName = "from source";
        }

        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var nonSourceMarker, out _));
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: true, out var anyMarker, out _));
        Assert.Equal(localRevision, nonSourceMarker);
        Assert.True(anyMarker > nonSourceMarker, "the applied commit must advance only the any-commit marker");

        var local = CreateChange(property, 0, 1, localRevision);

        // Act & Assert: identical change, opposite outcomes.
        Assert.True(ChangeDeliveryFilter.IsCurrent(in local, ChangeDeliveryRule.SourceValuesMayBeStale));
        Assert.False(ChangeDeliveryFilter.IsCurrent(in local, ChangeDeliveryRule.SourceValuesAreSettled));
    }

    /// <summary>
    /// The construction guard does not protect this entry point, which is the one a connector uses to
    /// repeat the decision under its own write lock. Mapping the zero value to a rule here would hand a
    /// third-party server client semantics with no diagnostic, which is the silent path the guard exists
    /// to close.
    /// </summary>
    [Fact]
    public void WhenTheDeliveryRuleIsUnspecified_ThenTheDeliveryDecisionIsRejected()
    {
        // Arrange
        var subject = new DerivedCollectionDevice(InterceptorSubjectContext.Create()) { First = 1 };
        var property = new PropertyReference(subject, nameof(DerivedCollectionDevice.First));
        var change = CreateChange(property, 0, 1, revision: 7);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ChangeDelivery.IsSuperseded(in change, default));
    }

    /// <summary>
    /// The public seam negates <see cref="ChangeDeliveryFilter.IsCurrent"/>, and every other test of the
    /// decision goes at the internal predicate, so dropping that negation changes nothing any unit test
    /// asserts. It is what the OPC UA server write loop asks while holding the node manager lock:
    /// inverted, it drops exactly what it has to write and writes exactly what it has to drop.
    /// </summary>
    [Fact]
    public void WhenAskedAgainAtTheWriteLock_ThenOnlyTheChangeALaterCommitReplacedIsSuperseded()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithPropertyChangeSubscriptions();

        var subject = new Person(context);
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        subject.FirstName = "first";
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var replacedRevision, out _));

        subject.FirstName = "second";
        Assert.True(property.TryGetWriteState(includeSourceCommitsInRevision: false, out var settledRevision, out _));
        Assert.True(settledRevision > replacedRevision, "the second write must commit a later revision");

        var replaced = CreateChange(property, 0, 1, replacedRevision);
        var settled = CreateChange(property, 1, 2, settledRevision);

        // Act & Assert: both directions, so neither the negation nor the comparison can be dropped.
        Assert.True(ChangeDelivery.IsSuperseded(in replaced, ChangeDeliveryRule.SourceValuesMayBeStale));
        Assert.False(ChangeDelivery.IsSuperseded(in settled, ChangeDeliveryRule.SourceValuesMayBeStale));
    }

    private static ChangeQueueProcessor CreateProcessor(
        IInterceptorSubjectContext context, Action<SubjectPropertyChange> onWritten)
    {
        // A distinct source object: with a null source the dequeue loop's echo check matches every
        // Local change, whose origin source is also null, and nothing is ever delivered.
        return new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    onWritten(change);
                }

                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);
    }

    private static async Task StopAsync(CancellationTokenSource cancellation, Task processing)
    {
        await cancellation.CancelAsync();
        try { await processing; } catch (OperationCanceledException) { /* expected */ }
    }
}
