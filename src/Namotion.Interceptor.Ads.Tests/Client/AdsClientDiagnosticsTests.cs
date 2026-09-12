using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsClientDiagnosticsTests
{

    [Fact]
    public void Properties_InitialState_ShouldHaveDefaultValues()
    {
        // Arrange
        var context = TestHelpers.CreateContextWithLifecycle();
        var subject = new TestPlcModel(context);
        var configuration = TestHelpers.CreateConfiguration();
        var logger = new Mock<ILogger>().Object;

        var source = new AdsSubjectClientSource(subject, configuration, logger);

        // Act
        var diagnostics = source.Diagnostics;

        // Assert
        Assert.Null(diagnostics.State);
        Assert.False(diagnostics.IsConnected);
        Assert.Equal(0, diagnostics.NotificationVariableCount);
        Assert.Equal(0, diagnostics.PolledVariableCount);
        Assert.Equal(0, diagnostics.TotalReconnectionAttempts);
        Assert.Equal(0, diagnostics.SuccessfulReconnections);
        Assert.Equal(0, diagnostics.FailedReconnections);
        Assert.Null(diagnostics.LastConnectedAt);
        Assert.False(diagnostics.IsCircuitBreakerOpen);
        Assert.Equal(0, diagnostics.CircuitBreakerTripCount);
    }

    [Fact]
    public void Polling_BeforeAnyPass_ShouldReportZero()
    {
        // Arrange
        var context = TestHelpers.CreateContextWithLifecycle();
        var subject = new TestPlcModel(context);
        var source = new AdsSubjectClientSource(
            subject, TestHelpers.CreateConfiguration(), new Mock<ILogger>().Object);

        // Act
        var polling = source.Diagnostics.Polling;

        // Assert
        Assert.Equal(0, polling.TotalPasses);
        Assert.Equal(0, polling.TotalFailedReads);
        Assert.Equal(0, polling.LastPassSymbolCount);
        Assert.Equal(0, polling.LastPassDurationMilliseconds);
    }
}
