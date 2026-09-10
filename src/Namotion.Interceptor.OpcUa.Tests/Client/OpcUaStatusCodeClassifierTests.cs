using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the OPC UA status code classifier. It answers two questions that the access-scoped
/// codes answer oppositely: <see cref="OpcUaStatusCodeClassifier.IsRecoverableWithinSession"/> (can this
/// recover without a new session?) backs the subscribe and write paths, and
/// <see cref="OpcUaStatusCodeClassifier.ThrowIfLoadMustRetry"/> (must the load abort and retry?) backs
/// the browse and read paths, where access-scoped codes are skipped rather than retried.
/// </summary>
public class OpcUaStatusCodeClassifierTests
{
    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadNodeIdInvalid)]
    [InlineData(StatusCodes.BadAttributeIdInvalid)]
    [InlineData(StatusCodes.BadIndexRangeInvalid)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    [InlineData(StatusCodes.BadNotWritable)]
    [InlineData(StatusCodes.BadWriteNotSupported)]
    public void WhenStatusIsSessionPermanent_ThenIsRecoverableWithinSessionReturnsFalse(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isRecoverable = OpcUaStatusCodeClassifier.IsRecoverableWithinSession(status);

        // Assert
        Assert.False(isRecoverable);
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadCommunicationError)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    [InlineData(StatusCodes.BadServerHalted)]
    [InlineData(StatusCodes.BadShutdown)]
    [InlineData(StatusCodes.BadResourceUnavailable)]
    [InlineData(StatusCodes.BadOutOfMemory)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTooManyOperations)]
    [InlineData(StatusCodes.BadSessionIdInvalid)]
    [InlineData(StatusCodes.BadSecureChannelClosed)]
    [InlineData(StatusCodes.BadDeviceFailure)]
    [InlineData(StatusCodes.BadSensorFailure)]
    [InlineData(StatusCodes.BadTooManyMonitoredItems)]
    public void WhenStatusIsTransientBadCode_ThenIsRecoverableWithinSessionReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isRecoverable = OpcUaStatusCodeClassifier.IsRecoverableWithinSession(status);

        // Assert
        Assert.True(isRecoverable);
    }

    [Theory]
    // Role permissions and the AccessLevel attribute are mutable server-side, so these can start
    // succeeding mid-session. Classifying them permanent would drop the monitored item and forfeit
    // both in-session recovery routes, leaving the property dark until the next reconnect.
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotReadable)]
    [InlineData(StatusCodes.BadNotImplemented)]
    public void WhenStatusIsAccessScoped_ThenIsRecoverableWithinSessionReturnsTrue(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act
        var isRecoverable = OpcUaStatusCodeClassifier.IsRecoverableWithinSession(status);

        // Assert
        Assert.True(isRecoverable);
    }

    [Fact]
    public void WhenStatusIsGood_ThenIsRecoverableWithinSessionReturnsFalse()
    {
        // Arrange
        var status = new StatusCode(StatusCodes.Good);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsRecoverableWithinSession(status));
    }

    [Fact]
    public void WhenStatusIsUncertain_ThenIsRecoverableWithinSessionReturnsFalse()
    {
        // Arrange: Uncertain carries a real value with reduced confidence; it is neither
        // a transient failure (no retry will improve it) nor a permanent design-time error.
        var status = new StatusCode(StatusCodes.Uncertain);

        // Act & Assert
        Assert.False(OpcUaStatusCodeClassifier.IsRecoverableWithinSession(status));
    }

    [Theory]
    [InlineData(StatusCodes.BadTimeout)]
    [InlineData(StatusCodes.BadOutOfService)]
    [InlineData(StatusCodes.BadTooManyMonitoredItems)]
    [InlineData(StatusCodes.BadServerNotConnected)]
    public void WhenBrowseStatusCanClearOnReload_ThenThrowIfLoadMustRetryThrows(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act & Assert
        var exception = Assert.Throws<OpcUaTransientServiceException>(
            () => OpcUaStatusCodeClassifier.ThrowIfLoadMustRetry(status, "Browse", new NodeId(1)));
        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(status, exception.StatusCode);
    }

    [Theory]
    // Permanent design-time codes: reloading repeats the failure, so the caller skips the node.
    [InlineData(StatusCodes.BadNodeIdUnknown)]
    [InlineData(StatusCodes.BadTypeMismatch)]
    [InlineData(StatusCodes.BadSecurityModeInsufficient)]
    // Access-scoped codes: transient over a longer horizon, but deterministic for this session, so
    // a browse/read reload would crash-loop. They must NOT throw here even though
    // IsRecoverableWithinSession classifies them recoverable for the subscribe path.
    [InlineData(StatusCodes.BadUserAccessDenied)]
    [InlineData(StatusCodes.BadNotReadable)]
    [InlineData(StatusCodes.BadNotImplemented)]
    // Non-bad statuses never throw.
    [InlineData(StatusCodes.Good)]
    [InlineData(StatusCodes.Uncertain)]
    public void WhenBrowseStatusWouldRepeatOnReload_ThenThrowIfLoadMustRetryDoesNotThrow(uint statusCode)
    {
        // Arrange
        var status = new StatusCode(statusCode);

        // Act & Assert (does not throw)
        OpcUaStatusCodeClassifier.ThrowIfLoadMustRetry(status, "Browse", new NodeId(1));
    }

    [Theory]
    [InlineData(StatusCodes.BadTooManyOperations)]
    [InlineData(StatusCodes.BadEncodingLimitsExceeded)]
    [InlineData(StatusCodes.BadRequestTooLarge)]
    [InlineData(StatusCodes.BadResponseTooLarge)]
    public void WhenServerRejectsBatchSize_ThenIsBatchTooLargeReturnsTrue(uint statusCode)
    {
        // Arrange
        var exception = new ServiceResultException(statusCode);

        // Act & Assert
        Assert.True(OpcUaStatusCodeClassifier.IsBatchTooLarge(exception));
    }
}
