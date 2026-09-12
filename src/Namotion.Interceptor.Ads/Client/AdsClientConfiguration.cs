using System;
using System.Net;
using Microsoft.Extensions.Configuration;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Ads.Attributes;
using Namotion.Interceptor.Ads.Mapping;
using TwinCAT.Ads;

namespace Namotion.Interceptor.Ads.Client;

/// <summary>
/// Configuration for the TwinCAT ADS client source.
/// </summary>
public class AdsClientConfiguration
{
    /// <summary>
    /// When set, enables embedded-router mode: the PLC IP or hostname the connector routes to via an
    /// in-process AMS router. When null, the system AMS router is used and AmsNetId addresses the target.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// Target AMS Net ID. Optional when Host is an IP (defaults to {Host}.1.1).
    /// Use <see cref="AmsNetId.Local"/> for a local loopback connection (with Host null).
    /// </summary>
    public AmsNetId? AmsNetId { get; set; }

    /// <summary>
    /// Embedded mode only: the local net id the in-process router presents to the PLC
    /// (the PLC's route back must match it). Defaults to the local IP + ".1.1".
    /// </summary>
    public AmsNetId? LocalAmsNetId { get; set; }

    /// <summary>
    /// Gets whether embedded-router mode is enabled (true when <see cref="Host"/> is set).
    /// </summary>
    public bool UseEmbeddedRouter => Host is not null;

    /// <summary>
    /// Resolves the target AMS Net ID, deriving {Host}.1.1 when Host is an IP and AmsNetId is unset.
    /// </summary>
    public AmsNetId GetTargetAmsNetId()
    {
        if (AmsNetId is not null) return AmsNetId;
        if (Host is not null && IPAddress.TryParse(Host, out _)) return global::TwinCAT.Ads.AmsNetId.Parse($"{Host}.1.1");
        throw new InvalidOperationException("AmsNetId must be set when Host is null or a hostname.");
    }

    /// <summary>
    /// Gets or sets the AMS port (default: 851 for TwinCAT3 PLC runtime).
    /// </summary>
    public int AmsPort { get; set; } = 851;

    /// <summary>
    /// Gets or sets the ADS communication timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the property mapper that resolves each property's symbol-path segment and ADS settings.
    /// Defaults to attribute-based mapping (<see cref="AdsVariableAttribute"/>). Compose a fluent mapper in to
    /// add or override mappings in code.
    /// </summary>
    public IPropertyMapper<AdsPropertyMapping> Mapper { get; set; } = AdsCompositeMapper.CreateDefault();

    /// <summary>
    /// Gets or sets the default read mode for variables without explicit configuration.
    /// </summary>
    public AdsReadMode DefaultReadMode { get; set; } = AdsReadMode.Auto;

    /// <summary>
    /// Gets or sets the default notification cycle time in milliseconds.
    /// </summary>
    public int DefaultCycleTime { get; set; } = 100;

