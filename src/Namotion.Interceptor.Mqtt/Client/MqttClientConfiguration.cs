using System;
using MQTTnet.Protocol;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Mqtt.Mapping;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mqtt.Client;

/// <summary>
/// Configuration for MQTT client source.
/// </summary>
public class MqttClientConfiguration
{
    private static readonly IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> DefaultMapper =
        new MqttCompositeMapper(
            new MqttPathProviderMapper(new AttributeBasedPathProvider(MqttConstants.DefaultConnectorName, '/')),
            new MqttAttributeMapper(MqttConstants.DefaultConnectorName));

    /// <summary>
    /// Gets or sets the MQTT broker hostname or IP address.
    /// </summary>
    public required string BrokerHost { get; init; }

    /// <summary>
    /// Gets or sets the MQTT broker port. Default is 1883.
    /// </summary>
    public int BrokerPort { get; init; } = 1883;

    /// <summary>
    /// Gets or sets the username for broker authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Gets or sets the password for broker authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets or sets whether to use TLS/SSL for the connection. Default is false.
    /// </summary>
    public bool UseTls { get; init; }

    /// <summary>
    /// Gets or sets the client identifier. Default is a unique GUID-based identifier.
    /// </summary>
    public string ClientId { get; init; } = $"Namotion_{Guid.NewGuid():N}";

    /// <summary>
    /// Gets or sets whether to use a clean session. Default is true.
    /// When false, the broker preserves subscriptions and queued messages across reconnects.
    /// </summary>
    public bool CleanSession { get; init; } = true;

    /// <summary>
    /// Gets or sets the optional topic prefix. When set, all topics are prefixed with this value.
    /// </summary>
    public string? TopicPrefix { get; init; }

    /// <summary>
    /// Gets or sets the property mapper for property-to-topic mapping.
    /// Defaults to composite of MqttPathProviderMapper and MqttAttributeMapper, both filtered by the "mqtt" connector name.
    /// </summary>
    public IReversePropertyMapper<MqttPropertyMapping, MqttLookupKey> Mapper { get; init; } = DefaultMapper;

    // QoS settings

    /// <summary>
    /// Gets or sets the default QoS level for publish/subscribe operations. Default is AtLeastOnce (1) for guaranteed delivery.
    /// Use AtMostOnce (0) only when message loss is acceptable and lowest latency is required.
    /// </summary>
    public MqttQualityOfServiceLevel DefaultQualityOfService { get; init; } = MqttQualityOfServiceLevel.AtLeastOnce;

    /// <summary>
    /// Gets or sets whether to use retained messages. Default is true so new subscribers receive
    /// the last known value. Disable for slightly better throughput if not needed.
    /// </summary>
    public bool UseRetainedMessages { get; init; } = true;
    
    /// <summary>
    /// Gets or sets the connection timeout. Default is 10 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the initial delay before attempting to reconnect. Default is 2 seconds.
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum delay between reconnection attempts (for exponential backoff). Default is 60 seconds.
    /// </summary>
    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the keep-alive interval. Default is 15 seconds.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the interval for connection health checks. Default is 30 seconds.
    /// Health checks use TryPingAsync to verify the connection is still alive.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum number of health check iterations while reconnecting before forcing a reset.
    /// Default is 10 iterations. Combined with HealthCheckInterval, this provides a stall timeout.
    /// Example: 10 iterations × 30s = 5 minutes timeout for hung reconnection attempts.
    /// Set to 0 to disable stall detection.
    /// </summary>
    public int ReconnectStallThreshold { get; init; } = 10;
    
    /// <summary>
    /// Gets or sets the number of consecutive connection failures before the circuit breaker opens.
    /// Default is 5 failures. When open, reconnection attempts are paused for the cooldown period.
    /// Set to 0 to disable circuit breaker.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; init; } = 5;

