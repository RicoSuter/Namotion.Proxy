using Namotion.Interceptor.Ads.Client;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsErrorClassifierTests
{
    [Theory]
    [InlineData(AdsErrorCode.DeviceSymbolNotFound)]
    [InlineData(AdsErrorCode.DeviceInvalidSize)]
    [InlineData(AdsErrorCode.DeviceInvalidData)]
    [InlineData(AdsErrorCode.DeviceServiceNotSupported)]
    [InlineData(AdsErrorCode.DeviceInvalidAccess)]
    [InlineData(AdsErrorCode.DeviceInvalidOffset)]
    public void IsTransientError_WithPermanentError_ReturnsFalse(AdsErrorCode errorCode)
    {
        // Arrange & Act
        var result = AdsErrorClassifier.IsTransientError(errorCode);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(AdsErrorCode.TargetPortNotFound)]
    [InlineData(AdsErrorCode.TargetMachineNotFound)]
    [InlineData(AdsErrorCode.ClientPortNotOpen)]
    [InlineData(AdsErrorCode.DeviceError)]
    [InlineData(AdsErrorCode.DeviceTimeOut)]
    [InlineData(AdsErrorCode.DeviceBusy)]
    public void IsTransientError_WithTransientError_ReturnsTrue(AdsErrorCode errorCode)
    {
        // Arrange & Act
        var result = AdsErrorClassifier.IsTransientError(errorCode);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(AdsErrorCode.DeviceInvalidAccess)]
    [InlineData(AdsErrorCode.DeviceServiceNotSupported)]
    [InlineData(AdsErrorCode.DeviceTimeOut)]
    public void GetErrorCode_FromAdsErrorException_ReturnsTheAdsCode(AdsErrorCode errorCode)
    {
        // Arrange
        var exception = new AdsErrorException("Simulated.", errorCode);

        // Act
        var result = AdsErrorClassifier.GetErrorCode(exception);

        // Assert
        Assert.Equal(errorCode, result);
    }

    [Fact]
    public void GetErrorCode_DoesNotUseHResult()
    {
        // Arrange - HResult is the generic managed 0x80131500 for every ADS error, so a classifier
        // reading it would see one value for all of them and match no entry in the permanent set
        var exception = new AdsErrorException("Simulated.", AdsErrorCode.DeviceInvalidAccess);

        // Act & Assert
        Assert.NotEqual(AdsErrorCode.DeviceInvalidAccess, (AdsErrorCode)exception.HResult);
        Assert.Equal(AdsErrorCode.DeviceInvalidAccess, AdsErrorClassifier.GetErrorCode(exception));
        Assert.False(AdsErrorClassifier.IsTransientError(AdsErrorClassifier.GetErrorCode(exception)));
    }

    [Fact]
    public void GetErrorCode_FromExceptionWithoutAnAdsCode_ReturnsNoError()
    {
        // Arrange & Act - falls through to the transient default rather than guessing permanent
        var result = AdsErrorClassifier.GetErrorCode(new InvalidOperationException("No ADS code."));

        // Assert
        Assert.Equal(AdsErrorCode.NoError, result);
        Assert.True(AdsErrorClassifier.IsTransientError(result));
    }

    [Fact]
    public void IsTransientError_WithUnknownErrorCode_ReturnsTrue()
    {
        // Arrange
        var unknownCode = (AdsErrorCode)99999;

        // Act
        var result = AdsErrorClassifier.IsTransientError(unknownCode);

        // Assert - unknown codes are treated as transient (safer)
        Assert.True(result);
    }

    [Fact]
    public void IsTransientError_WithNoError_FallsThroughToDefaultTrue()
    {
        // Arrange
        var errorCode = AdsErrorCode.NoError;

        // Act
        var result = AdsErrorClassifier.IsTransientError(errorCode);

        // Assert - NoError falls through to default case which returns true (not classified as permanent)
        Assert.True(result);
    }
}
