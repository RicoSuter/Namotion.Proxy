using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Extensions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI;

/// <summary>
/// The pieces a HomeBlaze MCP tool provider is built from, handed to satellite packages so they can
/// contribute tools without this package having to reference them.
/// </summary>
public readonly record struct McpToolProviderContext(
    IServiceProvider Services,
    Func<IInterceptorSubject> RootSubjectProvider,
    PathProviderBase PathProvider,
    bool IsReadOnly);

/// <summary>
/// Extension methods for registering HomeBlaze MCP tools with the MCP server builder.
/// </summary>
public static class McpBuilderExtensions
{
    /// <summary>
    /// Registers HomeBlaze-specific MCP tools including subject enrichment, type discovery,
    /// and method invocation. Configuration is resolved lazily from the service provider.
    /// </summary>
    /// <param name="builder">The MCP server builder.</param>
    /// <param name="isReadOnly">Whether operations are blocked and only queries are allowed.</param>
    /// <param name="additionalToolProviders">
    /// Tools contributed by satellite packages, for example the history package's
    /// get_property_history. They are built inside the configuration factory because that is where the
    /// path provider and root subject exist, and taking them as a parameter is what lets this package
    /// stay independent of whatever happens to be installed.
    /// </param>
    public static IMcpServerBuilder WithHomeBlazeMcpTools(
        this IMcpServerBuilder builder,
        bool isReadOnly = true,
        params Func<McpToolProviderContext, IMcpToolProvider>[] additionalToolProviders)
    {
        return builder.WithSubjectRegistryTools(
            GetLoadedRoot,
            sp =>
            {
                var typeRegistry = sp.GetRequiredService<SubjectTypeRegistry>();
                var pathProvider = new StateAttributePathProvider();
                var typeProviders = new IMcpTypeProvider[]
                {
                    new SubjectAbstractionTypeProvider(),
                    new SubjectTypeRegistryTypeProvider(typeRegistry)
                };

                var excludeTypes = typeRegistry.RegisteredTypes
                    .Where(type => type.GetCustomAttributes(typeof(ExcludeFromBrowsingAttribute), true).Length > 0)
                    .ToArray();

                var rootSubjectProvider = () => GetLoadedRoot(sp);
                var configuration = new McpServerConfiguration
                {
                    PathProvider = pathProvider,
                    PathPrefix = "/",
                    ExcludeTypes = excludeTypes,
                    SubjectEnrichers = { new HomeBlazeMcpSubjectEnricher(typeProviders, isReadOnly) },
                    TypeProviders = typeProviders,
                    ToolProviders =
                    {
                        new HomeBlazeMcpToolProvider(
                            rootSubjectProvider,
                            pathProvider, sp,
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HomeBlazeMcpToolProvider>(),
                            isReadOnly)
                    },
                    IsReadOnly = isReadOnly
                };

                var context = new McpToolProviderContext(sp, rootSubjectProvider, pathProvider, isReadOnly);
                foreach (var create in additionalToolProviders)
                {
                    configuration.ToolProviders.Add(create(context));
                }

                return configuration;
            });
    }

    /// <summary>
    /// Resolves the root subject for a request. The root is loaded by a background service, so a
    /// request arriving before it exists gets a diagnosable error rather than a null reference,
    /// and a failed load surfaces its own exception.
    /// </summary>
    private static IInterceptorSubject GetLoadedRoot(IServiceProvider serviceProvider)
    {
        var rootLoaded = serviceProvider.GetRequiredService<RootManager>().RootLoaded;
        if (!rootLoaded.IsCompleted)
        {
            throw new InvalidOperationException("The root subject is still loading, try again shortly.");
        }

        return rootLoaded.GetAwaiter().GetResult();
    }
}