    /// <summary>
    /// Gets or sets the cooldown period after the circuit breaker opens. Default is 60 seconds.
    /// During cooldown, no reconnection attempts are made. After cooldown, one retry is allowed.
    /// </summary>
    public TimeSpan CircuitBreakerCooldown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 8ms.
    /// Higher values create larger batches for better throughput, lower values reduce latency.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the time between retry attempts for failed writes. Default is 10 seconds.
    /// </summary>
    public TimeSpan RetryTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum number of writes to queue during disconnection. Default is 1000.
    /// Set to 0 to disable write buffering.
    /// </summary>
    public int WriteRetryQueueSize { get; init; } = 1000;

    /// <summary>
    /// Gets or sets the value converter for serialization/deserialization. Default is JSON.
    /// </summary>
    public IMqttValueConverter ValueConverter { get; init; } = new JsonMqttValueConverter();
    
    /// <summary>
    /// Gets or sets the MQTT user property name for the source timestamp. Default is "ts".
    /// Set to null to disable timestamp extraction.
    /// </summary>
    public string? SourceTimestampPropertyName { get; init; } = "ts";

    /// <summary>
    /// Gets or sets the function for serializing timestamps to bytes for MQTT user properties.
    /// Default converts to Unix milliseconds as UTF8. Must be paired with a matching <see cref="SourceTimestampDeserializer"/>.
    /// </summary>
    public Func<DateTimeOffset, byte[]> SourceTimestampSerializer { get; init; } = MqttHelper.DefaultSerializeTimestamp;

    /// <summary>
    /// Gets or sets the function for deserializing timestamp bytes from MQTT user properties.
    /// Default parses Unix milliseconds from UTF8. Must be paired with a matching <see cref="SourceTimestampSerializer"/>.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, DateTimeOffset?> SourceTimestampDeserializer { get; init; } = MqttHelper.DefaultDeserializeTimestamp;

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when configuration is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BrokerHost))
        {
            throw new ArgumentException("BrokerHost must be specified.", nameof(BrokerHost));
        }

        if (BrokerPort is < 1 or > 65535)
        {
            throw new ArgumentException($"BrokerPort must be between 1 and 65535, got: {BrokerPort}", nameof(BrokerPort));
        }

        if (Mapper is null)
        {
            throw new ArgumentException("Mapper must be specified.", nameof(Mapper));
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ConnectTimeout must be positive, got: {ConnectTimeout}", nameof(ConnectTimeout));
        }

        if (ReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException($"ReconnectDelay must be positive, got: {ReconnectDelay}", nameof(ReconnectDelay));
        }

        if (MaximumReconnectDelay < ReconnectDelay)
        {
            throw new ArgumentException($"MaxReconnectDelay must be >= ReconnectDelay, got: {MaximumReconnectDelay}", nameof(MaximumReconnectDelay));
        }

        if (WriteRetryQueueSize < 0)
        {
            throw new ArgumentException($"WriteRetryQueueSize must be non-negative, got: {WriteRetryQueueSize}", nameof(WriteRetryQueueSize));
        }

        if (ValueConverter is null)
        {
            throw new ArgumentException("ValueConverter must be specified.", nameof(ValueConverter));
        }

        if (ReconnectStallThreshold < 0)
        {
            throw new ArgumentException($"ReconnectStallThreshold must be non-negative, got: {ReconnectStallThreshold}", nameof(ReconnectStallThreshold));
        }

        if (CircuitBreakerFailureThreshold < 0)
        {
            throw new ArgumentException($"CircuitBreakerFailureThreshold must be non-negative, got: {CircuitBreakerFailureThreshold}", nameof(CircuitBreakerFailureThreshold));
        }

        if (CircuitBreakerCooldown <= TimeSpan.Zero && CircuitBreakerFailureThreshold > 0)
        {
            throw new ArgumentException($"CircuitBreakerCooldown must be positive when circuit breaker is enabled, got: {CircuitBreakerCooldown}", nameof(CircuitBreakerCooldown));
        }
    }
}
