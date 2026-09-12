using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Ads.Attributes;

/// <summary>
/// Maps a property to an ADS symbol path on the PLC.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class AdsVariableAttribute : PathAttribute
{
    /// <summary>
    /// Creates a mapping to an ADS symbol path.
    /// </summary>
    /// <param name="symbolPath">The ADS symbol path (e.g., "GVL.Temperature").</param>
    /// <param name="connectorName">The connector name. Defaults to "ads".</param>
    public AdsVariableAttribute(string symbolPath, string? connectorName = null)
        : base(connectorName ?? AdsConstants.DefaultConnectorName, symbolPath)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
    }

    /// <summary>
    /// Gets the ADS symbol path (alias for <see cref="PathAttribute.Path"/>).
    /// </summary>
    public string SymbolPath => Path;

    /// <summary>
    /// Gets or sets the read mode for this variable.
    /// </summary>
    public AdsReadMode ReadMode { get; init; } = AdsReadMode.Auto;

    /// <summary>
    /// Gets or sets the notification cycle time in milliseconds.
    /// Uses the global default if set to <see cref="int.MinValue"/>.
    /// </summary>
    public int CycleTime { get; init; } = int.MinValue;

    /// <summary>
    /// Gets or sets the maximum delay for notification batching in milliseconds.
    /// Uses the global default if set to <see cref="int.MinValue"/>.
    /// </summary>
    public int MaxDelay { get; init; } = int.MinValue;

    /// <summary>
    /// Gets or sets the priority for notification demotion.
    /// Higher values are demoted first when the notification limit is reached.
    /// Default: 0 (normal priority).
    /// </summary>
    public int Priority { get; init; }
}
