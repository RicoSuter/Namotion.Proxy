using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Coordinates one <see cref="ChangeQueueProcessor.ProcessAsync"/> execution, including cancellation and bounded finalization.
/// </summary>
/// <remarks>
/// Use each instance once. The core must report finalization before cleanup that may block.
/// A stop signal or finalization report starts the teardown timeout. Abandoned work is observed in
/// the background, and token sources are disposed after the core and cancellation callbacks settle.
/// </remarks>
internal sealed class ChangeQueueProcessorExecution
{
    private readonly CancellationTokenSource _processingTokenSource = new();
    private readonly CancellationTokenSource _teardownTokenSource = new();
    private readonly TaskCompletionSource<ExceptionDispatchInfo?> _finalizationStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _teardownTimeout;

    public ChangeQueueProcessorExecution(TimeSpan teardownTimeout)
    {
        _teardownTimeout = teardownTimeout;

        // Captured tokens remain readable after their sources are disposed.
        ProcessingToken = _processingTokenSource.Token;
        TeardownToken = _teardownTokenSource.Token;
    }

    /// <summary>Cancelled when a stop or finalization report is observed.</summary>
    public CancellationToken ProcessingToken { get; }

    /// <summary>Cancelled at the teardown timeout; used for the final flush and handoff.</summary>
    public CancellationToken TeardownToken { get; }

    /// <summary>Reports ordinary finalization. Only the first finalization report is retained.</summary>
    public void ReportFinalizationStarted() => _finalizationStarted.TrySetResult(null);

    /// <summary>
    /// Reports fault-driven finalization. Only the first finalization report is retained.
    /// </summary>
    public void ReportFinalizationStarted(Exception fault) =>
        _finalizationStarted.TrySetResult(ExceptionDispatchInfo.Capture(fault));

    /// <summary>
    /// Runs the core until completion or abandonment at the teardown timeout.
    /// </summary>
    /// <param name="core">Work that uses this execution's tokens and reports when finalization starts.</param>
    /// <param name="stopSignal">Completes when the caller requests cancellation.</param>
    /// <returns>
    /// Whether the core was abandoned, with the reported fault if finalization was observed before a stop.
    /// The caller settles abandoned delivery ownership before rethrowing that fault. If the core finishes
    /// within the timeout, its exception propagates directly.
    /// </returns>
    public async Task<(bool WasAbandoned, ExceptionDispatchInfo? Fault)> RunAsync(Func<Task> core, Task stopSignal)
    {
        var coreTask = Task.Run(core, CancellationToken.None);

        var completedTask = await Task.WhenAny(coreTask, _finalizationStarted.Task, stopSignal).ConfigureAwait(false);
        if (completedTask == coreTask)
        {
            try { await coreTask.ConfigureAwait(false); }
            finally
            {
                _processingTokenSource.Dispose();
                _teardownTokenSource.Dispose();
            }

            return (WasAbandoned: false, Fault: null);
        }

        var processingCancellationTask = _processingTokenSource.CancelAsync();
        var teardownCancellationTask = Task.CompletedTask;
        using var boundCancellation = new CancellationTokenSource();
        var boundExpiry = Task.Delay(_teardownTimeout, boundCancellation.Token);
        try
        {
            if (await Task.WhenAny(coreTask, boundExpiry).ConfigureAwait(false) == coreTask)
            {
                await boundCancellation.CancelAsync().ConfigureAwait(false);
                await coreTask.ConfigureAwait(false);
                return (WasAbandoned: false, Fault: null);
            }

            teardownCancellationTask = _teardownTokenSource.CancelAsync();

            // A fault reported after the stop was observed does not replace the cancellation outcome.
            return (WasAbandoned: true,
                Fault: completedTask == _finalizationStarted.Task
                    ? await _finalizationStarted.Task.ConfigureAwait(false)
                    : null);
        }
        finally
        {
            ObserveCompletionInBackground(coreTask, processingCancellationTask, teardownCancellationTask);
        }
    }

    // Awaiting these tasks here would extend the teardown timeout indefinitely.
    private void ObserveCompletionInBackground(Task coreTask, Task processingCancellationTask, Task teardownCancellationTask)
    {
        _ = Task.WhenAll(coreTask, processingCancellationTask, teardownCancellationTask).ContinueWith(
            task =>
            {
                _ = task.Exception;
                _processingTokenSource.Dispose();
                _teardownTokenSource.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
