using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Performance;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Factory for creating <see cref="SubjectUpdate"/> instances.
/// Uses a flat structure where all subjects are stored in a single dictionary
/// and referenced by string IDs, eliminating cycle detection complexity.
/// </summary>
internal static class SubjectUpdateFactory
{
    private static readonly ObjectPool<SubjectUpdateBuilder> BuilderPool = new(() => new SubjectUpdateBuilder());

    /// <summary>
    /// Creates a complete update with all properties for the given subject.
    /// </summary>
    public static SubjectUpdate CreateCompleteUpdate(
        IInterceptorSubject subject,
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(subject, processors);
            ProcessSubjectComplete(subject, builder);
            return builder.Build(subject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    /// <summary>
    /// Creates a partial update from property changes.
    /// </summary>
    public static SubjectUpdate CreatePartialUpdateFromChanges(
        IInterceptorSubject rootSubject,
        ReadOnlySpan<SubjectPropertyChange> propertyChanges,
        ISubjectUpdateProcessor[] processors)
    {
        var builder = BuilderPool.Rent();
        try
        {
            builder.Initialize(rootSubject, processors);
            propertyChanges = builder.MergeChanges(propertyChanges);

            // Merging can place a child's change before its parent assignment. Prepare complete
            // payloads first so sparse changes retain their captured values, timestamps and attributes.
            for (var i = 0; i < propertyChanges.Length; i++)
            {
                var property = propertyChanges[i].Property.TryGetRegisteredProperty();
                if (property?.IsSubjectReference == true && IsPropertyIncluded(property, processors) &&
                    propertyChanges[i].GetNewValue<IInterceptorSubject?>() is { } item)
                {
                    // Reserve the owner before traversal so a child's backreference stays sparse.
                    builder.GetOrCreateId(property.Parent.Subject);
                    if (!ReferenceEquals(item, rootSubject) && !ReferenceEquals(item, property.Parent.Subject))
                    {
                        ProcessSubjectComplete(item, builder);
                    }
                }
            }

            for (var i = 0; i < propertyChanges.Length; i++)
            {
                ProcessPropertyChange(propertyChanges[i], rootSubject, builder);
            }

            return builder.Build(rootSubject);
        }
        finally
        {
            builder.Clear();
            BuilderPool.Return(builder);
        }
    }

    internal static void ProcessSubjectComplete(
        IInterceptorSubject subject,
        SubjectUpdateBuilder builder)
    {
        var subjectId = builder.GetOrCreateId(subject);

        if (!builder.ProcessedSubjects.Add(subject))
            return;

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            builder.HasUnregisteredSubjects = true;
            return;
        }

        var properties = builder.GetOrCreateProperties(subjectId);

        foreach (var property in registeredSubject.Properties)
        {
            if (!property.HasGetter || property.IsAttribute)
                continue;

            if (!IsPropertyIncluded(property, builder.Processors))
                continue;

            var propertyUpdate = CreatePropertyUpdate(property, builder);
            properties[property.Name] = propertyUpdate;

            builder.TrackPropertyUpdate(propertyUpdate, property, properties);
        }
    }

    private static void ProcessPropertyChange(
        SubjectPropertyChange change,
        IInterceptorSubject rootSubject,
        SubjectUpdateBuilder builder)
    {
        var changedSubject = change.Property.Subject;
        var registeredProperty = change.Property.TryGetRegisteredProperty();

        if (registeredProperty is null)
            return;

        if (!IsPropertyIncluded(registeredProperty, builder.Processors))
            return;

        var subjectId = builder.GetOrCreateId(changedSubject);
        var properties = builder.GetOrCreateProperties(subjectId);

        if (registeredProperty.IsAttribute)
        {
            ProcessAttributeChange(registeredProperty, change, properties, builder);
        }
        else
        {
            // Try to get existing update (may have been created by earlier attribute changes)
            if (!properties.TryGetValue(registeredProperty.Name, out var propertyUpdate))
            {
                propertyUpdate = new SubjectPropertyUpdate();
                properties[registeredProperty.Name] = propertyUpdate;
            }

            // Update the property value in place (preserves any existing attributes)
            ApplyPropertyChangeToUpdate(propertyUpdate, registeredProperty, change, builder);
            builder.TrackPropertyUpdate(propertyUpdate, registeredProperty, properties);
        }

        BuildPathToRoot(changedSubject, rootSubject, builder);
    }

    private static SubjectPropertyUpdate CreatePropertyUpdate(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        var value = property.GetValue();
        var timestamp = property.Reference.TryGetWriteTimestamp();

        var update = new SubjectPropertyUpdate { Timestamp = timestamp };

        if (property.IsSubjectDictionary)
        {
            if (value is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildDictionaryComplete(update, value, builder);
            }
        }
        else if (property.IsSubjectCollection)
        {
            if (value is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildCollectionComplete(update, value, builder);
            }
        }
        else if (property.IsSubjectReference)
        {
            BuildObjectReference(update, value as IInterceptorSubject, builder);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = value;
        }

        update.Attributes = CreateAttributeUpdates(property, builder);

        return update;
    }

    /// <summary>
    /// Applies a property change to an existing update in place.
    /// This preserves any existing attributes on the update.
    /// </summary>
    private static void ApplyPropertyChangeToUpdate(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty property,
        SubjectPropertyChange change,
        SubjectUpdateBuilder builder)
    {
        update.Timestamp = change.ChangedTimestamp;

        if (property.IsSubjectDictionary)
        {
            var newValue = change.GetNewValue<object?>();
            if (newValue is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildDictionaryDiff(update, change.GetOldValue<object?>(),
                    newValue, builder);
            }
        }
        else if (property.IsSubjectCollection)
        {
            var newValue = change.GetNewValue<object?>();
            if (newValue is null)
            {
                update.Kind = SubjectPropertyUpdateKind.Value;
                update.Value = null;
            }
            else
            {
                SubjectItemsUpdateFactory.BuildCollectionDiff(update, change.GetOldValue<object?>(),
                    newValue, builder);
            }
        }
        else if (property.IsSubjectReference)
        {
            BuildObjectReference(update, change.GetNewValue<IInterceptorSubject?>(), builder);
        }
        else
        {
            update.Kind = SubjectPropertyUpdateKind.Value;
            update.Value = change.GetNewValue<object?>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildObjectReference(
        SubjectPropertyUpdate update,
        IInterceptorSubject? item,
        SubjectUpdateBuilder builder)
    {
        update.Kind = SubjectPropertyUpdateKind.Object;
        update.Id = null;

        if (item is not null)
        {
            var (id, isNew) = builder.GetOrCreateIdWithStatus(item);
            update.Id = id;

            // Only process the complete subject if it's newly encountered.
            // If the subject already had an ID, it's part of the existing tree
            // and we should only add a reference to it, not all its properties.
            // This prevents circular references from causing the entire tree
            // to be included in partial updates.
            if (isNew)
            {
                ProcessSubjectComplete(item, builder);
            }
        }
    }

    /// <summary>
    /// Builds the path from a changed subject up to the root subject by adding
    /// property references for each parent in the hierarchy.
    /// Only traverses the first parent (canonical registration path) in DAG structures.
    /// </summary>
    private static void BuildPathToRoot(
        IInterceptorSubject subject,
        IInterceptorSubject rootSubject,
        SubjectUpdateBuilder builder)
    {
        builder.PathVisited.Clear();
        var current = subject.TryGetRegisteredSubject();

        while (current is not null && current.Subject != rootSubject)
        {
            if (!builder.PathVisited.Add(current.Subject))
                break;

            if (current.Parents.Length == 0)
                break;

            var parentInfo = current.Parents[0];
            var parentProperty = parentInfo.Property;
            var parentSubject = parentProperty.Parent;

            var parentId = builder.GetOrCreateId(parentSubject.Subject);
            var parentProperties = builder.GetOrCreateProperties(parentId);
            var childId = builder.GetOrCreateId(current.Subject);

            if (parentInfo.Index is not null)
            {
                var kind = parentProperty.IsSubjectDictionary
                    ? SubjectPropertyUpdateKind.Dictionary
                    : SubjectPropertyUpdateKind.Collection;
            
                AddCollectionOrDictionaryItemToParent(parentProperties, parentProperty.Name, parentInfo.Index, childId, kind);
            }
            else
            {
                AddSingleReferenceToParent(parentProperties, parentProperty.Name, childId);
            }

            current = parentSubject;
        }
    }

    /// <summary>
    /// Adds a collection or dictionary item reference to the parent's property update.
    /// Appends to an existing update or creates a new one with the specified kind.
    /// </summary>
    private static void AddCollectionOrDictionaryItemToParent(
        Dictionary<string, SubjectPropertyUpdate> parentProperties,
        string propertyName,
        object index,
        string childId,
        SubjectPropertyUpdateKind kind)
    {
        if (parentProperties.TryGetValue(propertyName, out var existingUpdate))
        {
            existingUpdate.Items ??= [];

            // Skip if this subject is already referenced in Items (multiple property
            // changes on the same child each trigger BuildPathToRoot)
            for (var i = 0; i < existingUpdate.Items.Count; i++)
            {
                if (existingUpdate.Items[i].Id == childId)
                    return;
            }

            existingUpdate.Items.Add(new SubjectPropertyItemUpdate
            {
                Index = index,
                Id = childId
            });
        }
        else
        {
            parentProperties[propertyName] = new SubjectPropertyUpdate
            {
                Kind = kind,
                Items = [new SubjectPropertyItemUpdate { Index = index, Id = childId }]
            };
        }
    }

    /// <summary>
    /// Adds a single item reference to the parent's property update.
    /// Skips if the property already exists (avoids overwriting).
    /// </summary>
    private static void AddSingleReferenceToParent(
        Dictionary<string, SubjectPropertyUpdate> parentProperties,
        string propertyName,
        string childId)
    {
        if (!parentProperties.ContainsKey(propertyName))
        {
            parentProperties[propertyName] = new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Object,
                Id = childId
            };
        }
    }

    private static Dictionary<string, SubjectPropertyUpdate>? CreateAttributeUpdates(
        RegisteredSubjectProperty property,
        SubjectUpdateBuilder builder)
    {
        Dictionary<string, SubjectPropertyUpdate>? attributes = null;

        foreach (var attribute in property.Attributes)
        {
            if (!attribute.HasGetter)
                continue;

            if (!IsPropertyIncluded(attribute, builder.Processors))
                continue;

            attributes ??= new Dictionary<string, SubjectPropertyUpdate>();

            // Reuse the same property update logic for attributes
            var attributeUpdate = CreatePropertyUpdate(attribute, builder);
            attributes[attribute.AttributeMetadata.AttributeName] = attributeUpdate;
            builder.TrackPropertyUpdate(attributeUpdate, attribute, attributes);
        }

        return attributes;
    }

    private static void ProcessAttributeChange(
        RegisteredSubjectProperty attributeProperty,
        SubjectPropertyChange change,
        Dictionary<string, SubjectPropertyUpdate> subjectProperties,
        SubjectUpdateBuilder builder)
    {
        // Find the root property
        var rootProperty = attributeProperty;
        while (rootProperty.IsAttribute)
        {
            rootProperty = rootProperty.GetAttributedProperty();
        }

        if (!subjectProperties.TryGetValue(rootProperty.Name, out var rootUpdate))
        {
            rootUpdate = new SubjectPropertyUpdate();
            subjectProperties[rootProperty.Name] = rootUpdate;
        }

        // Navigate/create an attribute chain (excluding the last one which we'll create from change)
        var currentUpdate = rootUpdate;
        var attributeChain = new List<RegisteredSubjectProperty>();
        var currentProperty = attributeProperty;
        while (currentProperty.IsAttribute)
        {
            attributeChain.Add(currentProperty);
            currentProperty = currentProperty.GetAttributedProperty();
        }
        attributeChain.Reverse();

        // Navigate to parent of target attribute
        for (var i = 0; i < attributeChain.Count - 1; i++)
        {
            var chainedAttribute = attributeChain[i];
            currentUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
            var attributeName = chainedAttribute.AttributeMetadata.AttributeName;

            if (!currentUpdate.Attributes.TryGetValue(attributeName, out var nestedAttributeUpdate))
            {
                nestedAttributeUpdate = new SubjectPropertyUpdate();
                currentUpdate.Attributes[attributeName] = nestedAttributeUpdate;
            }

            currentUpdate = nestedAttributeUpdate;
        }

        // Get or create the final attribute update
        var finalAttribute = attributeChain[^1];
        currentUpdate.Attributes ??= new Dictionary<string, SubjectPropertyUpdate>();
        var finalAttributeName = finalAttribute.AttributeMetadata.AttributeName;

        if (!currentUpdate.Attributes.TryGetValue(finalAttributeName, out var attributeUpdate))
        {
            attributeUpdate = new SubjectPropertyUpdate();
            currentUpdate.Attributes[finalAttributeName] = attributeUpdate;
        }

        // Apply the change in place (preserves any existing nested attributes)
        ApplyPropertyChangeToUpdate(attributeUpdate, attributeProperty, change, builder);
        builder.TrackPropertyUpdate(attributeUpdate, attributeProperty, currentUpdate.Attributes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPropertyIncluded(
        RegisteredSubjectProperty property,
        ISubjectUpdateProcessor[] processors)
    {
        for (var i = 0; i < processors.Length; i++)
        {
            if (!processors[i].IsIncluded(property))
                return false;
        }
        return true;
    }
}
