using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Installs a deterministic hosted execution task whose cancellation and exit are independently gated.
/// </summary>
public sealed class HostedExecutionGate : IAsyncDisposable
{
    private static readonly FieldInfo ExecuteTaskField = typeof(BackgroundService).GetField(
        "_executeTask",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("BackgroundService._executeTask was not found.");

    private static readonly FieldInfo StoppingCtsField = typeof(BackgroundService).GetField(
        "_stoppingCts",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("BackgroundService._stoppingCts was not found.");

    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _executionTask;

    private HostedExecutionGate(BackgroundService service)
    {
        _executionTask = RunAsync();
        StoppingCtsField.SetValue(service, _stoppingCts);
        ExecuteTaskField.SetValue(service, _executionTask);
    }

    /// <summary>Gets a task that completes when the installed execution has started.</summary>
    public Task Started => _started.Task;

    /// <summary>Gets a task that completes when hosted-service cancellation is observed.</summary>
    public Task CancellationObserved => _cancellationObserved.Task;

    /// <summary>Installs a gated execution in <paramref name="service"/>.</summary>
    public static HostedExecutionGate Install(BackgroundService service) => new(service);

    /// <summary>Allows the installed execution to exit after it has observed cancellation.</summary>
    public void AllowExit() => _allowExit.TrySetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stoppingCts.CancelAsync().ConfigureAwait(false);
        AllowExit();
        await _executionTask.ConfigureAwait(false);
        _stoppingCts.Dispose();
    }

    private async Task RunAsync()
    {
        _started.SetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _stoppingCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            _cancellationObserved.SetResult();
        }

        await _allowExit.Task.ConfigureAwait(false);
    }
}
