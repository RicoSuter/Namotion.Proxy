﻿using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Sources.Abstractions;

namespace Namotion.Proxy.Sources;

public static class SourceExtensions
{
    private const string SourcePropertyNameKey = "Namotion.SourcePropertyName:";
    private const string SourcePathKey = "Namotion.SourcePath:";
    private const string SourcePathPrefixKey = "Namotion.SourcePathPrefix:";

    private const string IsChangingFromSourceKey = "Namotion.IsChangingFromSource";

    public static void SetValueFromSource(this PropertyReference property, IProxySource source, object? valueFromSource)
    {
        var contexts = property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<IProxySource>())!;
        lock (contexts)
        {
            contexts.Add(source);
        }

        try
        {
            var newValue = valueFromSource;

            var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
            if (!Equals(currentValue, newValue))
            {
                property.Metadata.SetValue?.Invoke(property.Subject, newValue);
            }
        }
        finally
        {
            lock (contexts)
            {
                contexts.Remove(source);
            }
        }
    }

    public static bool IsChangingFromSource(this ProxyPropertyChanged change, IProxySource source)
    {
        var contexts = change.Property.GetOrAddPropertyData(IsChangingFromSourceKey, () => new HashSet<IProxySource>())!;
        lock (contexts)
        {
            return contexts.Contains(source);
        }
    }

    public static string? TryGetAttributeBasedSourcePropertyName(this PropertyReference property, string sourceName)
    {
        return property.TryGetPropertyData($"{SourcePropertyNameKey}{sourceName}", out var value) ? value as string : null;
    }

    public static string? TryGetAttributeBasedSourcePath(this PropertyReference property, string sourceName, IProxyContext context)
    {
        return property.TryGetPropertyData($"{SourcePathKey}{sourceName}", out var value) ? value as string : null;
    }

    public static string? TryGetAttributeBasedSourcePathPrefix(this PropertyReference property, string sourceName)
    {
        return property.TryGetPropertyData($"{SourcePathPrefixKey}{sourceName}", out var value) ? value as string : null;
    }

    public static void SetAttributeBasedSourceProperty(this PropertyReference property, string sourceName, string sourceProperty)
    {
        property.SetPropertyData($"{SourcePropertyNameKey}{sourceName}", sourceProperty);
    }

    public static void SetAttributeBasedSourcePathPrefix(this PropertyReference property, string sourceName, string sourcePath)
    {
        property.SetPropertyData($"{SourcePathPrefixKey}{sourceName}", sourcePath);
    }

    public static void SetAttributeBasedSourcePath(this PropertyReference property, string sourceName, string sourcePath)
    {
        property.SetPropertyData($"{SourcePathKey}{sourceName}", sourcePath);
    }
}
