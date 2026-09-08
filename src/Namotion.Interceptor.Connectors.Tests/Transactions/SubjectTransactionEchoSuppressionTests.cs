using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Pins the echo-suppression contract: source-bound commit applies carry the accepting source's
/// identity so that source's outbound queue drops them as echoes, while other processors still
/// receive every notification.
/// </summary>
public class SubjectTransactionEchoSuppressionTests : TransactionTestBase
{
    private static List<SubjectPropertyChange> DrainUntilSentinelProperty(
        PropertyChangeQueueSubscription subscription, string sentinelPropertyName)
    {
        return DrainUntil(subscription, change => change.Property.Name == sentinelPropertyName);
    }

    [Fact]
    public async Task WhenLocalAndSourceBoundChangesCommittedTogether_ThenOnlySourceBoundCarriesSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        // FirstName is source-bound; LastName is a local (no-source) change.
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: FirstName_MaxLength is a distinct property, not involved in the commit.
        person.FirstName_MaxLength = 99;

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.FirstName_MaxLength));

        // Assert
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = changes.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();

        Assert.Single(firstNameChanges);
        Assert.Single(lastNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Origin.Source);
        Assert.Null(lastNameChanges[0].Origin.Source);
    }

    [Fact]
    public async Task WhenRollbackRevertsLocalApplies_ThenRevertNotificationsCarrySource()
    {
        // Arrange
        var context = CreateContext();
        var device = new ThrowingDevice(context)
        {
            // PropertyB throws during commit-apply; PropertyA succeeds and is then reverted.
            ShouldThrow = propertyName => propertyName == nameof(ThrowingDevice.PropertyB)
        };
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(ThrowingDevice.PropertyA)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(ThrowingDevice.PropertyB)).SetSource(sourceMock.Object);

        // Sentinel on a sibling subject: re-writing the reverted PropertyA value would be
        // suppressed by the equality check.
        var sentinel = new Person(context);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act. Dispose before writing the sentinel: writes inside the open transaction are
        // captured into the pending set, not published.
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            device.PropertyA = true;
            device.PropertyB = true;

            device.ThrowingEnabled = true;

            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert: both the forward apply and the rollback revert carry the source identity.
        var propertyAChanges = changes.Where(c => c.Property.Name == nameof(ThrowingDevice.PropertyA)).ToList();
        Assert.Equal(2, propertyAChanges.Count);
        Assert.All(propertyAChanges, change => Assert.Same(sourceMock.Object, change.Origin.Source));
        Assert.True(propertyAChanges[0].GetNewValue<bool>());   // forward apply: true
        Assert.False(propertyAChanges[1].GetNewValue<bool>());  // rollback revert: false
    }

    [Fact]
    public async Task WhenCommitSpansMultipleSources_ThenEachChangeCarriesItsOwnSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceA = CreateSucceedingSource();
        var sourceB = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceA.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceB.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: FirstName_MaxLength is local, not bound to any source.
        person.FirstName_MaxLength = 77;

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.FirstName_MaxLength));

        // Assert: each committed change carries its own source identity.
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = changes.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();

        Assert.Single(firstNameChanges);
        Assert.Single(lastNameChanges);
        Assert.Same(sourceA.Object, firstNameChanges[0].Origin.Source);
        Assert.Same(sourceB.Object, lastNameChanges[0].Origin.Source);
    }

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersCascade_ThenCascadePublishesLocalSource()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            device.Primary = 5;
            await transaction.CommitAsync(CancellationToken.None);
        }

        var secondaryAfterCommit = device.Secondary;

        // Sentinel on a sibling subject so the property is unambiguous.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert: the cascade produced Secondary = 10.
        Assert.Equal(10, secondaryAfterCommit);

        var primaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Primary)).ToList();
        var secondaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Secondary)).ToList();

        // Primary change carries the source.
        Assert.Single(primaryChanges);
        Assert.Equal(5, primaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, primaryChanges[0].Origin.Source);

        // Secondary change (the hook cascade) is a locally computed value: the one-shot pending
        // origin stamps only the Primary write it targets, so nested cascade writes publish with a
        // null (Local) source and are not echo-suppressed for the bound source.
        Assert.Single(secondaryChanges);
        Assert.Equal(10, secondaryChanges[0].GetNewValue<int>());
        Assert.Null(secondaryChanges[0].Origin.Source);

        // Only Primary was submitted to the source; the cascade was not in the pending set.
        // writtenBatches is populated: the synchronous mock completed inside CommitAsync.
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(nameof(CascadingDevice.Primary), writtenBatches[0][0].Property.Name);
    }

    [Fact]
    public async Task WhenInboundSourceValueTriggersCascade_ThenCascadePublishesLocalSource()
    {
        // Arrange
        var context = CreateContext();
        var device = new CascadingDevice(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(device, nameof(CascadingDevice.Primary)).SetSource(sourceMock.Object);
        new PropertyReference(device, nameof(CascadingDevice.Secondary)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        new PropertyReference(device, nameof(CascadingDevice.Primary))
            .SetValueFromSource(sourceMock.Object, DateTimeOffset.UtcNow, null, 7);

        // Sentinel: use a sibling subject so the sentinel property is unambiguous.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert: the cascade produced Secondary = 14.
        Assert.Equal(14, device.Secondary);

        var primaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Primary)).ToList();
        var secondaryChanges = changes.Where(c => c.Property.Name == nameof(CascadingDevice.Secondary)).ToList();

        // Primary change carries the inbound source.
        Assert.Single(primaryChanges);
        Assert.Equal(7, primaryChanges[0].GetNewValue<int>());
        Assert.Same(sourceMock.Object, primaryChanges[0].Origin.Source);

        // Secondary change (the hook cascade) is a locally computed value: the one-shot pending
        // origin stamps only the Primary write it targets, so nested cascade writes publish with a
        // null (Local) source and are not echo-suppressed for the bound source.
        Assert.Single(secondaryChanges);
        Assert.Equal(14, secondaryChanges[0].GetNewValue<int>());
        Assert.Null(secondaryChanges[0].Origin.Source);
    }

    [Fact]
    public async Task WhenCommitAppliedChangeTriggersDerivedRecalculation_ThenDerivedNotificationStaysLocallySourced()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writtenBatches = new List<SubjectPropertyChange[]>();
        var sourceMock = CreateSucceedingSource();
        sourceMock
            .Setup(s => s.WriteChangesAsync(
                It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(),
                It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
                writtenBatches.Add(batch.ToArray()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        // Only FirstName is source-bound; FullName is a derived property that recalculates
        // when FirstName changes and is never part of the transactional source write.
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Sentinel: use a sibling subject so the sentinel property is unambiguous and cannot be
        // confused with any notification produced by the commit or the derived recalculation.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.LastName));

        // Assert 1: the FullName recalculation notification carries a null source even though the
        // triggering apply ran under the confirming source scope. The recalculation is a nested
        // write that never consumes the one-shot pending origin, so a derived value is computed
        // locally and no source confirmed it. Consequence: derived recalculations are never
        // echo-suppressed, so a source-bound derived property still gets pushed.
        var fullNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FullName)).ToList();
        Assert.Single(fullNameChanges);
        Assert.Null(fullNameChanges[0].Origin.Source);

        // Assert 2 (sanity): FirstName change also carries the source.
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Origin.Source);

        // Assert 3: stage 1 (source write) contained exactly one batch with exactly one change
        // for FirstName; the derived FullName recalculation is never submitted to the source.
        Assert.Single(writtenBatches);
        Assert.Single(writtenBatches[0]);
        Assert.Equal(nameof(Person.FirstName), writtenBatches[0][0].Property.Name);
    }

    [Fact]
    public async Task WhenSourceBoundChangeCommitted_ThenServerSideProcessorStillReceivesIt()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        // The server-side processor uses a fresh, unique object as its source identity so the
        // echo filter (change.Origin.Source == _source) never matches the committed change's source.
        var serverSource = new object();
        var serverReceived = new List<SubjectPropertyChange>();

        var processor = new ChangeQueueProcessor(
            source: serverSource,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                lock (serverReceived)
                {
                    serverReceived.AddRange(changes.ToArray());
                }
                return ValueTask.CompletedTask;
            },
            bufferTime: TimeSpan.FromMilliseconds(8),
            maxQueueDepth: null,
            logger: NullLogger.Instance,
            deliveryRule: ChangeDeliveryRule.SourceValuesMayBeStale);

        using var processorCts = new CancellationTokenSource();
        var processTask = processor.ProcessAsync(processorCts.Token);

        try
        {
            // Act
            using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
            {
                person.FirstName = "John";
                await transaction.CommitAsync(CancellationToken.None);
            }

            // Wait until the server-side processor has received the FirstName change.
            await AsyncTestHelpers.WaitUntilAsync(
                () =>
                {
                    lock (serverReceived)
                    {
                        return serverReceived.Any(c => c.Property.Name == nameof(Person.FirstName));
                    }
                },
                message: "Server-side processor did not receive the committed FirstName change.");
        }
        finally
        {
            // Await the consumer before disposing: Dispose tears down the subscription's signal,
            // which a still-running TryDequeue may be about to wait on (ObjectDisposedException).
            await processorCts.CancelAsync();
            try { await processTask; } catch (OperationCanceledException) { }
            processor.Dispose();
        }

        // Assert: the server-side processor received the FirstName change with the confirming source
        // stamped on it. Its own echo filter does not suppress it because serverSource != sourceMock.Object.
        lock (serverReceived)
        {
            var change = serverReceived.First(c => c.Property.Name == nameof(Person.FirstName));
            Assert.Same(sourceMock.Object, change.Origin.Source);
        }
    }

    [Fact]
    public async Task WhenSingleSourceWritePartiallyFails_ThenWrittenChangeCarriesSourceAndFailedChangeIsNotApplied()
    {
        // Arrange: both properties bound to one source that accepts FirstName but rejects LastName,
        // pinning the partial-failure marking path of the single-source writer: accepted slots are
        // marked with the source, rejected slots stay unmarked and are excluded from the local apply.
        var context = CreateContext();
        var person = new Person(context);
        var sourceMock = CreatePartialFailureSource(change => change.Property.Name == nameof(Person.LastName));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        SubjectTransactionException exception;
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Sentinel: FirstName_MaxLength is a distinct property, not involved in the commit.
        person.FirstName_MaxLength = 77;

        var changes = DrainUntilSentinelProperty(subscription, nameof(Person.FirstName_MaxLength));

        // Assert: the accepted change was applied and its notification carries the accepting source.
        var firstNameChanges = changes.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Same(sourceMock.Object, firstNameChanges[0].Origin.Source);
        Assert.Equal("John", person.FirstName);

        // The rejected change was neither applied nor notified, and the commit reported partial success.
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(Person.LastName));
        Assert.Null(person.LastName);
        Assert.True(exception.IsPartialSuccess);
    }

    [Fact]
    public async Task WhenMultiSourceCommitPartiallyFails_ThenWrittenChangesCarryTheirOwnSources()
    {
        // Arrange: FirstName is bound to a fully succeeding source; LastName and FirstName_MaxLength are
        // bound to a second source that rejects FirstName_MaxLength, pinning the partial-failure marking
        // path of the parallel multi-source writer.
        var context = CreateContext();
        var person = new Person(context);
        var sourceA = CreateSucceedingSource();
        var sourceB = CreatePartialFailureSource(change => change.Property.Name == nameof(Person.FirstName_MaxLength));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceA.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceB.Object);
        new PropertyReference(person, nameof(Person.FirstName_MaxLength)).SetSource(sourceB.Object);

        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        SubjectTransactionException exception;
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            person.FirstName_MaxLength = 99;
            exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Sentinel: a sibling subject, matched by subject AND name because the committed LastName change
        // on 'person' shares the property name and must not terminate the drain early.
        var sentinel = new Person(context);
        sentinel.LastName = "Sentinel";

        var changes = DrainUntil(subscription, change =>
            ReferenceEquals(change.Property.Subject, sentinel) && change.Property.Name == nameof(Person.LastName));
        var personChanges = changes.Where(c => ReferenceEquals(c.Property.Subject, person)).ToList();

        // Assert: each accepted change carries its own accepting source.
        var firstNameChanges = personChanges.Where(c => c.Property.Name == nameof(Person.FirstName)).ToList();
        var lastNameChanges = personChanges.Where(c => c.Property.Name == nameof(Person.LastName)).ToList();
        Assert.Single(firstNameChanges);
        Assert.Single(lastNameChanges);
        Assert.Same(sourceA.Object, firstNameChanges[0].Origin.Source);
        Assert.Same(sourceB.Object, lastNameChanges[0].Origin.Source);
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);

        // The rejected change was neither applied nor notified, and the commit reported partial success.
        Assert.DoesNotContain(personChanges, c => c.Property.Name == nameof(Person.FirstName_MaxLength));
        Assert.Equal(123, person.FirstName_MaxLength);
        Assert.True(exception.IsPartialSuccess);
    }
}
