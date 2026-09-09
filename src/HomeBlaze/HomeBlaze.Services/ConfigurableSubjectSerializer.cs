using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services;

/// <summary>
/// Serializes and deserializes IConfigurable instances to/from JSON.
/// Uses "$type" discriminator for polymorphic serialization via System.Text.Json.
/// Only serializes properties marked with [Configuration].
/// Uses ActivatorUtilities for DI-aware construction during deserialization.
/// </summary>
public class ConfigurableSubjectSerializer
{
    private readonly TypeProvider _typeProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _options;

    public ConfigurableSubjectSerializer(TypeProvider typeProvider, IServiceProvider serviceProvider)
    {
        _typeProvider = typeProvider;
        _serviceProvider = serviceProvider;
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = new ConfigurationJsonTypeInfoResolver(typeProvider),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Serializes an IConfigurable to JSON with $type discriminator.
    /// </summary>
    public string Serialize(IInterceptorSubject subject)
    {
        if (subject is not IConfigurable configurableSubject)
        {
            throw new ArgumentException(
                $"Subject must implement IConfigurable. Type: {subject.GetType().FullName}",
                nameof(subject));
        }

        return JsonSerializer.Serialize(configurableSubject, typeof(IConfigurable), _options);
    }

    /// <summary>
    /// Deserializes JSON to an IConfigurable using $type discriminator.
    /// Uses ActivatorUtilities for DI-aware construction.
    /// </summary>
    public IConfigurable? Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Extract $type discriminator
        if (!root.TryGetProperty("$type", out var typeElement))
        {
            return null;
        }

        var typeName = typeElement.GetString();
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        // Find the type in registered types
        var type = _typeProvider.Types.FirstOrDefault(t => t.FullName == typeName);
        if (type == null)
        {
            return null;
        }

        using var startup = _serviceProvider.GetService<IInterceptorSubjectContext>()?.DeferHostedServiceStartup();
        // Create instance using ActivatorUtilities for DI-aware construction
        var subject = ActivatorUtilities.CreateInstance(_serviceProvider, type) as IConfigurable;
        if (subject == null)
        {
            return null;
        }

        PopulateConfigurationProperties(subject, type, root);
        startup?.Complete();
        return subject;
    }

    /// <summary>
    /// Populates [Configuration] properties on a newly created subject.
    /// Uses registry attribute lookup when available, falls back to reflection.
    /// </summary>
    private void PopulateConfigurationProperties(IConfigurable subject, Type type, JsonElement root)
    {
        if (subject is IInterceptorSubject interceptorSubject)
        {
            var registered = interceptorSubject.TryGetRegisteredSubject();
            if (registered != null)
            {
                foreach (var property in registered.Properties)
                {
                    if (property.TryGetAttribute(KnownAttributes.Configuration) == null)
                        continue;

                    var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    if (root.TryGetProperty(jsonName, out var jsonValue))
                    {
                        try
                        {
                            var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.Type, _options);
                            property.SetValue(value);
                        }
                        catch (JsonException)
                        {
                            // Skip properties that can't be deserialized
                        }
                    }
                }
                return;
            }
        }

        // Fallback to reflection for subjects without registry context
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var hasConfigAttribute = property.GetCustomAttributes(typeof(ConfigurationAttribute), true).Length > 0;
            if (!hasConfigAttribute || !property.CanWrite)
                continue;

            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                try
                {
                    var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.PropertyType, _options);
                    property.SetValue(subject, value);
                }
                catch (JsonException)
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }

    /// <summary>
    /// Updates a subject's configuration properties from JSON.
    /// Uses same options as Serialize/Deserialize for consistent property handling.
    /// Note: STJ doesn't support populating existing objects, so we deserialize each property individually.
    /// </summary>
    public void UpdateConfiguration(IInterceptorSubject subject, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        foreach (var property in subject.GetConfigurationProperties())
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (root.TryGetProperty(jsonName, out var jsonValue))
            {
                try
                {
                    // Use same _options for consistent deserialization behavior
                    var value = JsonSerializer.Deserialize(jsonValue.GetRawText(), property.Type, _options);
                    property.SetValue(value);
                }
                catch (JsonException)
                {
                    // Skip properties that can't be deserialized
                }
            }
        }
    }
}
