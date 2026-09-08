using System;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.WebSocket.Server;

/// <summary>
/// Configuration for WebSocket subject server.
/// </summary>
public class WebSocketServerConfiguration
{
    /// <summary>
    /// Port to listen on. Default: 8080
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// WebSocket path. Default: "/ws"
    /// </summary>
    public string Path { get; set; } = "/ws";

    /// <summary>
    /// Bind address. Default: any (null)
    /// </summary>
    public string? BindAddress { get; set; }

    /// <summary>
    /// Buffer time for batching outbound updates. Default: 8ms
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Maximum number of property changes per WebSocket message. Default: 1000.
    /// Smaller batches reduce latency, larger batches reduce per-message overhead.
    /// Set to 0 for unlimited (not recommended for large object graphs).
    /// </summary>
    public int WriteBatchSize { get; set; } = 1000;

    /// <summary>
    /// Maximum message size in bytes. Messages larger than this will be rejected. Default: 10MB
    /// </summary>
    public long MaxMessageSize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of concurrent connections. Default: 1000
    /// </summary>
    public int MaxConnections { get; set; } = 1000;

    /// <summary>
    /// Timeout for receiving Hello message from client. Default: 10 seconds.
    /// Clients that don't send Hello within this time will be disconnected.
    /// </summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The smallest heartbeat interval that can be configured.
    /// </summary>
    public static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Interval between heartbeat messages. Default: 30 seconds.
    /// Set to TimeSpan.Zero to disable heartbeats, otherwise at least
    /// <see cref="MinimumHeartbeatInterval"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for broadcasting updates and heartbeats to all connected clients. Default: 10 seconds.
    /// Sends that haven't completed continue in the background; zombie detection cleans up persistently slow connections.
    /// </summary>
    public TimeSpan BroadcastTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for acquiring the per-connection send lock. Default: 5 seconds.
    /// If a previous send to a connection is still in progress after this time,
    /// the new send is skipped and counted as a failure (triggering zombie detection
    /// for persistently slow clients).
    /// </summary>
    public TimeSpan SendLockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Path provider for property filtering/mapping.
    /// </summary>
    public PathProviderBase? PathProvider { get; set; }

    /// <summary>
    /// Subject factory for creating subjects from client updates.
    /// </summary>
    public ISubjectFactory? SubjectFactory { get; set; }

    /// <summary>
    /// Update processors for filtering/transforming updates.
    /// </summary>
    public ISubjectUpdateProcessor[] Processors { get; set; } = [];

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port must be between 1 and 65535, got: {Port}", nameof(Port));
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("Path must be specified.", nameof(Path));
        }

        if (BufferTime < TimeSpan.Zero)
        {
            throw new ArgumentException($"BufferTime must be non-negative, got: {BufferTime}", nameof(BufferTime));
        }

        if (MaxMessageSize <= 0)
        {
            throw new ArgumentException($"MaxMessageSize must be positive, got: {MaxMessageSize}", nameof(MaxMessageSize));
        }

        if (MaxConnections <= 0)
        {
            throw new ArgumentException($"MaxConnections must be positive, got: {MaxConnections}", nameof(MaxConnections));
        }

        if (HelloTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"HelloTimeout must be positive, got: {HelloTimeout}", nameof(HelloTimeout));
        }

        if (WriteBatchSize < 0)
        {
            throw new ArgumentException($"WriteBatchSize must be non-negative, got: {WriteBatchSize}", nameof(WriteBatchSize));
        }

        if (HeartbeatInterval != TimeSpan.Zero && HeartbeatInterval < MinimumHeartbeatInterval)
        {
            throw new ArgumentException(
                $"HeartbeatInterval must be TimeSpan.Zero to disable heartbeats, or at least {MinimumHeartbeatInterval}, got: {HeartbeatInterval}",
                nameof(HeartbeatInterval));
        }

        if (BroadcastTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"BroadcastTimeout must be positive, got: {BroadcastTimeout}", nameof(BroadcastTimeout));
        }

        if (SendLockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"SendLockTimeout must be positive, got: {SendLockTimeout}", nameof(SendLockTimeout));
        }
    }
}
