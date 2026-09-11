using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class BlockedGateHolderWindowTests
{
    [Fact]
    public void WhenTheSameThreadStartsAnotherTransactionBetweenSamples_ThenTheBlockedWindowRestarts()
    {
        // Arrange
        var window = new BlockedGateHolderWindow();
        var holder = Thread.CurrentThread;
        Assert.False(window.Observe(holder, 1, true, 100, 1_000));
        Assert.False(window.Observe(holder, 1, true, 800, 1_000));

        // Act & Assert
        Assert.False(window.Observe(holder, 2, true, 1_200, 1_000));
        Assert.False(window.Observe(holder, 2, true, 2_199, 1_000));
        Assert.True(window.Observe(holder, 2, true, 2_200, 1_000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAHolderRunsOrLeavesBetweenSamples_ThenTheBlockedWindowRestarts(bool leaves)
    {
        // Arrange
        var window = new BlockedGateHolderWindow();
        var holder = Thread.CurrentThread;
        Assert.False(window.Observe(holder, 1, true, 100, 1_000));

        // Act & Assert
        Assert.False(window.Observe(leaves ? null : holder, 1, false, 800, 1_000));
        Assert.False(window.Observe(holder, 1, true, 1_200, 1_000));
        Assert.True(window.Observe(holder, 1, true, 2_200, 1_000));
    }
}
