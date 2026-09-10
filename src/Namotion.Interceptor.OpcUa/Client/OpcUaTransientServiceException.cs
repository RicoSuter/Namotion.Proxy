using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Thrown when an OPC UA Browse, BrowseNext or Read service returns a transient bad status for a
/// node, or omits the result of a requested node, during a load. The owning subject source treats
/// it as a load failure and lets the session reconnect logic retry from scratch. Permanent bad
/// statuses do not raise this exception; they are logged and the node is skipped.
/// </summary>
/// <remarks>
/// Instances are observable through <c>OpcUaClientDiagnostics.LastError</c> and through the hosting
/// framework's error pipeline when the initial connect or a reconnect fails.
/// </remarks>
public sealed class OpcUaTransientServiceException : Exception
{
    /// <summary>The service that failed: <c>Browse</c>, <c>BrowseNext</c> or <c>Read</c>.</summary>
    public string Operation { get; }

    /// <summary>The node the operation targeted, or null when the failure cannot be attributed to one.</summary>
    public NodeId? NodeId { get; }

    /// <summary>The bad status the service returned, <c>BadUnexpectedError</c> for an omitted result.</summary>
    public StatusCode StatusCode { get; }

    public OpcUaTransientServiceException(string operation, NodeId? nodeId, StatusCode statusCode)
        : base($"OPC UA {operation} returned transient status {statusCode} for NodeId {nodeId}.")
    {
        Operation = operation;
        NodeId = nodeId;
        StatusCode = statusCode;
    }
}
