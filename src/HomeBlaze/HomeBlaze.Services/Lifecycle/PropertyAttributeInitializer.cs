using System.Linq;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Lifecycle handler that registers State and Configuration registry attributes
/// from [State] and [Configuration] C# attributes on first attach.
/// </summary>
public class PropertyAttributeInitializer : IPropertyLifecycleHandler
{
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = change.Subject.TryGetRegisteredProperty(change.Property.Name);
        if (property is not null)
        {
            InitializePropertyAttributes(property);
        }
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
    }

    private static void InitializePropertyAttributes(RegisteredSubjectProperty property)
    {
        var hasState = false;
        ConfigurationAttribute? configurationAttribute = null;

        // Merge fields: first explicitly-set value wins (class attributes come first)
        string? title = null;
        StateUnit? unit = null;
        int? position = null;
        bool? isCumulative = null;
        bool? isDiscrete = null;
        bool? isEstimated = null;

        foreach (var reflectionAttribute in property.ReflectionAttributes)
        {
            if (reflectionAttribute is StateAttribute stateAttribute)
            {
                hasState = true;

                title ??= stateAttribute.Title;

                if (unit == null && stateAttribute.IsUnitSet)
                    unit = stateAttribute.Unit;

                if (position == null && stateAttribute.IsPositionSet)
                    position = stateAttribute.Position;

                if (isCumulative == null && stateAttribute.IsCumulativeSet)
                    isCumulative = stateAttribute.IsCumulative;

                if (isDiscrete == null && stateAttribute.IsDiscreteSet)
                    isDiscrete = stateAttribute.IsDiscrete;

                if (isEstimated == null && stateAttribute.IsEstimatedSet)
                    isEstimated = stateAttribute.IsEstimated;
            }
            else if (reflectionAttribute is ConfigurationAttribute configAttribute)
            {
                configurationAttribute = configAttribute;
                if (property.TryGetAttribute(KnownAttributes.Configuration) is null)
                {
                    var metadata = new ConfigurationMetadata
                    {
                        IsSecret = configAttribute.IsSecret
                    };
                    property.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata),
                        _ => metadata, null);
                }
            }
        }

        // Fail closed on the [State] + secret [Configuration] conflict: a secret must never be
        // exposed as state, so skip the State registration and leave the property usable as secret
        // configuration. This replaces a throw that aborted attach and, from the hosted root loader,
        // could stop the whole host (see #384 for making lifecycle handler failures recoverable).
        // The warning is best-effort (only if host logging was made available to the context, see
        // RootManager) and wrapped, so a disposed or faulting logging provider cannot re-abort attach.
        if (hasState && configurationAttribute is { IsSecret: true })
        {
            var subject = property.Parent.Subject;
            try
            {
                subject.GetContext().GetServices<ILoggerFactory>().FirstOrDefault()?
                    .CreateLogger(typeof(PropertyAttributeInitializer).FullName!)
                    .LogWarning(
                        "Property '{Property}' on '{Subject}' has both [State] and [Configuration(IsSecret = true)]; " +
                        "the [State] attribute is ignored because secret configuration must not be exposed as state.",
                        property.Name, subject.GetType().Name);
            }
            catch
            {
                // Logging is diagnostics only; never let it reintroduce the fatal attach failure.
            }

            return;
        }

        if (hasState && property.TryGetAttribute(KnownAttributes.State) is null)
        {
            var stateMetadata = new StateMetadata
            {
                Title = title,
                Unit = unit ?? StateUnit.Default,
                Position = position ?? int.MaxValue,
                IsCumulative = isCumulative ?? false,
                IsDiscrete = isDiscrete ?? false,
                IsEstimated = isEstimated ?? false,
            };
            property.AddAttribute(KnownAttributes.State, typeof(StateMetadata),
                _ => stateMetadata, null);
        }
    }
}
