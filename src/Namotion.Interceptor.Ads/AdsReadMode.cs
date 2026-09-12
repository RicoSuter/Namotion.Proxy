namespace Namotion.Interceptor.Ads;

/// <summary>
/// Specifies how a variable should be read from the PLC.
/// </summary>
public enum AdsReadMode
{
    /// <summary>
    /// Real-time ADS device notifications (push-based).
    /// </summary>
    Notification,

    /// <summary>
    /// Periodic reads via batch polling (pull-based).
    /// </summary>
    Polled,

    /// <summary>
    /// Starts as notification with automatic demotion to polled when the notification limit is reached.
    /// </summary>
    Auto
}
