namespace Namotion.Interceptor.Connectors.Tests;

public class ConnectorCommitLeaseTests
{
    [Fact]
    public void WhenTheLeaseIsLive_ThenCommitsAreAdmitted()
    {
        // Arrange
        var lease = new ConnectorCommitLease();

        // Act
        var admitted = lease.TryAcquireCommit();

        // Assert
        Assert.True(admitted);
    }

    [Fact]
    public async Task WhenTheLeaseIsRetired_ThenNewCommitsAreRejected()
    {
        // Arrange
        var lease = new ConnectorCommitLease();

        // Act
        await lease.RetireAsync();

        // Assert
        Assert.False(lease.TryAcquireCommit());
    }

    [Fact]
    public void WhenNoCommitIsActive_ThenRetirementCompletesSynchronously()
    {
        // Arrange
        var lease = new ConnectorCommitLease();

        // Act
        var retirement = lease.RetireAsync();

        // Assert
        Assert.True(retirement.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenACommitIsActive_ThenRetirementWaitsUntilItIsReleased()
    {
        // Arrange
        var lease = new ConnectorCommitLease();
        Assert.True(lease.TryAcquireCommit());

        // Act
        var retirement = lease.RetireAsync();

        // Assert
        Assert.False(retirement.IsCompleted);

        // Act
        lease.ReleaseCommit();

        // Assert
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenSeveralCommitsAreActive_ThenRetirementWaitsForTheLastRelease()
    {
        // Arrange
        var lease = new ConnectorCommitLease();
        Assert.True(lease.TryAcquireCommit());
        Assert.True(lease.TryAcquireCommit());

        // Act
        var retirement = lease.RetireAsync();
        lease.ReleaseCommit();

        // Assert
        Assert.False(retirement.IsCompleted);

        // Act
        lease.ReleaseCommit();

        // Assert
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
