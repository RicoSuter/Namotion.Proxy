namespace Namotion.Interceptor.Connectors.Reconciliation;

/// <summary>Experimental capability for reading authoritative scalar source values.</summary>
/// <remarks>Return current source values, not cached notifications. Omitted properties remain pending and are retried.</remarks>
public interface ISourcePropertyReader
{
    ValueTask<IReadOnlyList<SourcePropertyValue>> ReadPropertiesAsync(
        ReadOnlyMemory<PropertyReference> properties, CancellationToken cancellationToken);
}

/// <summary>A scalar value read from the authoritative source.</summary>
public readonly record struct SourcePropertyValue(
    PropertyReference Property, object? Value, DateTimeOffset? Timestamp = null, DateTimeOffset? ReceivedTimestamp = null);
