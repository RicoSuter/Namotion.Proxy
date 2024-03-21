﻿using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;
using Namotion.Proxy.Lifecycle;
using Namotion.Proxy.Registry;

namespace Namotion.Proxy;

public static class ProxyContextBuilderExtensions
{
    public static IProxyContextBuilder WithFullPropertyTracking(this IProxyContextBuilder builder)
    {
        return builder
            .WithEqualityCheck()
            .WithRegistry()
            .WithParents()
            .WithDerivedPropertyChangeDetection();
    }

    public static IProxyContextBuilder WithEqualityCheck(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new PropertyValueEqualityCheckHandler());
    }

    public static IProxyContextBuilder WithDerivedPropertyChangeDetection(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new InitiallyLoadDerivedPropertiesHandler())
            .TryAddSingleHandler(new DerivedPropertyChangeDetectionHandler())
            .WithPropertyChangedHandlers();
    }

    public static IProxyContextBuilder WithPropertyChangeRecorder(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new PropertyChangeRecorder());
    }

    public static IProxyContextBuilder WithPropertyChangedCallback(this IProxyContextBuilder builder, Action<ProxyChangedHandlerContext> callback)
    {
        return builder
            .AddHandler(new PropertyChangedCallbackHandler(callback));
    }

    /// <summary>
    /// Adds support for <see cref="IProxyChangedHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder WithPropertyChangedHandlers(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new PropertyChangedHandler());
    }

    /// <summary>
    /// 
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder WithRegistry(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new ProxyRegistry())
            .WithAutomaticContextAssignment();
    }

    /// <summary>
    /// Adds  automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder WithAutomaticContextAssignment(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new AutomaticContextAssignmentHandler())
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder WithProxyLifecycle(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new ProxyLifecycleHandler());
    }


    public static IProxyContextBuilder WithParents(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new ParentsHandler())
            .WithRegistry();
    }
}