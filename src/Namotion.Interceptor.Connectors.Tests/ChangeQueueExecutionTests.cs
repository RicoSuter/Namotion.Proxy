namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueExecutionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(100);

    // Strictly above TestTimeout, so a test that hangs fails on its own timeout instead of
    // ambiguously racing the bound it meant to keep out of reach.
    private static readonly TimeSpan UnreachableBound = TimeSpan.FromMinutes(5);

    // A stop that never arrives; the name is the precondition the tests using it depend on.
    private static readonly Task NoStopRequested = new TaskCompletionSource().Task;

    [Fact]
    public async Task WhenTheCoreCompletesOnItsOwn_ThenNothingIsAbandoned()
    {
        // Arrange
        var execution = new ChangeQueueExecution(ShortBound);

        // Act
        var outcome = await execution.RunAsync(() => Task.CompletedTask, NoStopRequested).WaitAsync(TestTimeout);

        // Assert
        Assert.False(outcome.WasAbandoned);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenTheCoreThrows_ThenTheExceptionPropagatesToTheCaller()
    {
        // Arrange
        var execution = new ChangeQueueExecution(ShortBound);
        var fault = new InvalidOperationException("core failed");

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execution.RunAsync(() => throw fault, NoStopRequested)).WaitAsync(TestTimeout);
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task WhenTheCoreFaultsAfterReportingTheFault_ThenTheOriginalExceptionStillPropagates()
    {
        // Arrange
        var execution = new ChangeQueueExecution(UnreachableBound);
        var fault = new InvalidOperationException("core failed");

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => execution.RunAsync(() =>
            {
                execution.ReportFinalizationStarted(fault);
                throw fault;
            }, NoStopRequested)).WaitAsync(TestTimeout);
        Assert.Same(fault, thrown);
    }

    [Fact]
    public async Task WhenStopIsRequestedAndTheCoreFinishesWithinTheBound_ThenNothingIsAbandoned()
    {
        // Arrange
        var execution = new ChangeQueueExecution(UnreachableBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionTask = execution.RunAsync(async () =>
        {
            await using var registration = execution.ProcessingToken
                .Register(() => processingCancelled.TrySetResult())
                .ConfigureAwait(false);
            await processingCancelled.Task.ConfigureAwait(false);
        }, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        var outcome = await executionTask.WaitAsync(TestTimeout);

        // Assert
        Assert.False(outcome.WasAbandoned);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenStopIsRequestedAndTheCoreHangs_ThenTheCallerIsReleasedWithAbandonment()
    {
        // Arrange
        var execution = new ChangeQueueExecution(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var core = new HungCore();
        var executionTask = execution.RunAsync(() => core.Task, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        var outcome = await executionTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.WasAbandoned);
        Assert.Null(outcome.Fault);
        Assert.True(execution.TeardownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WhenTheCoreFaultsIntoFinalizationAndHangs_ThenTheBoundArmsWithoutAnyStop()
    {
        // Arrange
        var execution = new ChangeQueueExecution(ShortBound);
        using var core = new HungCore();
        var fault = new InvalidOperationException("finalization fault");
        var executionTask = execution.RunAsync(() =>
        {
            execution.ReportFinalizationStarted(fault);
            return core.Task;
        }, NoStopRequested);

        // Act
        var outcome = await executionTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.WasAbandoned);
        Assert.NotNull(outcome.Fault);
        Assert.Same(fault, outcome.Fault!.SourceException);
    }

    [Fact]
    public async Task WhenFinalizationWasAlreadyMarkedClean_ThenALaterFaultReportIsIgnored()
    {
        // Arrange
        var execution = new ChangeQueueExecution(ShortBound);
        using var core = new HungCore();
        var executionTask = execution.RunAsync(() =>
        {
            execution.ReportFinalizationStarted();
            execution.ReportFinalizationStarted(new InvalidOperationException("reported second"));
            return core.Task;
        }, NoStopRequested);

        // Act
        var outcome = await executionTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.WasAbandoned);
        Assert.Null(outcome.Fault);
    }

    [Fact]
    public async Task WhenStopWasObservedBeforeTheFaultReport_ThenAbandonmentCarriesNoFault()
    {
        // Arrange
        var execution = new ChangeQueueExecution(ShortBound);
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = execution.ProcessingToken
            .Register(() => processingCancelled.TrySetResult())
            .ConfigureAwait(false);
        using var core = new HungCore();
        var executionTask = execution.RunAsync(() => core.Task, stopSignal.Task);

        // Act
        stopSignal.TrySetResult();
        await processingCancelled.Task.WaitAsync(TestTimeout);
        execution.ReportFinalizationStarted(new InvalidOperationException("reported after the stop"));
        var outcome = await executionTask.WaitAsync(TestTimeout);

        // Assert
        Assert.True(outcome.WasAbandoned);
        Assert.Null(outcome.Fault);
    }

    // An abandoned core the test releases on scope exit, even when an assert fails first.
    private sealed class HungCore : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _completion.Task;

        public void Dispose() => _completion.TrySetResult();
    }
}
