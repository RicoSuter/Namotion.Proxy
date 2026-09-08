namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Whether a value applied from a source may supersede a change waiting to be delivered, which is the one
/// question a connector has to answer before it can decide what to drop.
/// </summary>
/// <remarks>
/// Both settings lose data when chosen wrongly, silently and permanently, so decide it by the condition
/// rather than by whether the connector is called a client or a server.
/// </remarks>
public enum ChangeDeliveryRule
{
    /// <summary>
    /// Not a rule. Occupies zero so that <c>default</c> and a literal <c>0</c>, both of which compile in
    /// a required parameter, cannot quietly select one. Rejected wherever a rule is consumed, both when a
    /// processor is constructed and at <see cref="ChangeDelivery.IsSuperseded"/>.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The source produced what it hands us before it saw our write, so an applied value cannot be ranked
    /// against our commits and a commit of ours that predates it is still the newer one.
    /// </summary>
    /// <remarks>
    /// Any connector talking to something over a wire. Its notifications reflect a state the far end had
    /// at some earlier moment, and our write may still be in flight toward it, so a local commit has to be
    /// delivered even though it looks older. Choosing
    /// <see cref="SourceValuesAreSettled"/> here drops that write and both ends settle on the stale value.
    /// </remarks>
    SourceValuesMayBeStale,

    /// <summary>
    /// An applied value has already reached the destination by the time we apply it, so it is the newer
    /// write and anything older must not be delivered over it.
    /// </summary>
    /// <remarks>
    /// A server, where the applied value is a client's own write. Check the condition rather than assuming
    /// it, because the three servers satisfy it differently: the OPC UA server because the SDK has written
    /// the node before the change reaches the subject, the MQTT and WebSocket servers because they apply
    /// inbound writes under a source that is not their own, so nothing is skipped as an echo and the
    /// superseding value is relayed onward. Changing either convention invalidates this for that server.
    /// What the condition really asks is that the destination already holds the value the subject will
    /// settle on, so where a server converts on the way in, as the OPC UA server does, it also needs that
    /// conversion to round-trip. A converter that clamps, scales or narrows leaves the node holding
    /// something the subject will not settle on. The shipped OPC UA converter is such a case: it maps a
    /// decimal property onto a double node and back, keeping 15 significant digits. This rule does not
    /// cause that divergence and dropping the older commit does not worsen it, since that commit carries
    /// an even staler value, but do not read the round-trip requirement as hypothetical.
    /// Choosing <see cref="SourceValuesMayBeStale"/> here delivers a commit the clients have already moved
    /// past, leaving them behind the model with nothing to correct them.
    /// </remarks>
    SourceValuesAreSettled
}
