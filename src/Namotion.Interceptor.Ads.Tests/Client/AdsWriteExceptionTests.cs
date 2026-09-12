using Namotion.Interceptor.Ads.Client;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsWriteExceptionTests
{
    [Fact]
    public void Constructor_ShouldSetProperties()
    {
        // Arrange & Act
        var exception = new AdsWriteException(3, 2, 10);

        // Assert
        Assert.Equal(3, exception.TransientCount);
        Assert.Equal(2, exception.PermanentCount);
        Assert.Equal(10, exception.TotalCount);
    }

    [Fact]
    public void Constructor_ShouldFormatMessage()
    {
        // Arrange & Act
        var exception = new AdsWriteException(3, 2, 10);

        // Assert
        Assert.Contains("3 transient", exception.Message);
        Assert.Contains("2 permanent", exception.Message);
        Assert.Contains("10 total", exception.Message);
    }

    [Fact]
    public void Constructor_WithZeroCounts_ShouldWork()
    {
        // Arrange & Act
        var exception = new AdsWriteException(0, 0, 0);

        // Assert
        Assert.Equal(0, exception.TransientCount);
        Assert.Equal(0, exception.PermanentCount);
        Assert.Equal(0, exception.TotalCount);
    }
}
