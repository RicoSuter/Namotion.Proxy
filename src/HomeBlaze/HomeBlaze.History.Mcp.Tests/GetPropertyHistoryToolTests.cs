using System.Text.Json;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.History.Mcp.Tests;

public class GetPropertyHistoryToolTests
{
    private static Task<JsonElement> InvokeAsync(object inputObject) =>
        InvokeAsync(inputObject, unsupportedPath: null, withStore: false);

    private static async Task<JsonElement> InvokeAsync(object inputObject, string? unsupportedPath, bool withStore)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var root = new HistoryToolTestSubject(context) { Name = "Test Room" };
        if (withStore)
        {
            // Registered in the same context, so the merger picks it up from the registry's subjects.
            _ = new TestHistoryStore(context) { UnsupportedPath = unsupportedPath };
        }

        var provider = new HistoryMcpToolProvider(
            () => root, new StateAttributePathProvider(), NullLogger<HistoryMcpToolProvider>.Instance);

        var tool = provider.GetTools().First(candidate => candidate.Name == "get_property_history");
        var input = JsonSerializer.SerializeToElement(inputObject);
        var result = await tool.Handler(input, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result);
    }

    [Fact]
    public async Task WhenOnePathCannotBeServed_ThenOnlyThatPathCarriesAnErrorAndTheOthersReturnData()
    {
        // Arrange - a store that rejects the aggregation for one path only, the way a real store does
        // when a numeric aggregation is asked of a string column.
        var json = await InvokeAsync(
            new
            {
                paths = new[] { "/a/Text", "/b/Value" },
                from = "2026-06-24T00:00:00Z",
                to = "2026-06-24T01:00:00Z",
                aggregation = "SampleAverage"
            },
            unsupportedPath: "/a/Text",
            withStore: true);

        // Assert - the whole call used to fail, throwing away the paths that could be served.
        Assert.False(json.TryGetProperty("error", out _));
        var series = json.GetProperty("series");
        Assert.True(series.GetProperty("/a/Text").TryGetProperty("error", out _));
        Assert.False(series.GetProperty("/b/Value").TryGetProperty("error", out _));
        Assert.True(series.GetProperty("/b/Value").TryGetProperty("points", out _));
    }

    [Fact]
    public async Task WhenMaxPointsIsNotAnInteger_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            max_points = "many"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenMaxPointsIsZeroOrNegative_ThenStructuredError()
    {
        // Act - the stores reject a non-positive budget from inside the fan-out, which surfaces
        // unstructured and naming an internal parameter.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            max_points = 0
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenPropertyTypeIsNotRecordable_ThenValueTypeIsNullRatherThanNumber()
    {
        // Arrange - Child is a subject-valued property: nothing records it, so claiming a value type
        // for it invites the caller to chart a series that can never have samples.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/Child" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.Equal(
            JsonValueKind.Null,
            json.GetProperty("series").GetProperty("/Child").GetProperty("value_type").ValueKind);
    }

    [Fact]
    public async Task WhenPathsMissing_ThenError()
    {
        // Act
        var json = await InvokeAsync(new { from = "2026-06-24T00:00:00Z" });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenToIsNotAfterFrom_ThenStructuredError()
    {
        // Act: a swapped range, which is a routine mistake when composing timestamps.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T12:00:00Z",
            to = "2026-06-24T06:00:00Z"
        });

        // Assert: a structured error, not an exception surfaced from inside the stores.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenBucketIsNotAString_ThenStructuredErrorRatherThanSilentRawQuery()
    {
        // Act: a caller expressing seconds as a number. Ignoring it turned this into a raw query with
        // ten times the point budget, with nothing telling the caller its bucket was dropped.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = 300
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenAggregationIsNotAString_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            aggregation = 0
        });

        // Assert: previously an InvalidOperationException escaped the handler entirely.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenBucketOverflows_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = "99999999999d"
        });

        // Assert: OverflowException is not a FormatException, so it used to escape the parse handler.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenTooManyPaths_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = Enumerable.Range(0, 50).Select(index => $"/a/Value{index}").ToArray(),
            from = "2026-06-24T00:00:00Z"
        });

        // Assert: each path costs a full query per store, so the list has to be bounded.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenFromMissing_ThenError()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/a/Value" } });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenPathsContainsOnlyNonStringElements_ThenStructuredErrorNotException()
    {
        // Act - a non-string element must not throw; it yields the structured "at least one path" error.
        var json = await InvokeAsync(new
        {
            paths = new object[] { 123 },
            from = "2026-06-24T00:00:00Z"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("at least one path", error.GetString());
    }

    [Fact]
    public async Task WhenPathsMixesStringAndNonStringElements_ThenNonStringIgnoredAndNoError()
    {
        // Act - the non-string element is skipped and the valid string path drives a normal (empty) result.
        var json = await InvokeAsync(new
        {
            paths = new object[] { "/a/Value", 123 },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.True(json.GetProperty("series").TryGetProperty("/a/Value", out _));
    }

    [Fact]
    public async Task WhenAggregationUnknown_ThenErrorWithAvailableSet()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            aggregation = "bogus"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
        var available = json.GetProperty("available").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("TimeWeightedAverage", available);
        Assert.Contains("Last", available);
    }

    [Fact]
    public async Task WhenBucketInvalid_ThenError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = "5x"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenValidRawQueryWithNoStores_ThenPerPathEntriesWithEmptyPoints()
    {
        // Arrange - default aggregation Last (AlwaysAvailable), no stores -> empty points, not an error.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value", "/b/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        var series = json.GetProperty("series");
        foreach (var path in new[] { "/a/Value", "/b/Value" })
        {
            Assert.True(series.TryGetProperty(path, out var entry));
            Assert.True(entry.TryGetProperty("value_type", out _));     // present (null when unresolved)
            Assert.Empty(entry.GetProperty("coverage").EnumerateArray());
            Assert.Empty(entry.GetProperty("points").EnumerateArray());
            Assert.False(entry.GetProperty("truncated").GetBoolean());
        }
    }

    [Fact]
    public async Task WhenBucketAndCaseInsensitiveCountAggregation_ThenNoError()
    {
        // Arrange - "count" normalizes to Count (AlwaysAvailable); bucket "5m" parses; no stores -> empty, no error.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            bucket = "5m",
            aggregation = "count"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.True(json.GetProperty("series").TryGetProperty("/a/Value", out _));
    }
}
