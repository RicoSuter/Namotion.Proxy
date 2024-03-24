﻿using Namotion.Proxy;

namespace Namotion.Trackable.Sources;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class TrackableSourcePathAttribute : Attribute
{
    public string SourceName { get; }

    public string? Path { get; }

    public string? AbsolutePath { get; set; }

    public TrackableSourcePathAttribute(string sourceName, string? path = null)
    {
        SourceName = sourceName;
        Path = path;
    }

    //public void InitializeProperty(TrackedProperty property, object? parentCollectionKey, ITrackableContext context)
    //{
    //    var parentPath = property.Parent.ParentProperty?.TryGetAttributeBasedSourcePathPrefix(SourceName) +
    //        (parentCollectionKey != null ? $"[{parentCollectionKey}]" : string.Empty);

    //    var sourcePath = GetSourcePath(parentPath, property);
    //    property.SetAttributeBasedSourcePathPrefix(SourceName, sourcePath);
    //    property.SetAttributeBasedSourceProperty(SourceName, Path ?? property.Name);
    //}

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

        return (!string.IsNullOrEmpty(basePath) ? basePath + "." : "") + property.PropertyName;
    }
}