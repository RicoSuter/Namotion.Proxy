namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceGateTests
{
    [Fact]
    public void WhenEnsureStartedIsCalledTwice_ThenStateIsRunning()
    {
        // Arrange
        var gate = new HostedServiceGate();

        // Act
        gate.EnsureStarted();
        gate.EnsureStarted();

        // Assert
        Assert.Equal(HostedServiceGateState.Running, gate.State);
    }

    [Fact]
    public void WhenEnsureStartedIsCalledWhileDraining_ThenStateStaysDraining()
    {
        // Arrange
        var gate = new HostedServiceGate();
        gate.EnsureStarted();
        gate.BeginDraining();

        // Act
        gate.EnsureStarted();

        // Assert - a plain assignment here would reopen the shutdown race the fourth state closes
        Assert.Equal(HostedServiceGateState.Draining, gate.State);
    }

    [Fact]
    public async Task WhenGateIsNotStarted_ThenWaitDoesNotComplete()
    {
        // Arrange
        var gate = new HostedServiceGate();

        // Act
        var wait = gate.WaitForOpenAsync();

        // Assert
        Assert.False(wait.IsCompleted);
        gate.EnsureStarted();
        await wait;
    }

    [Fact]
    public async Task WhenDrainingStartsFromNotStarted_ThenParkedWaitersAreReleased()
    {
        // Arrange - a host that aborts startup never opens the gate; parked transitions must not hang
        var gate = new HostedServiceGate();
        var wait = gate.WaitForOpenAsync();
        Assert.False(wait.IsCompleted);

        // Act
        gate.BeginDraining();

        // Assert - awaited between the two calls, because CompleteDraining sets the same signal and
        // would release the waiter whatever BeginDraining did.
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostedServiceGateState.Draining, gate.State);

        gate.CompleteDraining();
        Assert.Equal(HostedServiceGateState.Drained, gate.State);
    }
}
