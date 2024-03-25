﻿using Namotion.Proxy.Registry.Abstractions;

namespace Namotion.Proxy.Sources.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class TrackableSourcePathAttribute : Attribute, IProxyPropertyInitializer
{
    public string SourceName { get; }

    public string? Path { get; }

    public string? AbsolutePath { get; set; }

    public TrackableSourcePathAttribute(string sourceName, string? path = null)
    {
        SourceName = sourceName;
        Path = path;
    }

    public void InitializeProperty(ProxyPropertyMetadata property, object? parentCollectionKey, IProxyContext context)
    {
        var prefix = property.Parent.Parents.Any() ?
            property.Parent.Parents.FirstOrDefault().TryGetAttributeBasedSourcePathPrefix(SourceName) :
            string.Empty;

        var parentPath = prefix + (parentCollectionKey != null ? $"[{parentCollectionKey}]" : string.Empty);

        var sourcePath = GetSourcePath(parentPath, property.Property);
        property.Property.SetAttributeBasedSourcePathPrefix(SourceName, sourcePath);
        property.Property.SetAttributeBasedSourceProperty(SourceName, Path ?? property.Property.Name);
    }

    private string GetSourcePath(string? basePath, ProxyPropertyReference property)
    {
        if (AbsolutePath != null)
        {
            return AbsolutePath!;
        }
        else if (Path != null)
        {
            return (!string.IsNullOrEmpty(basePath) ? basePath + "." : "") + Path;
        }

        return (!string.IsNullOrEmpty(basePath) ? basePath + "." : "") + property.Name;
    }
}