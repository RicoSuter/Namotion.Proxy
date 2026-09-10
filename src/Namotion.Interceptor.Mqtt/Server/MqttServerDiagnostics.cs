using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// What the MQTT server reports about its transport, on top of the shared connector diagnostics.
/// </summary>
/// <remarks>
/// A value of <c>true</c> means the broker is listening and <c>false</c> means it is not, either
/// because the server reported that or because it has stopped. It reads <c>null</c> only while the
/// server runs before its first liveness report. Neither throughput direction is measured, so both
/// rates are <c>null</c> rather than 0.
/// </remarks>
public sealed class MqttServerDiagnostics : ConnectorDiagnostics
{
    private readonly MqttSubjectServer _server;

    internal MqttServerDiagnostics(MqttSubjectServer server, ConnectorMetrics metrics)
        : base(metrics)
    {
        _server = server;
    }

    /// <summary>
    /// Gets the number of clients currently connected to the broker.
    /// </summary>
    public int ConnectedClientCount => _server.ConnectedClientCount;
}
