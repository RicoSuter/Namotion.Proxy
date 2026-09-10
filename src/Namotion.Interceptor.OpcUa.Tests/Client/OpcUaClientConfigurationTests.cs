using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for OpcUaClientConfiguration validation logic.
/// </summary>
public class OpcUaClientConfigurationTests
{
    private static OpcUaClientConfiguration CreateValidConfiguration(
        TimeSpan? maxReconnectDuration = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
        };

        if (maxReconnectDuration.HasValue)
        {
            config.MaxReconnectDuration = maxReconnectDuration.Value;
        }

        return config;
    }

    [Fact]
    public void WhenMaxReconnectDurationIsLessThanFiveSeconds_ThenValidateThrows()
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(4));

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("MaxReconnectDuration", exception.Message);
        Assert.Contains("at least 5 seconds", exception.Message);
    }

    [Fact]
    public void WhenMaxReconnectDurationIsExactlyFiveSeconds_ThenValidateSucceeds()
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(5));

        // Act & Assert - should not throw
        config.Validate();
    }

    [Fact]
    public void WhenMaxReconnectDurationIsDefault_ThenValidateSucceeds()
    {
        // Arrange
        var config = CreateValidConfiguration();

        // Act & Assert - should not throw
        config.Validate();
        Assert.Equal(TimeSpan.FromSeconds(30), config.MaxReconnectDuration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4.9)]
    public void WhenMaxReconnectDurationIsBelowMinimum_ThenValidateThrows(double seconds)
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(seconds));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void WhenMaxReconnectDurationIsAtOrAboveMinimum_ThenValidateSucceeds(double seconds)
    {
        // Arrange
        var config = CreateValidConfiguration(TimeSpan.FromSeconds(seconds));

        // Act & Assert - should not throw
        config.Validate();
    }

    [Fact]
    public void WhenReadAfterWriteBufferIsNegative_ThenValidateThrows()
    {
        // Arrange
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ReadAfterWriteBuffer = TimeSpan.FromMilliseconds(-1)
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.Validate());
        Assert.Contains("ReadAfterWriteBuffer", exception.Message);
    }

    [Fact]
    public void WhenReadAfterWriteBufferIsZero_ThenValidateSucceeds()
    {
        // Arrange - Zero is valid (no buffer)
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ReadAfterWriteBuffer = TimeSpan.Zero
        };

        // Act & Assert - Should not throw
        config.Validate();
    }

    [Fact]
    public void WhenReadAfterWriteSettingsAreValid_ThenValidateSucceeds()
    {
        // Arrange
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance),
            ReadAfterWriteBuffer = TimeSpan.FromMilliseconds(100)
        };

        // Act & Assert - Should not throw
        config.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WhenMaxBrowseContinuationRoundsIsNotPositive_ThenValidateThrows(int maxBrowseContinuationRounds)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.MaxBrowseContinuationRounds = maxBrowseContinuationRounds;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaClientConfiguration.MaxBrowseContinuationRounds), exception.ParamName);
    }

    [Fact]
    public void WhenMaxBrowseContinuationRoundsIsDefault_ThenValidateSucceeds()
    {
        // Arrange
        var configuration = CreateValidConfiguration();

        // Act & Assert - Should not throw
        configuration.Validate();
        Assert.Equal(100, configuration.MaxBrowseContinuationRounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WhenMaxAttributeTraversalDepthIsNotPositive_ThenValidateThrows(int maxAttributeTraversalDepth)
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        configuration.MaxAttributeTraversalDepth = maxAttributeTraversalDepth;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => configuration.Validate());
        Assert.Equal(nameof(OpcUaClientConfiguration.MaxAttributeTraversalDepth), exception.ParamName);
    }

    [Fact]
    public void WhenMaxAttributeTraversalDepthIsDefault_ThenValidateSucceeds()
    {
        // Arrange
        var configuration = CreateValidConfiguration();

        // Act & Assert - Should not throw
        configuration.Validate();
        Assert.Equal(100, configuration.MaxAttributeTraversalDepth);
    }
}
