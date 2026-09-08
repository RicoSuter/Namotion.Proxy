using System.Globalization;
using System.Text.Json;
using HomeBlaze.History.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.History.Mcp;

/// <summary>
/// Provides the get_property_history MCP tool.
///
/// A satellite of the history packages rather than part of the general HomeBlaze tool provider, so
/// installing history brings its MCP tool with it and the AI package does not have to reference the
/// history stack to expose a tool it knows nothing about. The host composes the two.
/// </summary>
public class HistoryMcpToolProvider : IMcpToolProvider
{
    private static readonly JsonElement GetPropertyHistorySchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            paths = new
            {
                type = "array",
                items = new { type = "string" },
                description = "One or more canonical property paths, e.g. /Devices/Sensor/Temperature."
            },
            from = new { type = "string", description = "Range start, ISO 8601. A bare timestamp is treated as UTC." },
            to = new { type = "string", description = "Range end, ISO 8601. Defaults to now." },
            bucket = new { type = "string", description = "Bucket size for downsampling, e.g. 5m, 30s, 1h, 7d. Omit for raw samples." },
            aggregation = new
            {
                type = "string",
                description = "Last, First, SampleAverage, TimeWeightedAverage, Minimum, Maximum, Sum, Count, " +
                              "or StandardDeviation. Case-insensitive. Defaults to Last."
            },
            max_points = new
            {
                type = "integer",
                description = "Maximum points per path. Defaults to (and is capped at) 10000 for raw samples " +
                              "and 1000 for bucketed ones. Lower it to keep the response small."
            }
        },
        required = new[] { "paths", "from" }
    });

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly PathProviderBase _pathProvider;
    private readonly ILogger<HistoryMcpToolProvider> _logger;

    public HistoryMcpToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        ILogger<HistoryMcpToolProvider> logger)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _pathProvider = pathProvider;
        _logger = logger;
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = "get_property_history",
            Description = "Query recorded history for one or more [State] property paths over a time range. " +
                          "Supports raw samples or bucketed downsampling with an aggregation. Returns a 'series' " +
                          "map keyed by path, each with a value_type hint, effective coverage ranges, the points " +
                          "(null entries are gaps), and a truncated flag; a path that cannot be served carries its " +
                          "own error and the others still return data. " +
                          "All input and output timestamps are UTC. " +
                          "Use browse or search to discover paths first.",
            InputSchema = GetPropertyHistorySchema,
            Handler = HandleGetPropertyHistoryAsync
        };
    }

    private const int MaxHistoryPaths = 10;

    // Recorded timestamps keep whatever offset their source supplied, so formatting them verbatim can
    // emit mixed offsets and break a caller that compares or sorts the strings, as the documented
    // all-UTC contract invites.
    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns a structured error when an optional parameter is present but is neither a string nor
    /// null, so a wrong-typed value is reported rather than silently falling back to its default.
    /// </summary>
    private static object? HasWrongType(JsonElement input, string name) =>
        input.TryGetProperty(name, out var element) &&
        element.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            ? new { error = $"Parameter '{name}' must be a string." }
            : null;

    private async Task<object?> HandleGetPropertyHistoryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        // Mirrors invoke_method: anything unexpected becomes a structured error rather than reaching the
        // MCP layer, which returns the raw exception message and so leaks internals to the caller.
        try
        {
            return await GetPropertyHistoryAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The get_property_history tool failed.");
            return new { error = "Could not read history. Check server logs for details." };
        }
    }

    private async Task<object?> GetPropertyHistoryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("paths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
        {
            return new { error = "Parameter 'paths' is required and must be an array of canonical property paths." };
        }

        var paths = pathsElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (paths.Length == 0)
        {
            return new { error = "Parameter 'paths' must contain at least one path." };
        }

        // Every path costs a full query against every store, and the budget below is per path, so an
        // unbounded list multiplies both the work and the response size.
        if (paths.Length > MaxHistoryPaths)
        {
            return new { error = $"Parameter 'paths' must contain at most {MaxHistoryPaths} paths." };
        }

        if (!input.TryGetProperty("from", out var fromElement) || fromElement.ValueKind != JsonValueKind.String)
        {
            return new { error = "Parameter 'from' is required (ISO 8601 timestamp)." };
        }

        // A present-but-wrong-typed parameter is rejected rather than ignored. Silently dropping a
        // numeric 'bucket' turned a bucketed request into a raw one with ten times the point budget,
        // and a numeric 'to' silently extended the range, both with no signal to the caller.
        if (HasWrongType(input, "to") is { } toTypeError)
        {
            return toTypeError;
        }

        if (HasWrongType(input, "bucket") is { } bucketTypeError)
        {
            return bucketTypeError;
        }

        if (HasWrongType(input, "aggregation") is { } aggregationTypeError)
        {
            return aggregationTypeError;
        }

        DateTimeOffset from;
        DateTimeOffset to;
        TimeSpan? bucket;
        try
        {
            from = HistoryToolParsing.ParseTimestamp(fromElement.GetString()!);
            to = input.TryGetProperty("to", out var toElement) && toElement.ValueKind == JsonValueKind.String
                ? HistoryToolParsing.ParseTimestamp(toElement.GetString()!)
                : DateTimeOffset.UtcNow;
            bucket = input.TryGetProperty("bucket", out var bucketElement) && bucketElement.ValueKind == JsonValueKind.String
                ? HistoryToolParsing.ParseBucket(bucketElement.GetString())
                : null;
        }
        catch (FormatException exception)
        {
            return new { error = $"Could not parse a time parameter: {exception.Message}" };
        }

        // The stores validate this too, but there it throws from inside the fan-out and surfaces as an
        // unstructured failure naming an internal parameter. A swapped or empty range is a routine
        // mistake for a caller composing timestamps, so it gets a structured answer.
        if (from >= to)
        {
            return new { error = "Parameter 'from' must be earlier than 'to'." };
        }

        var aggregation = HistoryToolParsing.NormalizeAggregation(
            input.TryGetProperty("aggregation", out var aggregationElement) ? aggregationElement.GetString() : null);
        if (aggregation is null)
        {
            return new
            {
                error = "Unknown aggregation.",
                available = HistoryToolParsing.AllAggregations.OrderBy(name => name).ToArray()
            };
        }

        var defaultMaxPoints = bucket is null ? 10_000 : 1_000;
        if (TryReadMaxPoints(input, defaultMaxPoints, out var maxPoints) is { } maxPointsError)
        {
            return maxPointsError;
        }

        var rootSubject = _rootSubjectProvider();
        var registry = rootSubject.Context.GetService<ISubjectRegistry>();
        var rootRegistered = rootSubject.TryGetRegisteredSubject();
        var stores = registry.KnownSubjects.Keys.OfType<IHistoryStore>().ToArray();

        // One query per path with its own error boundary. The fan-out overload fails the whole call if
        // any single path throws, so asking for one property no store can aggregate threw away the
        // results for every other path in the same request.
        var results = await Task.WhenAll(paths.Select(path => QueryOnePathAsync(
                stores, rootRegistered, path, from, to, bucket, aggregation, maxPoints, cancellationToken)))
            .ConfigureAwait(false);

        var series = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (path, payload) in results)
        {
            series[path] = payload;
        }

        // Nested rather than returned as the top-level map: a path is caller-supplied, so a bare map
        // keyed by path cannot be told apart from the tool's own { error = ... } shape.
        return new { series };
    }

    private async Task<(string Path, object? Payload)> QueryOnePathAsync(
        IReadOnlyList<IHistoryStore> stores,
        RegisteredSubject? rootRegistered,
        string path,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? bucket,
        string aggregation,
        int maxPoints,
        CancellationToken cancellationToken)
    {
        // Resolution runs inside the boundary too: walking a path invokes property getters, so one
        // throwing getter would otherwise fault the whole fan-out before any query started.
        string? valueType = null;
        HistorySeries result;
        try
        {
            var resolved = rootRegistered is null
                ? null
                : _pathProvider.TryGetPropertyFromPath(rootRegistered, path);

            // Null rather than a guess when the type is not one history records: reporting an
            // unrecordable property as "number" invites the caller to chart something that will never
            // have samples.
            if (resolved is { } match && HistoryColumns.IsRecordable(match.Property.Type))
            {
                valueType = HistoryToolParsing.ValueType(match.Property.Type);
            }

            result = await stores
                .QueryHistoryAsync(
                    new HistoryQuery(path, from, to, bucket, aggregation, maxPoints), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HistoryAggregationNotSupportedException exception)
        {
            return (path, new
            {
                value_type = valueType,
                error = exception.Message,
                available = exception.Available.OrderBy(name => name).ToArray()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up on the whole request; there is nothing to isolate. A cancellation
            // raised by a store's own internal timeout is a store failure and is isolated below.
            throw;
        }
        catch (Exception exception)
        {
            // Any other store failure is isolated to its own path too. Letting it escape faulted the
            // whole fan-out and discarded the results for every sibling path that had already
            // succeeded, which is exactly what this tool promises not to do.
            _logger.LogError(exception, "Reading history for '{PropertyPath}' failed.", path);
            return (path, new
            {
                value_type = valueType,
                error = "Could not read history for this path. Check server logs for details."
            });
        }

        return (path, new
        {
            value_type = valueType,
            truncated = result.Truncated,
            coverage = result.CoverageRanges.Select(range => new
            {
                from = FormatUtc(range.From),
                to = FormatUtc(range.To)
            }).ToArray(),
            points = result.Points.Select(point => new
            {
                t = FormatUtc(point.Timestamp),
                value = point.Number is { } number ? (object?)number
                      : point.Json is { } json ? json
                      : null
            }).ToArray()
        });
    }

    /// <summary>
    /// Reads the optional max_points override. It can only lower the default: the defaults bound the
    /// response size across up to ten paths, so letting a caller raise them is how one request turns
    /// into a multi-megabyte payload.
    /// </summary>
    private static object? TryReadMaxPoints(JsonElement input, int defaultMaxPoints, out int maxPoints)
    {
        maxPoints = defaultMaxPoints;
        if (!input.TryGetProperty("max_points", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var requested))
        {
            return new { error = "Parameter 'max_points' must be an integer." };
        }

        if (requested < 1)
        {
            return new { error = "Parameter 'max_points' must be at least 1." };
        }

        maxPoints = Math.Min(requested, defaultMaxPoints);
        return null;
    }
}
