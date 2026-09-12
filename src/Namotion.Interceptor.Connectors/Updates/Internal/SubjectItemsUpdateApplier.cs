using System.Collections;
using System.Text.Json;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Applies collection and dictionary updates from <see cref="SubjectUpdate"/> instances.
/// Handles structural operations (Insert, Remove, Move) and sparse property updates.
/// </summary>
internal static class SubjectItemsUpdateApplier
{
    /// <summary>
    /// Applies a collection (array/list) update to a property.
    /// </summary>
    internal static void ApplyCollectionUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var workingItems = SubjectValueConvert.ToSubjectMutableList(property.GetValue());
        var structureChanged = false;

        // Apply structural operations in two phases:
        // Phase 1: Remove and Insert operations (applied sequentially)
        // Phase 2: Move operations (applied atomically using snapshot)
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            // Phase 1: Apply Remove and Insert operations sequentially
            // Removes should be in descending order so they don't affect each other's indices
            foreach (var operation in propertyUpdate.Operations)
            {
                var index = ConvertIndexToInt(operation.Index);
                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (index >= 0 && index < workingItems.Count)
                        {
                            workingItems.RemoveAt(index);
                            structureChanged = true;
                        }
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Id is not null)
                        {
                            var itemProps = context.GetSubjectProperties(operation.Id);
                            var newItem = CreateAndApplyItem(parent, property, index, operation.Id, itemProps, context);
                            if (index >= workingItems.Count)
                                workingItems.Add(newItem);
                            else
                                workingItems.Insert(index, newItem);
                            structureChanged = true;
                        }
                        break;
                }
            }

            // Phase 2: Apply Move operations atomically using snapshot
            // Move indices reference the state after removes/inserts, and moves are applied simultaneously
            var hasMoves = propertyUpdate.Operations.Any(op => op.Action == SubjectCollectionOperationType.Move);
            if (hasMoves)
            {
                var snapshot = workingItems.ToArray();
                foreach (var operation in propertyUpdate.Operations)
                {
                    if (operation is { Action: SubjectCollectionOperationType.Move, FromIndex: not null })
                    {
                        var toIndex = ConvertIndexToInt(operation.Index);
                        var fromIndex = operation.FromIndex.Value;
                        if (fromIndex >= 0 && fromIndex < snapshot.Length && toIndex >= 0 && toIndex < workingItems.Count)
                        {
                            workingItems[toIndex] = snapshot[fromIndex];
                            structureChanged = true;
                        }
                    }
                }
            }
        }

        // Apply sparse property updates
        if (propertyUpdate.Items is { Count: > 0 })
        {
            foreach (var collectionUpdate in propertyUpdate.Items)
            {
                var index = ConvertIndexToInt(collectionUpdate.Index);

                // Validate index against declared count - if count is specified, index must be < count
                if (propertyUpdate.Count.HasValue && index >= propertyUpdate.Count.Value)
                {
                    throw new InvalidOperationException(
                        $"Invalid collection update: index {index} is out of bounds for declared count {propertyUpdate.Count.Value}. " +
                        "The index in a sparse update must be less than the declared count.");
                }

                if (collectionUpdate.Id is not null)
                {
                    var itemProps = context.GetSubjectProperties(collectionUpdate.Id);
                    if (index >= 0 && index < workingItems.Count)
                    {
                        // Update existing item
                        if (context.TryMarkAsProcessed(collectionUpdate.Id))
                        {
                            SubjectUpdateApplier.ApplyPropertyUpdates(workingItems[index], itemProps, context);
                        }
                    }
                    else if (index >= 0 && index <= workingItems.Count)
                    {
                        // Create new item at append position (for complete updates rebuilding the collection)
                        var newItem = CreateAndApplyItem(parent, property, index, collectionUpdate.Id, itemProps, context);
                        if (index >= workingItems.Count)
                            workingItems.Add(newItem);
                        else
                            workingItems[index] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var collection = context.SubjectFactory.CreateSubjectCollection(property.Type, workingItems);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, collection);
        }
    }

    /// <summary>
    /// Applies a dictionary update to a property.
    /// </summary>
    internal static void ApplyDictionaryUpdate(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var targetKeyType = SubjectFactoryExtensions.GetCollectionTypes(property.Type, dictionary: true).Key!;
        var workingDictionary = new Dictionary<object, IInterceptorSubject>();
        var structureChanged = false;

        var existingValue = property.GetValue();
        if (existingValue is not null)
        {
            foreach (DictionaryEntry entry in SubjectValueConvert.ToSubjectDictionary(existingValue))
            {
                if (entry.Value is IInterceptorSubject subject)
                    workingDictionary[entry.Key] = subject;
            }
        }

        // Apply structural operations
        if (propertyUpdate.Operations is { Count: > 0 })
        {
            foreach (var operation in propertyUpdate.Operations)
            {
                var key = ConvertDictionaryKey(operation.Index, targetKeyType);
                switch (operation.Action)
                {
                    case SubjectCollectionOperationType.Remove:
                        if (workingDictionary.Remove(key))
                            structureChanged = true;
                        break;

                    case SubjectCollectionOperationType.Insert:
                        if (operation.Id is not null)
                        {
                            var itemProps = context.GetSubjectProperties(operation.Id);
                            var newItem = CreateAndApplyItem(parent, property, key, operation.Id, itemProps, context);
                            workingDictionary[key] = newItem;
                            structureChanged = true;
                        }
                        break;
                }
            }
        }

        // Apply sparse property updates
        if (propertyUpdate.Items is { Count: > 0 })
        {
            foreach (var collUpdate in propertyUpdate.Items)
            {
                var key = ConvertDictionaryKey(collUpdate.Index, targetKeyType);

                if (collUpdate.Id is not null)
                {
                    var itemProps = context.GetSubjectProperties(collUpdate.Id);
                    if (workingDictionary.TryGetValue(key, out var existing))
                    {
                        if (context.TryMarkAsProcessed(collUpdate.Id))
                        {
                            SubjectUpdateApplier.ApplyPropertyUpdates(existing, itemProps, context);
                        }
                    }
                    else
                    {
                        var newItem = CreateAndApplyItem(parent, property, key, collUpdate.Id, itemProps, context);
                        workingDictionary[key] = newItem;
                        structureChanged = true;
                    }
                }
            }
        }

        if (structureChanged)
        {
            var dictionary = context.SubjectFactory.CreateSubjectDictionary(property.Type, workingDictionary);
            context.SetPropertyValue(property, propertyUpdate.Timestamp, dictionary);
        }
    }

    private static int ConvertIndexToInt(object index) => index switch
    {
        int i => i,
        JsonElement json => json.GetInt32(),
        _ => Convert.ToInt32(index)
    };

    private static object ConvertDictionaryKey(object key, Type targetKeyType)
        => DictionaryKeyConverter.Convert(key, targetKeyType);

    private static IInterceptorSubject CreateAndApplyItem(
        IInterceptorSubject parent,
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context)
    {
        var newItem = context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
        newItem.Context.AddFallbackContext(parent.Context);
        if (context.TryMarkAsProcessed(subjectId))
        {
            SubjectUpdateApplier.ApplyPropertyUpdates(newItem, properties, context);
        }
        return newItem;
    }
}