    /// <summary>
    /// Gets or sets the default max delay for notification batching in milliseconds.
    /// </summary>
    public int DefaultMaxDelay { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum number of concurrent ADS notifications before demotion.
    /// </summary>
    /// <remarks>
    /// Set to 0 to leave no notification budget for <see cref="AdsReadMode.Auto"/> properties, which
    /// demotes all of them to polling. A property that asks for <see cref="AdsReadMode.Notification"/>
    /// explicitly is never demoted and still gets one, so this is not the same as disabling
    /// notifications outright.
    /// </remarks>
    public int MaxNotifications { get; set; } = 500;

    /// <summary>
    /// Gets or sets whether the symbol table and data types are fetched when the symbol loader is
    /// created, rather than on the first symbol access.
    /// </summary>
    /// <remarks>
    /// The fetch happens either way. Left to itself it runs synchronously, on whichever thread
    /// first touches the symbols, which Beckhoff advises against for the first call. Fetching up
    /// front moves it onto a thread that is allowed to wait and gets it out of the way before
    /// DynamicTree starts building struct members, array elements and enum fields from the type
    /// information. Set to false to restore the lazy load, which also defers any error in it to
    /// the first symbol access.
    /// </remarks>
    public bool PrefetchSymbols { get; set; } = true;

    /// <summary>
    /// Gets or sets how many polled reads may be in flight at once, or 0 for no limit.
    /// </summary>
    /// <remarks>
    /// Only reached when sum commands cannot resolve the polled symbols, which makes the round trip
    /// count per pass fixed and this the only lever on pass duration. The work is IO, so the default
    /// issues every read at once. Median pass over 2290 symbols against a usermode runtime: 8998 ms
    /// sequential, 1149 ms at 8 in flight, 387 ms at 32, 151 ms at 128, 16 ms unthrottled, with no
    /// failed read at any setting, and lower process CPU unthrottled than sequential. Set a positive
    /// value to bound it on a router that does not tolerate the burst; 1 restores sequential polling.
    /// </remarks>
    public int MaxConcurrentReads { get; set; }

    /// <summary>
    /// Gets or sets the polling interval for polled/demoted variables.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the write retry queue size.
    /// </summary>
    public int WriteRetryQueueSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the health check interval for monitoring.
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the debounce time for rescan requests.
    /// When multiple events (connection restored, PLC state change, symbol version change) fire
    /// in rapid succession, the rescan waits until this time has elapsed since the last request
    /// before executing, coalescing multiple events into a single rescan.
    /// </summary>
    public TimeSpan RescanDebounceTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the buffer time for batching inbound updates.
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets the retry time for failed writes.
    /// </summary>
    public TimeSpan RetryTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the circuit breaker failure threshold.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Gets or sets the circuit breaker cooldown period.
    /// </summary>
    public TimeSpan CircuitBreakerCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the value converter for ADS/PLC type conversions.
    /// </summary>
    public AdsValueConverter ValueConverter { get; set; } = new();

    /// <summary>
    /// Gets or sets the optional router configuration for the ADS client.
    /// When set, the ADS client uses a custom loopback port (useful for testing without TwinCAT installed).
    /// When null, the default ADS client behavior is used.
    /// </summary>
    public IConfiguration? RouterConfiguration { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (Mapper is null)
            throw new ArgumentException("Mapper must not be null.", nameof(Mapper));

        if (Host is null && AmsNetId is null)
            throw new ArgumentException("Either Host or AmsNetId must be set.", nameof(AmsNetId));

        if (AmsNetId is null && Host is not null && !IPAddress.TryParse(Host, out _))
            throw new ArgumentException("AmsNetId must be set when Host is a hostname (the net id cannot be derived).", nameof(AmsNetId));

        if (AmsPort <= 0)
            throw new ArgumentOutOfRangeException(nameof(AmsPort), "AmsPort must be positive.");

        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be positive.");

        if (DefaultCycleTime <= 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultCycleTime), "DefaultCycleTime must be positive.");

        if (MaxNotifications < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxNotifications), "MaxNotifications must be non-negative.");

        if (PollingInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollingInterval), "PollingInterval must be positive.");

        if (MaxConcurrentReads < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentReads),
                "MaxConcurrentReads must be non-negative; 0 means no limit.");

        if (WriteRetryQueueSize < 0)
            throw new ArgumentOutOfRangeException(nameof(WriteRetryQueueSize), "WriteRetryQueueSize must be non-negative.");

        if (HealthCheckInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HealthCheckInterval), "HealthCheckInterval must be positive.");

        if (CircuitBreakerFailureThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(CircuitBreakerFailureThreshold), "CircuitBreakerFailureThreshold must be positive.");

        if (CircuitBreakerCooldown <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CircuitBreakerCooldown), "CircuitBreakerCooldown must be positive.");

        if (DefaultMaxDelay < 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultMaxDelay), "DefaultMaxDelay must be non-negative.");

        if (RescanDebounceTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RescanDebounceTime), "RescanDebounceTime must be non-negative.");
    }
}
