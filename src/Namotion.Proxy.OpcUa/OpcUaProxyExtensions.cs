﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

using Namotion.Trackable.Sources;
using Namotion.Proxy.Sources;
using Namotion.Proxy;
using Namotion.Proxy.OpcUa.Server;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class OpcUaProxyExtensions
{
    public static IServiceCollection AddOpcUaServerProxySource<TProxy>(
        this IServiceCollection serviceCollection, string sourceName, string? pathPrefix = null, string? rootName = null)
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
                    sp.GetRequiredService<ILogger<OpcUaServerTrackableSource<TProxy>>>(),
                    rootName);
            })
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<OpcUaServerTrackableSource<TProxy>>())
            .AddSingleton<IHostedService>(sp =>
            {
                var context = sp.GetRequiredService<TProxy>().Context ??
                    throw new InvalidOperationException($"Context is not set on {nameof(TProxy)}.");

                return new ProxySourceBackgroundService<TProxy>(
                    sp.GetRequiredService<OpcUaServerTrackableSource<TProxy>>(),
                    context,
                    sp.GetRequiredService<ILogger<ProxySourceBackgroundService<TProxy>>>());
            });
    }
}
