using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Ads.Mapping;

/// <summary>
/// Builds a code-based <see cref="AdsFluentMapper"/> by configuring members per type. Compose the result into
/// an <see cref="AdsCompositeMapper"/> after the default mappers so fluent wins on overlap.
/// </summary>
public sealed class AdsFluentMapperBuilder<TRoot>
    where TRoot : IInterceptorSubject
{
    internal FluentMappingRegistry<AdsPropertyMapping> Registry { get; } = new();

    /// <summary>Begins configuring members of <typeparamref name="T"/>.</summary>
    public AdsFluentTypeBuilder<TRoot, T> ForType<T>() => new(this);

    /// <summary>Builds the fluent mapper from the configured registrations.</summary>
    public AdsFluentMapper Build(char pathSeparator = '.') => new(Registry, pathSeparator);
}

/// <summary>Type-scoped ADS fluent builder; chains within a type, into another type, and into <see cref="Build"/>.</summary>
public sealed class AdsFluentTypeBuilder<TRoot, TSubject>
    where TRoot : IInterceptorSubject
{
    private readonly AdsFluentMapperBuilder<TRoot> _owner;

    internal AdsFluentTypeBuilder(AdsFluentMapperBuilder<TRoot> owner) => _owner = owner;

    /// <summary>Configures a single member of <typeparamref name="TSubject"/>.</summary>
    public AdsFluentTypeBuilder<TRoot, TSubject> Map<TValue>(
        Expression<Func<TSubject, TValue>> selector,
        Action<AdsFluentPropertyBuilder> configure)
    {
        var member = ExpressionPathHelper.GetSingleMemberName(selector.Body);
        var builder = new AdsFluentPropertyBuilder();
        configure(builder);
        var (segment, metadata) = builder.Build();
        _owner.Registry.AddPropertyMetadata(typeof(TSubject), member, segment, metadata);
        return this;
    }

    /// <summary>Switches configuration to another type.</summary>
    public AdsFluentTypeBuilder<TRoot, TOther> ForType<TOther>() => _owner.ForType<TOther>();

    /// <summary>Builds the fluent mapper (delegates to the owner).</summary>
    public AdsFluentMapper Build(char pathSeparator = '.') => _owner.Build(pathSeparator);
}
