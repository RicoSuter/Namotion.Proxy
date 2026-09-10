using System;
using System.Collections;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.AspNetCore.Extensions;

public static class SubjectRegistryJsonExtensions
{
    /// <summary>
    /// Gets the JSON path for a property, requires the lifecycle's parent tracking
    /// to be registered (WithLifecycle()) and uses optionally the registry.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="jsonSerializerOptions">The serializer options.</param>
    /// <returns>The path.</returns>
    public static string GetJsonPath(this PropertyReference property, JsonSerializerOptions jsonSerializerOptions)
    {
        var registry = property.Subject.TryGetContext()?.TryGetService<ISubjectRegistry>();
        if (registry is not null)
        {
            // TODO: avoid endless recursion
            string? path = null;
            var parent = new SubjectParent(property, null);
            do
            {
                var attribute = registry
                    .TryGetRegisteredSubject(parent.Property.Subject)?
                    .TryGetProperty(parent.Property.Name)?
                    .ReflectionAttributes
                    .OfType<PropertyAttributeAttribute>()
                    .FirstOrDefault();

                if (attribute is not null)
                {
                    return GetJsonPath(new PropertyReference(
                        parent.Property.Subject,
                        attribute.PropertyName), jsonSerializerOptions) +
                        "@" + attribute.AttributeName;
                }

                var propertyName = GetJsonPropertyName(parent.Property.Subject, parent.Property.Metadata, jsonSerializerOptions);
                path = propertyName +
                    (parent.Index is not null ? $"[{parent.Index}]" : string.Empty) +
                    (path is not null ? $".{path}" : string.Empty);

                var subjectParents = parent.Property.Subject.GetParents();
                parent = subjectParents.Length > 0 ? subjectParents[0] : default;
            }
            while (parent.Property.Subject is not null);

            return path.Trim('.');
        }
        else
        {
            // TODO: avoid endless recursion
            string? path = null;
            var parent = new SubjectParent(property, null);
            do
            {
                var attribute = parent.Property.Subject
                    .Properties[parent.Property.Name]
                    .Attributes
                    .OfType<PropertyAttributeAttribute>()
                    .FirstOrDefault();

                if (attribute is not null)
                {
                    return GetJsonPath(new PropertyReference(
                        parent.Property.Subject,
                        attribute.PropertyName), jsonSerializerOptions) +
                        "@" + attribute.AttributeName;
                }

                var propertyName = GetJsonPropertyName(parent.Property.Subject, parent.Property.Metadata, jsonSerializerOptions);
                path = propertyName +
                    (parent.Index is not null ? $"[{parent.Index}]" : string.Empty) +
                    (path is not null ? $".{path}" : string.Empty);

                var parentSubjects = parent.Property.Subject.GetParents();
                parent = parentSubjects.Length > 0 ? parentSubjects[0] : default;
            }
            while (parent.Property.Subject is not null);

            return path.Trim('.');
        }
    }

    public static JsonObject ToJsonObject(this IInterceptorSubject subject, JsonSerializerOptions jsonSerializerOptions)
    {
        var obj = new JsonObject();
        foreach (var property in subject
            .Properties
            .Where(p => p.Value.GetValue is not null))
        {
            // add static properties
            var propertyName = GetJsonPropertyName(subject, property.Value, jsonSerializerOptions);
            obj[propertyName] = ValueToJsonNode(property.Value.GetValue?.Invoke(subject), jsonSerializerOptions);
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is not null)
        {
            // add dynamic properties
            foreach (var property in registeredSubject
                .Properties
                .Where(p => p.HasGetter &&
                            subject.Properties.ContainsKey(p.Name) == false))
            {
                var propertyName = property.GetJsonPropertyName(jsonSerializerOptions);
                obj[propertyName] = ValueToJsonNode(property.GetValue(), jsonSerializerOptions);
            }
        }

        return obj;
    }

    private static JsonNode? ValueToJsonNode(object? value, JsonSerializerOptions jsonSerializerOptions)
    {
        if (value is IInterceptorSubject childProxy)
        {
            return childProxy.ToJsonObject(jsonSerializerOptions);
        }

        if (value is IEnumerable enumerable and not string)
        {
            // Materialize once so the Count check and the foreach share a single pass over
            // potentially lazy / one-shot enumerables.
            var subjectChildren = enumerable.OfType<IInterceptorSubject>().ToList();
            if (subjectChildren.Count > 0)
            {
                var children = new JsonArray();
                foreach (var item in subjectChildren)
                    children.Add(item.ToJsonObject(jsonSerializerOptions));
                return children;
            }
        }

        return JsonValue.Create(value);
    }

    private static string GetJsonPropertyName(IInterceptorSubject subject, SubjectPropertyMetadata property, JsonSerializerOptions jsonSerializerOptions)
    {
        var attribute = property
            .Attributes
            .OfType<PropertyAttributeAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            var propertyName = GetJsonPropertyName(subject, subject.Properties[attribute.PropertyName], jsonSerializerOptions);
            return $"{propertyName}@{attribute.AttributeName}";
        }

        return jsonSerializerOptions.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
    }

    private static string GetJsonPropertyName(this RegisteredSubjectProperty property, JsonSerializerOptions jsonSerializerOptions)
    {
        if (property.IsAttribute)
        {
            return property
                .GetAttributedProperty()
                .GetJsonPropertyName(jsonSerializerOptions) + "@" + property.AttributeMetadata.AttributeName;
        }

        return jsonSerializerOptions.PropertyNamingPolicy?
            .ConvertName(property.Name) ?? property.Name;
    }

    public static (IInterceptorSubject?, SubjectPropertyMetadata) FindPropertyFromJsonPath(this IInterceptorSubject subject, string path)
    {
        // TODO: Should return RegisteredSubjectProperty and use registry internally, also handle attributes
        return subject.FindPropertyFromJsonPath(path.Split('.'));
    }

    private static (IInterceptorSubject?, SubjectPropertyMetadata) FindPropertyFromJsonPath(this IInterceptorSubject subject, Span<string> segments)
    {
        while (true)
        {
            var nextSegment = segments[0];
            nextSegment = ConvertToUpperCamelCase(nextSegment);

            if (segments.Length > 1)
            {
                segments = segments[1..];
                if (nextSegment.Contains('['))
                {
                    var arraySegments = nextSegment.Split('[', ']');
                    var index = int.Parse(arraySegments[1]);
                    nextSegment = arraySegments[0];

                    if (subject.Properties.TryGetValue(nextSegment, out var property))
                    {
                        var collection = property.GetValue?.Invoke(subject) as IEnumerable;
                        var child = collection?.OfType<IInterceptorSubject>().ElementAt(index);
                        if (child is not null)
                        {
                            subject = child;
                            continue;
                        }
                    }

                    return (null, default);
                }
                else
                {
                    if (subject.Properties.TryGetValue(nextSegment, out var property) &&
                        property.GetValue?.Invoke(subject) is IInterceptorSubject child)
                    {
                        subject = child;
                        continue;
                    }

                    return (null, default);
                }
            }
            else
            {
                if (subject.Properties.TryGetValue(nextSegment, out var property))
                {
                    return (subject, property);
                }

                return (null, default);
            }
        }
    }

    private static string ConvertToUpperCamelCase(string nextSegment)
    {
        if (string.IsNullOrEmpty(nextSegment))
        {
            return nextSegment;
        }

        var characters = nextSegment.ToCharArray();
        characters[0] = char.ToUpperInvariant(characters[0]);
        return new string(characters);
    }
}
