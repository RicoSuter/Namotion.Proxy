using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// What the OPC UA server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// A value of <c>true</c> means the server has started and is accepting client connections and
/// <c>false</c> means it is not, either because the server reported that or because it has stopped.
/// It reads <c>null</c> only while the server runs before its first liveness report.
/// <see cref="ConnectorDiagnostics.OperationalChangeTime"/> moves on every internal restart, where
/// <see cref="ConnectorDiagnostics.StartTime"/> marks the hosted service's own start and does not.
/// </remarks>
public sealed class OpcUaServerDiagnostics : ConnectorDiagnostics
{
    private readonly OpcUaSubjectServer _server;

    internal OpcUaServerDiagnostics(OpcUaSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of currently active client sessions.
    /// </summary>
    public int ActiveSessionCount => _server.ActiveSessionCount;

    /// <summary>
    /// Gets the number of consecutive startup failures, reset on a successful start.
    /// </summary>
    public int ConsecutiveFailures => _server.ConsecutiveFailures;
}
