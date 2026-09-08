namespace Namotion.Interceptor.Connectors.Tests;

public class ConnectorRunAttemptTests
{
    [Fact]
    public async Task WhenForceKilled_ThenTheAttemptIsMarkedAndCancelled()
    {
        // Arrange
        using var attempt = new ConnectorRunAttempt(CancellationToken.None);
        var token = attempt.Token;

        // Act
        await attempt.ForceKillAsync();

        // Assert
        Assert.True(attempt.WasForceKilled);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task WhenCancelled_ThenTheAttemptIsNotMarkedAsForceKilled()
    {
        // Arrange
        using var attempt = new ConnectorRunAttempt(CancellationToken.None);
        var token = attempt.Token;

        // Act
        await attempt.CancelAsync();

        // Assert
        Assert.False(attempt.WasForceKilled);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task WhenForceKilledAfterDisposal_ThenTheAttemptIsLeftUnmarked()
    {
        // Arrange - the loop is between attempts, so this kill reached nothing.
        var attempt = new ConnectorRunAttempt(CancellationToken.None);
        attempt.Dispose();

        // Act
        await attempt.ForceKillAsync();

        // Assert
        Assert.False(attempt.WasForceKilled);
    }

    [Fact]
    public async Task WhenTheStoppingTokenIsCancelled_ThenTheAttemptIsCancelledWithoutBeingMarked()
    {
        // Arrange
        using var stopping = new CancellationTokenSource();
        using var attempt = new ConnectorRunAttempt(stopping.Token);

        // Act
        await stopping.CancelAsync();

        // Assert
        Assert.True(attempt.Token.IsCancellationRequested);
        Assert.False(attempt.WasForceKilled);
    }
}
