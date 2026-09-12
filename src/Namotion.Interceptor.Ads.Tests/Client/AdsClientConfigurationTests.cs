using Namotion.Interceptor.Ads.Client;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsClientConfigurationTests
{
    private static AdsClientConfiguration CreateValidConfiguration()
    {
        return new AdsClientConfiguration
        {
            Host = "192.168.1.100",
            AmsNetId = AmsNetId.Parse("192.168.1.100.1.1")
        };
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var configuration = CreateValidConfiguration();

        // Assert
        Assert.Equal(851, configuration.AmsPort);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.Timeout);
        Assert.Equal(AdsReadMode.Auto, configuration.DefaultReadMode);
        Assert.Equal(100, configuration.DefaultCycleTime);
        Assert.Equal(0, configuration.DefaultMaxDelay);
        Assert.Equal(500, configuration.MaxNotifications);
        Assert.Equal(0, configuration.MaxConcurrentReads);
        Assert.True(configuration.PrefetchSymbols);
        Assert.Equal(TimeSpan.FromMilliseconds(100), configuration.PollingInterval);
        Assert.Equal(1000, configuration.WriteRetryQueueSize);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.HealthCheckInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
        Assert.Equal(TimeSpan.FromSeconds(10), configuration.RetryTime);
        Assert.Equal(5, configuration.CircuitBreakerFailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(60), configuration.CircuitBreakerCooldown);
        Assert.NotNull(configuration.ValueConverter);
        Assert.NotNull(configuration.Mapper);
    }

    [Fact]
    public void Validate_WithValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();

        // Act & Assert - should not throw
        configuration.Validate();
    }

    [Fact]
    public void Validate_WithNullHost_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = null;

        // Act & Assert - Host is optional; only AmsNetId is required for the connection
        configuration.Validate();
    }

    [Fact]
    public void Validate_WithNoHostAndNoAmsNetId_ThrowsArgumentException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = null;
        configuration.AmsNetId = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Contains("AmsNetId", exception.Message);
    }

    [Fact]
    public void Validate_WithHostnameHostAndNoAmsNetId_ThrowsArgumentException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = "plc01";
        configuration.AmsNetId = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Contains("AmsNetId", exception.Message);
    }

    [Fact]
    public void Validate_WithIpHostAndNoAmsNetId_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = "192.168.1.100";
        configuration.AmsNetId = null;

        // Act & Assert - the net id derives from the IP host
        configuration.Validate();
    }

    [Fact]
    public void GetTargetAmsNetId_WithIpHostAndNoAmsNetId_DerivesOneOne()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = "192.168.1.100";
        configuration.AmsNetId = null;

        // Act
        var amsNetId = configuration.GetTargetAmsNetId();

        // Assert
        Assert.Equal("192.168.1.100.1.1", amsNetId.ToString());
    }

    [Fact]
    public void GetTargetAmsNetId_WithExplicitAmsNetId_ReturnsExplicitValue()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = "192.168.1.100";
        configuration.AmsNetId = AmsNetId.Parse("5.23.100.200.1.1");

        // Act
        var amsNetId = configuration.GetTargetAmsNetId();

        // Assert
        Assert.Equal("5.23.100.200.1.1", amsNetId.ToString());
    }

    [Fact]
    public void GetTargetAmsNetId_WithHostnameHostAndNoAmsNetId_Throws()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.Host = "plc01";
        configuration.AmsNetId = null;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => configuration.GetTargetAmsNetId());
    }

    [Fact]
    public void UseEmbeddedRouter_IsTrueOnlyWhenHostIsSet()
    {
        // Arrange
        var configuration = CreateValidConfiguration();

        // Act & Assert
        configuration.Host = "192.168.1.100";
        Assert.True(configuration.UseEmbeddedRouter);

        configuration.Host = null;
        Assert.False(configuration.UseEmbeddedRouter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidAmsPort_ThrowsArgumentOutOfRangeException(int port)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.AmsPort = port;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithNegativeMaxConcurrentReads_ThrowsArgumentOutOfRangeException(int maxConcurrentReads)
    {
        // Arrange - 0 means no limit, but a negative value silently meant the same thing
        var configuration = CreateValidConfiguration();
        configuration.MaxConcurrentReads = maxConcurrentReads;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroMaxNotifications_Succeeds()
    {
        // Arrange — 0 leaves no notification budget for Auto properties, which is a valid choice;
        // a property that asks for Notification explicitly still gets one.
        var configuration = CreateValidConfiguration();
        configuration.MaxNotifications = 0;

        // Act & Assert
        configuration.Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithInvalidMaxNotifications_ThrowsArgumentOutOfRangeException(int maxNotifications)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.MaxNotifications = maxNotifications;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroPollingInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.PollingInterval = TimeSpan.Zero;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithNegativePollingInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.PollingInterval = TimeSpan.FromMilliseconds(-1);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithNegativeRescanDebounceTime_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.RescanDebounceTime = TimeSpan.FromMilliseconds(-1);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroRescanDebounceTime_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.RescanDebounceTime = TimeSpan.Zero;

        // Act & Assert - zero means no debounce, which is valid
        configuration.Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_WithNegativeDefaultMaxDelay_ThrowsArgumentOutOfRangeException(int defaultMaxDelay)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.DefaultMaxDelay = defaultMaxDelay;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroDefaultMaxDelay_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.DefaultMaxDelay = 0;

        // Act & Assert - zero means no batching delay, which is valid
        configuration.Validate();
    }

    [Fact]
    public void Validate_WithCustomValues_DoesNotThrow()
    {
        // Arrange
        var configuration = new AdsClientConfiguration
        {
            Host = "10.0.0.1",
            AmsNetId = AmsNetId.Parse("10.0.0.1.1.1"),
            AmsPort = 852,
            DefaultReadMode = AdsReadMode.Polled,
            MaxNotifications = 1000,
            PollingInterval = TimeSpan.FromMilliseconds(500)
        };

        // Act & Assert - should not throw
        configuration.Validate();
    }
}
