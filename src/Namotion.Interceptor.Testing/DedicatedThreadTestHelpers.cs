namespace Namotion.Interceptor.Testing;

/// <summary>
/// Helpers for tests that park a thread on a gate or a lock to manufacture a race.
/// </summary>
public static class DedicatedThreadTestHelpers
{
    /// <summary>
    /// Runs <paramref name="body"/> on its own thread and surfaces it as a task.
    /// </summary>
    /// <remarks>
    /// A race participant that blocks must not be queued on the thread pool. Test assemblies run their
    /// collections in parallel, CI agents size the pool from a low core count, and the pool only injects
    /// extra threads gradually, so a queued work item can wait seconds before it is scheduled at all.
    /// A test that then bounds how long it waits for that body to reach its gate fails for a reason that
    /// has nothing to do with what it is testing. Worse, a body that was never scheduled has also never
    /// completed, so an assertion phrased as "this has not finished yet" passes without proving anything.
    /// </remarks>
    /// <param name="body">The work to run. Expected to block.</param>
    /// <param name="name">Optional thread name, to make a hung test readable in a dump.</param>
    public static Task<T> RunOnDedicatedThreadAsync<T>(Func<T> body, string? name = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(body());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = name ?? "dedicated test thread"
        };

        thread.Start();
        return completion.Task;
    }

    /// <inheritdoc cref="RunOnDedicatedThreadAsync{T}(Func{T}, string)"/>
    public static Task RunOnDedicatedThreadAsync(Action body, string? name = null) =>
        RunOnDedicatedThreadAsync<object?>(
            () =>
            {
                body();
                return null;
            },
            name);

    /// <summary>
    /// Starts <paramref name="body"/> on its own thread and surfaces the resulting task.
    /// </summary>
    /// <remarks>
    /// Only the part before the first suspension point is guaranteed to run on the dedicated thread,
    /// which is what matters here: the gate a race participant parks on is reached synchronously.
    /// See <see cref="RunOnDedicatedThreadAsync{T}(Func{T}, string)"/> for why the pool is unsuitable.
    /// </remarks>
    public static Task RunOnDedicatedThreadAsync(Func<Task> body, string? name = null) =>
        RunOnDedicatedThreadAsync<Task>(body, name).Unwrap();
}
