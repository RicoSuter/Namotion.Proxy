using System;
using Namotion.Interceptor.WebSocket.Server;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class WebSocketServerConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_ShouldHaveExpectedValues()
    {
        // Act
        var configuration = new WebSocketServerConfiguration();

        // Assert
        Assert.Equal(8080, configuration.Port);
        Assert.Equal("/ws", configuration.Path);
        Assert.Null(configuration.BindAddress);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
    }

    [Fact]
    public void DefaultConfiguration_ShouldIncludeAllDefaults()
    {
        // Act
        var configuration = new WebSocketServerConfiguration();

        // Assert
        Assert.Equal(1000, configuration.MaxConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), configuration.HelloTimeout);
        Assert.Equal(10 * 1024 * 1024, configuration.MaxMessageSize);
        Assert.Equal(1000, configuration.WriteBatchSize);
        Assert.Null(configuration.BindAddress);
        Assert.Null(configuration.PathProvider);
        Assert.Null(configuration.SubjectFactory);
        Assert.Empty(configuration.Processors);
    }

    [Fact]
    public void Validate_WithInvalidPort_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { Port = 0 };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithPortAboveRange_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { Port = 70000 };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithEmptyPath_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { Path = "" };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithWhitespacePath_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { Path = "   " };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithNegativeBufferTime_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration
        {
            BufferTime = TimeSpan.FromMilliseconds(-1)
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroMaxMessageSize_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { MaxMessageSize = 0 };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroMaxConnections_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { MaxConnections = 0 };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithZeroHelloTimeout_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration
        {
            HelloTimeout = TimeSpan.Zero
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_WithNegativeWriteBatchSize_ShouldThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { WriteBatchSize = -1 };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void HeartbeatInterval_ShouldDefaultTo30Seconds()
    {
        // Act
        var configuration = new WebSocketServerConfiguration();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.HeartbeatInterval);
    }

    [Fact]
    public void Validate_ShouldThrowForNegativeHeartbeatInterval()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { HeartbeatInterval = TimeSpan.FromSeconds(-1) };

        // Act & Assert
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void Validate_ShouldAcceptZeroHeartbeatInterval()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration { HeartbeatInterval = TimeSpan.Zero };

        // Act & Assert
        configuration.Validate(); // should not throw
    }

    [Fact]
    public void WhenHeartbeatIntervalIsPositiveButBelowTheMinimum_ThenValidateThrows()
    {
        // Arrange - a heartbeat is broadcast to every connected client, so a sub-second interval
        // floods the connections rather than probing them.
        var configuration = new WebSocketServerConfiguration { HeartbeatInterval = TimeSpan.FromMilliseconds(500) };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(configuration.Validate);
        Assert.Equal(nameof(WebSocketServerConfiguration.HeartbeatInterval), exception.ParamName);
    }

    [Fact]
    public void WhenHeartbeatIntervalIsExactlyTheMinimum_ThenValidateAccepts()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration
        {
            HeartbeatInterval = WebSocketServerConfiguration.MinimumHeartbeatInterval
        };

        // Act & Assert
        configuration.Validate(); // should not throw
    }

    [Fact]
    public void Validate_WithValidConfiguration_ShouldNotThrow()
    {
        // Arrange
        var configuration = new WebSocketServerConfiguration
        {
            Port = 8080,
            Path = "/ws"
        };

        // Act & Assert
        configuration.Validate(); // Should not throw
    }
}
