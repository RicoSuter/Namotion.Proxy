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
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var workingItems = SubjectValueConvert.ToSubjectMutableList(property.GetValue());
        var structureChanged = false;
        List<PendingItem>? pendingItems = null;

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
                        if (operation.Id is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                        {
                            var newItem = CreateItem(property, index, operation.Id, itemProps, context, ref pendingItems);
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

                if (collectionUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collectionUpdate.Id, out var itemProps))
                {
                    if (index >= 0 && index < workingItems.Count)
                    {
                        // Update existing item. Queued rather than applied here, because the item
                        // at this index may itself have been created by an insert in this same
                        // update and so is not in the graph until the assignment below. What makes
                        // that safe is the drain's attachment check, not this loop being unable to
                        // drop a queued item: do not rely on the latter when changing this loop.
                        QueueItem(workingItems[index], collectionUpdate.Id, itemProps, ref pendingItems);
                    }
                    else if (index >= 0 && index <= workingItems.Count)
                    {
                        // Create new item at append position (for complete updates rebuilding the collection)
                        var newItem = CreateItem(property, index, collectionUpdate.Id, itemProps, context, ref pendingItems);
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

        ApplyPendingItems(pendingItems, context);
    }

    /// <summary>
    /// Applies a dictionary update to a property.
    /// </summary>
    internal static void ApplyDictionaryUpdate(
        RegisteredSubjectProperty property,
        SubjectPropertyUpdate propertyUpdate,
        SubjectUpdateApplyContext context)
    {
        var targetKeyType = property.Type.GenericTypeArguments[0];
        var workingDictionary = new Dictionary<object, IInterceptorSubject>();
        var structureChanged = false;
        List<PendingItem>? pendingItems = null;

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
                        if (operation.Id is not null && context.Subjects.TryGetValue(operation.Id, out var itemProps))
                        {
                            var newItem = CreateItem(property, key, operation.Id, itemProps, context, ref pendingItems);
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

                if (collUpdate.Id is not null &&
                    context.Subjects.TryGetValue(collUpdate.Id, out var itemProps))
                {
                    if (workingDictionary.TryGetValue(key, out var existing))
                    {
                        // Queued for the same reason as the collection case: this key may have
                        // been filled by an insert in this same update, and as there it is the
                        // drain's attachment check that makes queueing safe.
                        QueueItem(existing, collUpdate.Id, itemProps, ref pendingItems);
                    }
                    else
                    {
                        var newItem = CreateItem(property, key, collUpdate.Id, itemProps, context, ref pendingItems);
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

        ApplyPendingItems(pendingItems, context);
    }

    private static int ConvertIndexToInt(object index) => index switch
    {
        int i => i,
        JsonElement json => json.GetInt32(),
        _ => Convert.ToInt32(index)
    };

    private static object ConvertDictionaryKey(object key, Type targetKeyType)
        => DictionaryKeyConverter.Convert(key, targetKeyType);

    private static IInterceptorSubject CreateItem(
        RegisteredSubjectProperty property,
        object indexOrKey,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> properties,
        SubjectUpdateApplyContext context,
        ref List<PendingItem>? pendingItems)
    {
        var newItem = context.SubjectFactory.CreateCollectionSubject(property, indexOrKey);
        QueueItem(newItem, subjectId, properties, ref pendingItems);
        return newItem;
    }

    /// <summary>
    /// Defers an item's population to after the assignment. Unlike a single object property, a
    /// collection is assigned once as a whole at the end, so no item it contains is guaranteed to be
    /// in the graph before then, and the population is registry-driven. Every item this update
    /// touches goes through here, so which of them are new is not a question the callers must answer.
    /// </summary>
    private static void QueueItem(
        IInterceptorSubject subject,
        string subjectId,
        Dictionary<string, SubjectPropertyUpdate> properties,
        ref List<PendingItem>? pendingItems)
    {
        (pendingItems ??= []).Add(new PendingItem(subject, subjectId, properties));
    }

    /// <summary>
    /// Populates every queued item, after the assignment that put the new ones into the graph.
    /// Deliberately outside the structural-change branch: a sparse update that only touches items
    /// already present queues them without changing the structure, so a queued item does not imply
    /// that an assignment ran.
    /// </summary>
    private static void ApplyPendingItems(List<PendingItem>? pendingItems, SubjectUpdateApplyContext context)
    {
        if (pendingItems is null)
        {
            return;
        }

        foreach (var pendingItem in pendingItems)
        {
            // This attachment check is the safety property of the whole queue rather than a
            // corner case: any item the update dropped before the assignment, by a later remove or
            // by a move overwriting its slot, stays unattached, so there is nothing to populate and
            // no id to consume. Callers therefore never have to establish that what they queued
            // survived to the assignment.
            if (pendingItem.Subject.TryGetContext() is not null &&
                context.TryMarkAsProcessed(pendingItem.SubjectId))
            {
                SubjectUpdateApplier.ApplyPropertyUpdates(pendingItem.Subject, pendingItem.Properties, context);
            }
        }
    }

    private readonly record struct PendingItem(
        IInterceptorSubject Subject,
        string SubjectId,
        Dictionary<string, SubjectPropertyUpdate> Properties);
}
