namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// The synchronization state of a source, and of a property with respect to its owning source.
/// One enum serves both so the property-level API returns a single coherent type.
/// </summary>
public enum SourceState
{
    /// <summary>No source has claimed the property. Only returned by the property-level API; a source is never Unclaimed.</summary>
    Unclaimed,

    /// <summary>Claimed and working toward sync: connecting, reconnecting, or connected with the initial load still in flight. Also the state after a detected connection loss, because the connect-and-load phase runs again.</summary>
    Synchronizing,

    /// <summary>The source completed its initial load procedure. What that guarantees differs per protocol; see the source monitoring documentation.</summary>
    Synchronized,

    /// <summary>The source shut down. Final, a stopped instance is never restarted.</summary>
    Stopped
}
