using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketSubjectHandlerSequenceTests
{
    private static WebSocketSubjectHandler CreateHandler(TestRoot? root = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        root ??= new TestRoot(context);
        return new WebSocketSubjectHandler(
            root,
            new WebSocketServerConfiguration(),
            NullLogger.Instance);
    }

    [Fact]
    public void CurrentSequence_ShouldStartAtZero()
    {
        // Act
        var handler = CreateHandler();

        // Assert
        Assert.Equal(0L, handler.CurrentSequence);
    }

    [Fact]
    public async Task BroadcastChanges_WithNoConnections_ShouldNotIncrementSequence()
    {
        // Arrange
        var handler = CreateHandler();

        // Act - BroadcastChangesAsync short-circuits when no connections exist
        await handler.BroadcastChangesAsync(ReadOnlyMemory<Namotion.Interceptor.Tracking.Change.SubjectPropertyChange>.Empty, CancellationToken.None);

        // Assert
        Assert.Equal(0L, handler.CurrentSequence);
    }

    [Fact]
    public async Task WhenHeartbeatsAreDisabled_ThenTheLoopParksUntilCancelled()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new TestRoot(context);
        var handler = new WebSocketSubjectHandler(
            root,
            new WebSocketServerConfiguration { HeartbeatInterval = TimeSpan.Zero },
            NullLogger.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        var task = handler.RunHeartbeatLoopAsync(cts.Token);
        var completedEarly = task.IsCompleted;
        await cts.CancelAsync();
        await task;

        // Assert
        // Both callers race this task against the change processor and treat either one finishing
        // as a reason to restart, so completing here would spin the server rebuilding its host.
        Assert.False(completedEarly);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunHeartbeatLoopAsync_ShouldRespectCancellation()
    {
        // Arrange
        var handler = CreateHandler();
        using var cts = new CancellationTokenSource();
        var task = handler.RunHeartbeatLoopAsync(cts.Token);

        // Act - Cancel quickly
        await cts.CancelAsync();

        // Assert - Should complete without throwing
        await task;
    }
}
