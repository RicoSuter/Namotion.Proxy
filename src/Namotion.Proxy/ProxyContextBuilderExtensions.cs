﻿using Microsoft.Extensions.DependencyInjection;
using Namotion.Interception.Lifecycle;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interception.Lifecycle.Handlers;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Registry;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy;

public static class ProxyContextBuilderExtensions
{
    public static IProxyContextBuilder WithFullPropertyTracking(this IProxyContextBuilder builder)
    {
        return builder
            .WithEqualityCheck()
            .WithAutomaticContextAssignment()
            .WithDerivedPropertyChangeDetection();
    }

    public static IProxyContextBuilder WithEqualityCheck(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddInterceptor(_ => new PropertyValueEqualityCheckHandler());
    }

    public static IProxyContextBuilder WithDerivedPropertyChangeDetection(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddInterceptor(context => new DerivedPropertyChangeDetectionHandler(context))
            .TryAddSingleton<IProxyLifecycleHandler, DerivedPropertyChangeDetectionHandler>(context => 
                context.GetRequiredService<DerivedPropertyChangeDetectionHandler>())
            .WithPropertyChangedObservable();
    }

    public static IProxyContextBuilder WithReadPropertyRecorder(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddInterceptor(context => new ReadPropertyRecorder(context));
    }

    /// <summary>
    /// Registers support for <see cref="IProxyPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithPropertyValidation(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddInterceptor(context => new ValidationInterceptor(context.GetServices<IProxyPropertyValidator>()));
    }

    /// <summary>
    /// Adds support for data annotations on the interceptable properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithDataAnnotationValidation(this IProxyContextBuilder builder)
    {
        builder
            .WithPropertyValidation()
            .TryAddSingleton<IProxyPropertyValidator, DataAnnotationsValidator>(_ => new DataAnnotationsValidator());

        return builder;
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using interceptable.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithPropertyChangedObservable(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddInterceptor(context => new PropertyChangedObservable(context));
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithAutomaticContextAssignment(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleton<IProxyLifecycleHandler, InterceptorCollectionAssignmentHandler>(context => new InterceptorCollectionAssignmentHandler(context))
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithProxyLifecycle(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddInterceptor(context => new LifecycleInterceptor(context.GetServices<IProxyLifecycleHandler>()));
    }

    /// <summary>
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithRegistry(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleton<IProxyRegistry, ProxyRegistry>(context => new ProxyRegistry(context))
            .TryAddSingleton<IProxyLifecycleHandler, ProxyRegistry>(context => (ProxyRegistry)context.GetRequiredService<IProxyRegistry>())
            .WithAutomaticContextAssignment();
    }

    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithParents(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleton<IProxyLifecycleHandler, ParentTrackingHandler>(context => new ParentTrackingHandler())
            .WithProxyLifecycle();
    }
}