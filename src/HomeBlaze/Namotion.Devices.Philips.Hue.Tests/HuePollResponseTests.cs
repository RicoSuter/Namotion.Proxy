using HueApi.Models;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HuePollResponseTests
{
    [Fact]
    public void WhenTheBridgeReturnsErrorsInsteadOfData_ThenTheResponseIsRejected()
    {
        // Arrange - the SDK does not throw for a non-success status that carries a JSON body. It
        // returns an empty Data list, which the poll would otherwise read as "the bridge has no
        // devices" and use to empty the model.
        var response = new HueResponse<Device>
        {
            Errors = [new HueError { Description = "Rate limit exceeded" }]
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => response.ThrowOnError("device"));
        Assert.Contains("device", exception.Message);
        Assert.Contains("Rate limit exceeded", exception.Message);
    }

    [Fact]
    public void WhenTheBridgeReturnsData_ThenTheResponseIsReturnedUnchanged()
    {
        // Arrange
        var response = new HueResponse<Device>
        {
            Data = [TestHelpers.CreateDevice("TEST001")]
        };

        // Act
        var result = response.ThrowOnError("device");

        // Assert
        Assert.Same(response, result);
    }

    [Fact]
    public void WhenTheBridgeReturnsNeitherDataNorErrors_ThenTheEmptyResponseIsAccepted()
    {
        // Arrange - a bridge with nothing of a given resource type is a legitimate answer, so an
        // empty list on its own must not be mistaken for a failure.
        var response = new HueResponse<Device>();

        // Act
        var result = response.ThrowOnError("device");

        // Assert
        Assert.Same(response, result);
        Assert.Empty(result.Data);
    }
}
