namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// Per-member fluent configuration for ADS. <see cref="WithSymbolPath"/> sets the relative symbol-path
/// segment; the other setters configure the ADS notification/polling settings.
/// </summary>
public sealed class AdsFluentPropertyBuilder
{
    private string? _segment;
    private AdsReadMode? _readMode;
    private int? _cycleTime;
    private int? _maxDelay;
    private int? _priority;

    /// <summary>Sets the relative ADS symbol-path segment, composed with parent segments into the full symbol path.</summary>
    public AdsFluentPropertyBuilder WithSymbolPath(string symbolPath) { _segment = symbolPath; return this; }

    /// <summary>Sets how the variable is read from the PLC (notification, polled, or auto-demotion).</summary>
    public AdsFluentPropertyBuilder WithReadMode(AdsReadMode readMode) { _readMode = readMode; return this; }

    /// <summary>Sets the notification cycle time in milliseconds.</summary>
    public AdsFluentPropertyBuilder WithCycleTime(int cycleTime) { _cycleTime = cycleTime; return this; }

    /// <summary>Sets the maximum delay for notification batching in milliseconds.</summary>
    public AdsFluentPropertyBuilder WithMaxDelay(int maxDelay) { _maxDelay = maxDelay; return this; }

    /// <summary>Sets the demotion priority (higher values are demoted to polling first when the notification limit is reached).</summary>
    public AdsFluentPropertyBuilder WithPriority(int priority) { _priority = priority; return this; }

    internal (string? Segment, AdsPropertyMapping Metadata) Build()
        => (_segment, new AdsPropertyMapping(
            Segment: null,
            ReadMode: _readMode,
            CycleTime: _cycleTime,
            MaxDelay: _maxDelay,
            Priority: _priority));
}
