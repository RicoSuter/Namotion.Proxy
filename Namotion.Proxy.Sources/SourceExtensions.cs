﻿using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Sources.Abstractions;

namespace Namotion.Proxy.Sources;

public static class SourceExtensions
{
    private const string SourcePropertyNameKey = "SourcePropertyName:";
    private const string SourcePathKey = "SourcePath:";
    private const string SourcePathPrefixKey = "SourcePathPrefix:";

    private const string IsChangingFromSourceKey = "IsChangingFromSource";

    public static string? TryGetAttributeBasedSourceProperty(this ProxyPropertyReference property, string sourceName)
    {
        return property.Proxy.Data.TryGetValue($"{SourcePropertyNameKey}{property.PropertyName}.{sourceName}", out var value) ?
            value as string : null;
    }

    public static string? TryGetAttributeBasedSourcePath(this ProxyPropertyReference property, string sourceName, IProxyContext context)
    {
        return property.Proxy.Data.TryGetValue($"{SourcePathKey}{property.PropertyName}.{sourceName}", out var value) ?
            value as string : null;
    }

    public static string? TryGetAttributeBasedSourcePathPrefix(this ProxyPropertyReference property, string sourceName)
    {
        return property.Proxy.Data.TryGetValue($"{SourcePathPrefixKey}{property.PropertyName}.{sourceName}", out var value) ?
            value as string : null;
    }

    public static void SetAttributeBasedSourceProperty(this ProxyPropertyReference property, string sourceName, string sourceProperty)
    {
        property.Proxy.Data[$"{SourcePropertyNameKey}{property.PropertyName}.{sourceName}"] = sourceProperty;
    }

    public static void SetAttributeBasedSourcePathPrefix(this ProxyPropertyReference property, string sourceName, string sourcePath)
    {
        property.Proxy.Data[$"{SourcePathPrefixKey}{property.PropertyName}.{sourceName}"] = sourcePath;
    }

    public static void SetAttributeBasedSourcePath(this ProxyPropertyReference property, string sourceName, string sourcePath)
    {
        property.Proxy.Data[$"{SourcePathKey}{property.PropertyName}.{sourceName}"] = sourcePath;
    }

    public static void SetValueFromSource(this ProxyPropertyReference property, ITrackableSource source, object? valueFromSource)
    {
        var contexts = (HashSet<ITrackableSource>)property.Proxy.Data.GetOrAdd($"{IsChangingFromSourceKey}{property.PropertyName}", _ => new HashSet<ITrackableSource>())!;
        lock (contexts)
        {
            contexts.Add(source);
        }

        try
        {
            var newValue = valueFromSource;

            var currentValue = property.Proxy.Properties[property.PropertyName].GetValue?.Invoke(property.Proxy);
            if (!Equals(currentValue, newValue))
            {
                property.Proxy.Properties[property.PropertyName].SetValue?.Invoke(property.Proxy, newValue);
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

    public static bool IsChangingFromSource(this ProxyPropertyChanged change, ITrackableSource source)
    {
        var contexts = (HashSet<ITrackableSource>)change.Proxy.Data.GetOrAdd($"{IsChangingFromSourceKey}{change.PropertyName}", _ => new HashSet<ITrackableSource>())!;
        lock (contexts)
        {
            return contexts.Contains(source);
        }
    }
}
