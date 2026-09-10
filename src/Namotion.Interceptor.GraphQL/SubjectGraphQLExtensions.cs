using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;
using Namotion.Interceptor.Tracking;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    /// <summary>
    /// Adds GraphQL support for the specified subject type using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support for the specified subject type with a custom root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL(
            subjectSelector,
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        string rootName)
        where TSubject : class, IInterceptorSubject
    {
        return builder.AddSubjectGraphQL(
            subjectSelector,
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and full configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : class, IInterceptorSubject
    {
        var key = Guid.NewGuid().ToString();

        builder.Services
            .AddKeyedSingleton(key, (sp, _) =>
            {
                var configuration = configurationProvider(sp);
                configuration.Validate();
                return configuration;
            })
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp));

        // Resolve configuration lazily from app services during schema building.
        // We use OnConfigureSchemaServicesHooks via IConfigureOptions<RequestExecutorSetup>
        // to capture the resolved configuration before descriptor configuration runs.
        // This avoids calling configurationProvider(null!) which would fail when the
        // provider depends on IServiceProvider.
        // TODO: Replace with builder.ConfigureSchemaServices() when upgrading to HotChocolate v16.
        GraphQLSubjectConfiguration? resolvedConfiguration = null;

        builder.Services.AddTransient<IConfigureOptions<RequestExecutorSetup>>(
            _ => new ConfigureNamedOptions<RequestExecutorSetup>(
                builder.Name,
                setup =>
                {
                    setup.OnConfigureSchemaServicesHooks.Add((context, _) =>
                    {
                        Volatile.Write(
                            ref resolvedConfiguration,
                            context.ApplicationServices
                                .GetRequiredKeyedService<GraphQLSubjectConfiguration>(key));
                    });
                }));

        builder
            .AddQueryType(d =>
            {
                var configuration = Volatile.Read(ref resolvedConfiguration)
                    ?? throw new InvalidOperationException(
                        "GraphQL subject configuration was not resolved. " +
                        "Ensure the schema services hook has run before descriptor configuration.");
                d.Name("Query")
                    .Field(configuration.RootName)
                    .Resolve(ctx => ctx.Services.GetRequiredKeyedService<TSubject>(key));
            })
            .AddSubscriptionType(d =>
            {
                var configuration = Volatile.Read(ref resolvedConfiguration)
                    ?? throw new InvalidOperationException(
                        "GraphQL subject configuration was not resolved. " +
                        "Ensure the schema services hook has run before descriptor configuration.");
                d.Name("Subscription")
                    .Field(configuration.RootName)
                    .Type<ObjectType<TSubject>>()
                    .Subscribe(ctx =>
                    {
                        var subject = ctx.Services.GetRequiredKeyedService<TSubject>(key);
                        var runtimeConfiguration = ctx.Services.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key);
                        var logger = ctx.Services.GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(SubjectGraphQLExtensions));

                        var selectedPaths = ExtractSelectionPaths(ctx, logger);

                        return CreateFilteredStream(subject, runtimeConfiguration, selectedPaths, ctx.RequestAborted);
                    })
                    .Resolve(ctx => ctx.GetEventMessage<TSubject>());
            });

        return builder;
    }

    private static IReadOnlySet<string> ExtractSelectionPaths(
        HotChocolate.Resolvers.IResolverContext context,
        ILogger? logger)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var hasUnsupportedSelections = false;

        var selections = context.Selection.SelectionSet?.Selections;
        if (selections != null)
        {
            ExtractPathsRecursive(selections, "", paths, ref hasUnsupportedSelections);
        }

        if (hasUnsupportedSelections)
        {
            logger?.LogWarning(
                "GraphQL subscription contains unsupported selection nodes (fragments). " +
                "Selection-aware filtering is disabled for this subscription; all property changes will be sent. " +
                "See https://github.com/RicoSuter/Namotion.Interceptor/issues/206");
            paths.Clear();
        }

        return paths;
    }

    private static void ExtractPathsRecursive(
        IReadOnlyList<HotChocolate.Language.ISelectionNode> selections,
        string prefix,
        HashSet<string> paths,
        ref bool hasUnsupportedSelections)
    {
        foreach (var selection in selections)
        {
            if (selection is HotChocolate.Language.FieldNode field)
            {
                var fieldName = field.Name.Value;
                var path = string.IsNullOrEmpty(prefix) ? fieldName : $"{prefix}.{fieldName}";
                paths.Add(path);

                // Recurse into nested selections
                if (field.SelectionSet?.Selections != null)
                {
                    ExtractPathsRecursive(field.SelectionSet.Selections, path, paths, ref hasUnsupportedSelections);
                }
            }
            else
            {
                hasUnsupportedSelections = true;
            }
        }
    }

    private static async IAsyncEnumerable<TSubject> CreateFilteredStream<TSubject>(
        TSubject subject,
        GraphQLSubjectConfiguration configuration,
        IReadOnlySet<string> selectedPaths,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TSubject : class, IInterceptorSubject
    {
        var observable = subject.Context.GetPropertyChangeObservable();

        // Filter changes by selection and buffer them
        var buffered = observable
            .Where(change =>
                selectedPaths.Count == 0 ||
                GraphQLSelectionMatcher.IsPropertyInSelection(
                    change, selectedPaths, configuration.PathProvider, subject))
            .Buffer(configuration.BufferTime)
            .Where(batch => batch.Count > 0);

        await foreach (var _ in buffered.ToAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return subject;
        }
    }
}
