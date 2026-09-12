using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Classifies ADS errors as transient (retry) or permanent (don't retry).
/// Unknown error codes are treated as transient (safer default).
/// </summary>
internal static class AdsErrorClassifier
{
    private static readonly HashSet<AdsErrorCode> PermanentErrors =
    [
        AdsErrorCode.DeviceSymbolNotFound,
        AdsErrorCode.DeviceInvalidSize,
        AdsErrorCode.DeviceInvalidData,
        AdsErrorCode.DeviceServiceNotSupported,
        AdsErrorCode.DeviceInvalidAccess,
        AdsErrorCode.DeviceInvalidOffset,
    ];

    /// <summary>
    /// Determines if an ADS error is transient and should be retried.
    /// </summary>
    /// <param name="errorCode">The ADS error code to classify.</param>
    /// <returns>True if the error is transient and should be retried; false if permanent.</returns>
    public static bool IsTransientError(AdsErrorCode errorCode)
    {
        return !PermanentErrors.Contains(errorCode);
    }

    /// <summary>
    /// Reads the ADS error code off an exception.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Exception.HResult"/>: that carries the generic managed 0x80131500 for every
    /// ADS error, so casting it to <see cref="AdsErrorCode"/> yields -2146233088 and matches nothing.
    /// The code is only on <see cref="AdsErrorException"/>.
    /// </remarks>
    /// <param name="exception">The exception to read the code from.</param>
    /// <returns>The ADS error code, or <see cref="AdsErrorCode.NoError"/> when the exception carries
    /// none. That falls through to the transient default, which is the safer way to be wrong.</returns>
    public static AdsErrorCode GetErrorCode(Exception exception)
    {
        return exception is AdsErrorException adsErrorException
            ? adsErrorException.ErrorCode
            : AdsErrorCode.NoError;
    }

    /// <summary>
    /// Classifies an exception thrown by the ADS API.
    /// </summary>
    /// <remarks>
    /// Only <see cref="AdsErrorException"/> carries an error code, so classifying the others by code
    /// puts them all in the transient bucket. Several describe something no retry can change: a value
    /// whose PLC type will not resolve, will not marshal, or names a symbol the PLC will not expose
    /// fails the same way on every attempt. <c>ClientNotConnectedException</c> and the session
    /// exceptions stay transient, since a reconnect does change the answer.
    /// </remarks>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>True when retrying could plausibly succeed.</returns>
    public static bool IsTransientException(Exception exception)
    {
        return exception switch
        {
            AdsErrorException adsErrorException => IsTransientError(adsErrorException.ErrorCode),
            DataTypeException => false,
            MarshalException => false,
            SymbolException => false,
            _ => true,
        };
    }
}
