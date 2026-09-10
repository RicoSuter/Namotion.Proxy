using System.Text.Json;
using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// Creates the "search" tool for flat search across all known subjects.
/// </summary>
internal class SearchTool
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly McpServerConfiguration _configuration;

    public SearchTool(Func<IInterceptorSubject> rootSubjectProvider, McpServerConfiguration configuration)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _configuration = configuration;
    }

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            format = new { type = "string", @enum = new[] { "text", "json" }, description = "Output format: 'text' (default) for LLM-readable overview, 'json' for exact structured data" },
            types = new { type = "array", items = new { type = "string" }, description = "Filter by type/interface names (short or full). Use list_types first to discover the correct names." },
            includeProperties = new { type = "boolean", description = "Include property values (default: false)" },
            includeAttributes = new { type = "boolean", description = "Include registry attributes on properties (default: false)" },
            includeMethods = new { type = "boolean", description = "Include $methods in subject nodes (default: false)" },
            includeInterfaces = new { type = "boolean", description = "Include $interfaces in subject nodes (default: false)" },
            maxSubjects = new { type = "integer", description = "Maximum subjects to return (default: server limit)" },
            excludeTypes = new { type = "array", items = new { type = "string" }, description = "Exclude subjects matching these type/interface names (short or full)" },
            path = new { type = "string", description = "Scope search to a subtree path prefix" },
            query = new { type = "string", description = "Filter by path and enrichment values (e.g. $title, $icon) ONLY — does NOT match types, interfaces, or property names. Use list_types + types parameter for capability filtering" }
        }
    });

    public McpToolInfo CreateTool() => new()
    {
        Name = "search",
        Description = "Find subjects by type/interface names or by enrichments (e.g. title, path). " +
                      "IMPORTANT: query only matches path and enrichment values (e.g. $title, $icon) — NOT types, interfaces, capabilities, or property names. " +
                      "To find subjects by capability, call list_types first to get the interface name, then pass it to types. " +
                      "Use format=json for structured data, text (default) for overview.",
        InputSchema = Schema,
        Handler = HandleSearchAsync
    };

    private Task<object?> HandleSearchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var pathProvider = _configuration.PathProvider as PathProviderBase
            ?? throw new InvalidOperationException("PathProvider must extend PathProviderBase.");

        var registry = _rootSubjectProvider().TryGetContext()?.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return Task.FromResult<object?>(new { error = "Subject registry is not available." });
        }

        var format = input.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : "text";
        var includeProperties = input.TryGetProperty("includeProperties", out var propsElement) && propsElement.GetBoolean();
        var includeAttributes = input.TryGetProperty("includeAttributes", out var attrsElement) && attrsElement.GetBoolean();
        var includeMethods = input.TryGetProperty("includeMethods", out var methodsElement) && methodsElement.GetBoolean();
        var includeInterfaces = input.TryGetProperty("includeInterfaces", out var interfacesElement) && interfacesElement.GetBoolean();

        var maxSubjects = input.TryGetProperty("maxSubjects", out var maxElement)
            ? Math.Min(maxElement.GetInt32(), _configuration.MaxSubjectsPerResponse)
            : _configuration.MaxSubjectsPerResponse;

        string[]? typeFilter = null;
        if (input.TryGetProperty("types", out var typesElement))
        {
            typeFilter = typesElement.EnumerateArray().Select(element => element.GetString()!).ToArray();
        }

        string[]? excludeTypes = null;
        if (input.TryGetProperty("excludeTypes", out var excludeElement))
        {
            excludeTypes = excludeElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        var pathPrefix = input.TryGetProperty("path", out var pathPrefixElement) ? pathPrefixElement.GetString() : null;
        var query = input.TryGetProperty("query", out var queryElement) ? queryElement.GetString() : null;

        var subjects = new Dictionary<string, SubjectNode>();
        var truncated = false;
        var rootSubject = _rootSubjectProvider();

        foreach (var (_, registered) in registry.KnownSubjects)
        {
            if (typeFilter is not null && typeFilter.Length > 0 && ShouldFilterOutByType(registered, typeFilter))
            {
                continue;
            }

            if (McpToolHelper.ShouldExcludeByType(registered, _configuration.ExcludeTypes, excludeTypes))
            {
                continue;
            }

            // Compute path cheaply before building full DTO
            var path = McpToolHelper.TryGetSubjectPath(registered, pathProvider, rootSubject, _configuration.PathPrefix);
            if (path is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(pathPrefix) &&
                !path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query) && !MatchesQuery(registered, path, query))
            {
                continue;
            }

            if (subjects.Count >= maxSubjects)
            {
                truncated = true;
                break;
            }

            // Build full DTO only for subjects that pass all filters
            var node = McpToolHelper.BuildSubjectNodeDto(
                registered, pathProvider, rootSubject, _configuration,
                includeProperties, includeAttributes, includeMethods, includeInterfaces);

            subjects[path] = node;
        }

        var searchResult = new SearchResult
        {
            Results = subjects,
            SubjectCount = subjects.Count,
            Truncated = truncated
        };

        if (format == "json")
        {
            return Task.FromResult<object?>(searchResult);
        }

        return Task.FromResult<object?>(McpTextFormatter.FormatSearchResult(searchResult));
    }

    private bool MatchesQuery(RegisteredSubject subject, string path, string query)
    {
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var enricher in _configuration.SubjectEnrichers)
        {
            foreach (var kvp in enricher.GetSubjectEnrichments(subject))
            {
                if (kvp.Value is string stringValue &&
                    stringValue.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ShouldFilterOutByType(RegisteredSubject subject, string[] typeFilter)
    {
        var subjectType = subject.Subject.GetType();
        foreach (var filter in typeFilter)
        {
            if (subjectType.FullName == filter || subjectType.Name == filter)
            {
                return false;
            }

            if (subjectType.GetInterfaces().Any(interfaceType =>
                interfaceType.FullName == filter || interfaceType.Name == filter))
            {
                return false;
            }
        }

        return true;
    }
}
