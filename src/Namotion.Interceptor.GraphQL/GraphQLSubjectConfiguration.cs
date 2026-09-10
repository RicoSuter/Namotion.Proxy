using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.GraphQL;

/// <summary>
/// Configuration for GraphQL subject integration.
/// </summary>
public class GraphQLSubjectConfiguration
{
    /// <summary>
    /// Gets or sets the root field name for queries and subscriptions. Default is "root".
    /// Must not be null, empty, or whitespace.
    /// </summary>
    public string RootName { get; init; } = "root";

    /// <summary>
    /// Gets or sets the path provider for property-to-GraphQL field mapping.
    /// Default uses camelCase to match GraphQL conventions.
    /// </summary>
    public IPathProvider PathProvider { get; init; } = CamelCasePathProvider.Instance;

    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 50ms.
    /// Higher values batch more changes together, lower values reduce latency.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Validates this configuration and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootName))
        {
            throw new ArgumentException(
                "RootName must not be null, empty, or whitespace.", nameof(RootName));
        }
    }
}
