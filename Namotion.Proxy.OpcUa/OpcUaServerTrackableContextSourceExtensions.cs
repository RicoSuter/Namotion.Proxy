﻿using Microsoft.Extensions.Logging;

using Namotion.Trackable.Sources;
using Namotion.Trackable.OpcUa;
using Namotion.Proxy.Sources;
using Namotion.Proxy;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaServerTrackableContextSourceExtensions
{
    public static IServiceCollection AddOpcUaServerProxySource<TProxy>(
        this IServiceCollection serviceCollection, string sourceName, string? pathPrefix = null)
        where TProxy : IProxy
    {
        return serviceCollection
            .AddSingleton(sp =>
            {
                var context = sp.GetRequiredService<TProxy>().Context ?? 
                    throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

                var sourcePathProvider = new AttributeBasedSourcePathProvider(
                    sourceName, context, pathPrefix);

                return new OpcUaServerTrackableSource<TProxy>(
                    sp.GetRequiredService<TProxy>(),
                    sourcePathProvider,
                    sp.GetRequiredService<ILogger<OpcUaServerTrackableSource<TProxy>>>());
            })
            .AddHostedService(sp => sp.GetRequiredService<OpcUaServerTrackableSource<TProxy>>())
            .AddHostedService(sp =>
            {
                var context = sp.GetRequiredService<TProxy>().Context ??
                    throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

                return new TrackableContextSourceBackgroundService<TProxy>(
                    sp.GetRequiredService<OpcUaServerTrackableSource<TProxy>>(),
                    context,
                    sp.GetRequiredService<ILogger<TrackableContextSourceBackgroundService<TProxy>>>());
            });
    }
}
