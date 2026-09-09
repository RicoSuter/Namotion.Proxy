using Namotion.Interceptor.Interceptors;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Reconciliation;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Reconciliation;

public class SourceReconciliationTests
{
    [Fact]
    public async Task WhenDelayedNotificationArrives_ThenCommittedValueIsNotReverted()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        // Act
        fixture.Writer.WriteValue(fixture.Property, "Old");
        var read = await fixture.Reader.Next();
        Assert.Equal("New", fixture.Person.FirstName);
        read.Complete("New");
        await fixture.WaitSettled();
        fixture.Writer.WriteValue(fixture.Property, "Old");
        (await fixture.Reader.Next()).Complete("New");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("New", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenWriteHasNoEcho_ThenVerificationAppliesSourceCorrection()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        // Act
        await fixture.WriteLocal("Sent");
        (await fixture.Reader.Next()).Complete("Adj");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Adj", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenLocalWriteOvertakesRead_ThenOldReadIsRejectedAndNewWriteSettles()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Writer.WriteValue(fixture.Property, "Old");
        var first = await fixture.Reader.Next();
        // Act
        await fixture.WriteLocal("Next");
        first.Complete("Old");
        var second = await fixture.Reader.Next();
        Assert.Equal("Next", fixture.Person.FirstName);
        second.Complete("Next");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Next", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenNotificationOvertakesRead_ThenAnotherReadResolvesIt()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Writer.WriteValue(fixture.Property, "Old");
        var first = await fixture.Reader.Next();
        // Act
        fixture.Writer.WriteValue(fixture.Property, "Adj");
        first.Complete("Old");
        (await fixture.Reader.Next()).Complete("Adj");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Adj", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenPollingRequestsRefreshDuringRead_ThenReadCanStillApply()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Writer.RequestReconciliation(fixture.Property);
        var first = await fixture.Reader.Next();
        // Act
        fixture.Writer.RequestReconciliation(fixture.Property);
        first.Complete("Adj");
        var second = await fixture.Reader.Next();
        Assert.Equal("Adj", fixture.Person.FirstName);
        second.Complete("Adj");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Adj", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenReadFails_ThenItRetriesWithoutAnotherWrite()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        // Act
        fixture.Writer.WriteValue(fixture.Property, "Adj");
        (await fixture.Reader.Next()).Completion.SetException(new IOException("Disconnected"));
        (await fixture.Reader.Next()).Complete("Adj");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Adj", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenReconnectOvertakesRead_ThenOldSessionCannotApply()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Writer.WriteValue(fixture.Property, "Old");
        var first = await fixture.Reader.Next();
        // Act
        fixture.Writer.StartBuffering();
        await fixture.Writer.LoadInitialStateAndResumeAsync(CancellationToken.None);
        first.Complete("Old");
        (await fixture.Reader.Next()).Complete("New");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("New", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenTransactionCommits_ThenVerificationRunsAfterLocalConfirmation()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Context.WithTransactions();
        fixture.Context.AddService<ITransactionWriter>(new SourceTransactionWriter());
        // Act
        using (var transaction = await fixture.Context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            fixture.Person.FirstName = "Sent";
            await transaction.CommitAsync(CancellationToken.None);
        }
        Assert.Equal("Sent", fixture.Person.FirstName);
        (await fixture.Reader.Next()).Complete("Adj");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Adj", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenOwnershipIsReleasedAndReclaimed_ThenPreviousReadCannotApply()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Writer.WriteValue(fixture.Property, "Old");
        var first = await fixture.Reader.Next();
        // Act
        fixture.Property.RemoveSource(fixture.Source);
        fixture.Property.SetSource(fixture.Source);
        fixture.Writer.RequestReconciliation(fixture.Property);
        first.Complete("Old");
        var second = await fixture.Reader.Next();
        Assert.Equal("New", fixture.Person.FirstName);
        second.Complete("New");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("New", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenSetterOvertakesReadInsideInterceptor_ThenTerminalGuardRejectsRead()
    {
        // Arrange
        using var blocker = new BlockingSourceInterceptor();
        using var fixture = await Fixture.Create(blocker);
        fixture.Writer.WriteValue(fixture.Property, "Adj");
        // Act
        (await fixture.Reader.Next()).Complete("Adj");
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await fixture.WriteLocal("Next"); }
        finally { blocker.Release.Set(); }
        (await fixture.Reader.Next()).Complete("Next");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Next", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenAlreadyVerifiedLocalObservationArrivesLate_ThenItDoesNotScheduleAnotherRead()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        var change = await fixture.WriteLocal("Sent");
        (await fixture.Reader.Next()).Complete("Sent");
        await fixture.WaitSettled();
        // Act
        fixture.Writer.Reconciler!.ProcessChange(change);
        // Assert
        Assert.False(fixture.Writer.HasPendingReconciliation);
        Assert.Equal("Sent", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenSourceOperationHasNoLocalCommit_ThenItStillInvalidatesAnEarlierRead()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Writer.WriteValue(fixture.Property, "Old");
        var first = await fixture.Reader.Next();
        var change = SubjectPropertyChange.Create(fixture.Property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "New", "Adj", 0);
        // Act
        await fixture.Source.WriteChangesInBatchesAsync(new[] { change }, CancellationToken.None);
        first.Complete("Old");
        var second = await fixture.Reader.Next();
        Assert.Equal("New", fixture.Person.FirstName);
        second.Complete("Adj");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("Adj", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenTransactionSourceWriteFails_ThenReconciliationIsNotLeftBlocked()
    {
        // Arrange
        using var fixture = await Fixture.Create(failWrites: true);
        fixture.Context.WithTransactions();
        fixture.Context.AddService<ITransactionWriter>(new SourceTransactionWriter());
        // Act
        using (var transaction = await fixture.Context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            fixture.Person.FirstName = "Sent";
            await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        }
        (await fixture.Reader.Next()).Complete("New");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("New", fixture.Person.FirstName);
    }

    [Fact]
    public async Task WhenCallbackArrivesAfterRelease_ThenItCannotResurrectOldOwnership()
    {
        // Arrange
        using var fixture = await Fixture.Create();
        fixture.Property.RemoveSource(fixture.Source);
        // Act
        fixture.Writer.WriteValue(fixture.Property, "Old");
        fixture.Property.SetSource(fixture.Source);
        fixture.Writer.RequestReconciliation(fixture.Property);
        (await fixture.Reader.Next()).Complete("New");
        await fixture.WaitSettled();
        // Assert
        Assert.Equal("New", fixture.Person.FirstName);
    }

    private sealed class BlockingSourceInterceptor : IWriteInterceptor, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void WriteProperty<T>(ref PropertyWriteContext<T> context, WriteInterceptionDelegate<T> next)
        {
            if (context.Origin.Kind == ChangeOriginKind.FromSource)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Blocked source apply was not released.");
            }
            next(ref context);
        }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        public required IInterceptorSubjectContext Context { get; init; }
        public required Person Person { get; init; }
        public required TestSubjectSource Source { get; init; }
        public required ControlledReader Reader { get; init; }
        public required PropertyReference Property { get; init; }
        public SubjectPropertyWriter Writer => Source.PropertyWriter;
        public static async Task<Fixture> Create(IWriteInterceptor? interceptor = null, bool failWrites = false)
        {
            var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
            if (interceptor is not null) context.WithService<IWriteInterceptor>(() => interceptor, _ => false);
            var person = new Person(context) { FirstName = "New" };
            var source = new TestSubjectSource(person, context, NullLogger.Instance)
            {
                WriteChangesOverride = failWrites ? (changes, _) => ValueTask.FromResult(WriteResult.Failure(changes, new IOException("Unknown write outcome"))) : null
            };
            var reader = new ControlledReader();
            source.PropertyWriter.EnableReconciliation(reader);
            var property = new PropertyReference(person, nameof(Person.FirstName));
            property.SetSource(source);
            await source.PropertyWriter.LoadInitialStateAndResumeAsync(CancellationToken.None);
            return new Fixture { Context = context, Person = person, Source = source, Reader = reader, Property = property };
        }
        public async Task<SubjectPropertyChange> WriteLocal(string value)
        {
            using var changes = Context.CreatePropertyChangeQueueSubscription();
            Person.FirstName = value;
            Assert.True(changes.TryDequeueImmediate(out var change));
            await Source.WriteChangesInBatchesAsync(new[] { change }, CancellationToken.None);
            return change;
        }
        public Task WaitSettled() => AsyncTestHelpers.WaitUntilAsync(() => !Writer.HasPendingReconciliation);
        public void Dispose() => Source.Dispose();
    }

    private sealed class ControlledReader : ISourcePropertyReader
    {
        private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
        public async ValueTask<IReadOnlyList<SourcePropertyValue>> ReadPropertiesAsync(ReadOnlyMemory<PropertyReference> properties, CancellationToken cancellationToken)
        {
            var request = new Request(properties.ToArray());
            await _requests.Writer.WriteAsync(request, cancellationToken);
            return await request.Completion.Task.WaitAsync(cancellationToken);
        }
        public Task<Request> Next() => _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Request(PropertyReference[] properties)
    {
        public TaskCompletionSource<IReadOnlyList<SourcePropertyValue>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(string value) => Completion.SetResult(properties.Select(property => new SourcePropertyValue(property, value)).ToArray());
    }
}
