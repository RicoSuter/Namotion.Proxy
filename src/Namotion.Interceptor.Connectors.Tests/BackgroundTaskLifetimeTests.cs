using Microsoft.Extensions.Logging.Abstractions;

namespace Namotion.Interceptor.Connectors.Tests;

public class BackgroundTaskLifetimeTests
{
    [Fact]
    public async Task WhenDisposed_ThenMonitorTaskIsCancelledAndCleanupRunsAfter()
    {
        // Arrange
        var order = new List<string>();
        var monitorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = BackgroundTaskLifetime.Start(
            CancellationToken.None,
            NullLogger.Instance,
            async token =>
            {
                monitorStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    order.Add("monitor-exited");
                }
            },
            disposeAsyncFunc: () =>
            {
                order.Add("cleanup");
                return ValueTask.CompletedTask;
            });

        await monitorStarted.Task;

        // Act
        await lifetime.DisposeAsync();

        // Assert
        Assert.Equal(["monitor-exited", "cleanup"], order);
    }

    [Fact]
    public async Task WhenDisposedTwice_ThenCleanupRunsOnlyOnce()
    {
        // Arrange
        var cleanupCount = 0;
        var lifetime = BackgroundTaskLifetime.Start(
            CancellationToken.None,
            NullLogger.Instance,
            async token => await Task.Delay(Timeout.Infinite, token),
            disposeAsyncFunc: () =>
            {
                Interlocked.Increment(ref cleanupCount);
                return ValueTask.CompletedTask;
            });

        // Act
        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync();

        // Assert
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public async Task WhenMonitorBodyThrows_ThenDisposeDoesNotThrow()
    {
        // Arrange
        var lifetime = BackgroundTaskLifetime.Start(
            CancellationToken.None,
            NullLogger.Instance,
            _ => throw new InvalidOperationException("monitor failed"));

        await Task.Yield();

        // Act & Assert
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task WhenCleanupCallbackThrows_ThenDisposeDoesNotThrow()
    {
        // Arrange
        var lifetime = BackgroundTaskLifetime.Start(
            CancellationToken.None,
            NullLogger.Instance,
            async token => await Task.Delay(Timeout.Infinite, token),
            disposeAsyncFunc: () => throw new InvalidOperationException("cleanup failed"));

        // Act & Assert
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task WhenParentTokenCancelled_ThenMonitorTaskCompletes()
    {
        // Arrange
        var parentCts = new CancellationTokenSource();
        var monitorExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = BackgroundTaskLifetime.Start(
            parentCts.Token,
            NullLogger.Instance,
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    monitorExited.SetResult();
                }
            });

        // Act
        await parentCts.CancelAsync();
        await monitorExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — monitor exited due to parent cancellation
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task WhenCleanupIsNull_ThenDisposeCompletes()
    {
        // Arrange
        var lifetime = BackgroundTaskLifetime.Start(
            CancellationToken.None,
            NullLogger.Instance,
            async token => await Task.Delay(Timeout.Infinite, token),
            disposeAsyncFunc: null);

        // Act & Assert
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task WhenMonitorBodyCompletesImmediately_ThenDisposeCompletes()
    {
        // Arrange
        var lifetime = BackgroundTaskLifetime.Start(
            CancellationToken.None,
            NullLogger.Instance,
            _ => Task.CompletedTask);

        await Task.Yield();

        // Act & Assert
        await lifetime.DisposeAsync();
    }
}
