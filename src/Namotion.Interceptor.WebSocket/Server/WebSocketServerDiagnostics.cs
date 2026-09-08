using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// What the WebSocket server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// A value of <c>true</c> means the listener is accepting connections and <c>false</c> means it is
/// not, either because the server reported that or because it has stopped. It reads <c>null</c> only
/// while the server runs before its first liveness report. Neither throughput direction is measured,
/// so both rates are <c>null</c> rather than 0.
/// </remarks>
public sealed class WebSocketServerDiagnostics : ConnectorDiagnostics
{
    private readonly WebSocketSubjectServer _server;

    internal WebSocketServerDiagnostics(WebSocketSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of currently connected WebSocket clients.
    /// </summary>
    public int ConnectionCount => _server.ConnectionCount;

    /// <summary>
    /// Gets the sequence number most recently assigned to an outgoing message, a monotonic position
    /// in the message stream rather than a count of events.
    /// </summary>
    public long CurrentSequence => _server.CurrentSequence;
}
