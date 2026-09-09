using System.Collections;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builds collection change operations (Insert/Remove/Move) by comparing old and new collections.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class CollectionDiffBuilder
{
    // Reusable containers
    private readonly Dictionary<IInterceptorSubject, int> _oldIndexMap = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IInterceptorSubject, int> _newIndexMap = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IInterceptorSubject, int> _oldRetainedIndexMap = new(ReferenceEqualityComparer.Instance);
    private readonly List<IInterceptorSubject> _oldRetainedOrder = [];
    private readonly List<IInterceptorSubject> _newRetainedOrder = [];
    private readonly List<(object key, IInterceptorSubject item)> _retainedDictionaryItems = [];

    /// <summary>
    /// Builds collection change operations.
    /// </summary>
    /// <param name="oldItems">The old collection items.</param>
    /// <param name="newItems">The new collection items.</param>
    /// <param name="operations">Output: structural operations (Insert/Remove/Move), or null if none.</param>
    /// <param name="newItemsToProcess">Output: items that are new and need full processing, or null if none.</param>
    /// <param name="reorderedItems">Output: items that were reordered (for Move operations), or null if none.</param>
    public void GetCollectionChanges(
        IReadOnlyList<IInterceptorSubject> oldItems,
        IReadOnlyList<IInterceptorSubject> newItems,
        out List<SubjectCollectionOperation>? operations,
        out List<(int index, IInterceptorSubject item)>? newItemsToProcess,
        out List<(int oldIndex, int newIndex, IInterceptorSubject item)>? reorderedItems)
    {
        operations = null;
        newItemsToProcess = null;
        reorderedItems = null;

        // Build index maps
        _oldIndexMap.Clear();
        _newIndexMap.Clear();
        for (var i = 0; i < oldItems.Count; i++)
            _oldIndexMap[oldItems[i]] = i;
        for (var i = 0; i < newItems.Count; i++)
            _newIndexMap[newItems[i]] = i;

        // Generate Remove operations in descending order.
        // Descending order ensures each remove doesn't affect indices of subsequent removes
        // when applied sequentially.
        for (var i = oldItems.Count - 1; i >= 0; i--)
        {
            var item = oldItems[i];
            if (!_newIndexMap.ContainsKey(item))
            {
                operations ??= [];
                operations.Add(new SubjectCollectionOperation
                {
                    Action = SubjectCollectionOperationType.Remove,
                    Index = i
                });
            }
        }
        // Keep descending order - do NOT reverse

        // Generate Insert operations for new items
        for (var i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            if (!_oldIndexMap.ContainsKey(item))
            {
                newItemsToProcess ??= [];
                newItemsToProcess.Add((i, item));
            }
        }

        // Build common order lists to detect reordering
        _oldRetainedOrder.Clear();
        _newRetainedOrder.Clear();
        for (var i = 0; i < oldItems.Count; i++)
        {
            if (_newIndexMap.ContainsKey(oldItems[i]))
                _oldRetainedOrder.Add(oldItems[i]);
        }
        for (var i = 0; i < newItems.Count; i++)
        {
            if (_oldIndexMap.ContainsKey(newItems[i]))
                _newRetainedOrder.Add(newItems[i]);
        }

        // Detect reordering and compute intermediate indices for moves
        if (_oldRetainedOrder.Count > 0 && !_oldRetainedOrder.SequenceEqual(_newRetainedOrder, ReferenceEqualityComparer.Instance))
        {
            _oldRetainedIndexMap.Clear();
            for (var i = 0; i < _oldRetainedOrder.Count; i++)
                _oldRetainedIndexMap[_oldRetainedOrder[i]] = i;

            // Build set of removed indices to compute index shifts for moves
            HashSet<int>? removedIndices = null;
            if (operations is not null)
            {
                foreach (var op in operations)
                {
                    if (op.Action == SubjectCollectionOperationType.Remove)
                    {
                        removedIndices ??= [];
                        removedIndices.Add((int)op.Index);
                    }
                }
            }

            for (var i = 0; i < _newRetainedOrder.Count; i++)
            {
                var item = _newRetainedOrder[i];
                var oldCommonIndex = _oldRetainedIndexMap[item];
                if (oldCommonIndex != i)
                {
                    var originalOldIndex = _oldIndexMap[item];
                    var originalNewIndex = _newIndexMap[item];

                    // Compute intermediate fromIndex: original index minus removes before it
                    // This accounts for index shifts after removes are applied
                    var removesBeforeOldIndex = CountRemovesBefore(removedIndices, originalOldIndex);
                    var intermediateFromIndex = originalOldIndex - removesBeforeOldIndex;

                    reorderedItems ??= [];
                    reorderedItems.Add((intermediateFromIndex, originalNewIndex, item));
                }
            }
        }
    }

    /// <summary>
    /// Counts how many removed indices are less than the given index.
    /// </summary>
    private static int CountRemovesBefore(HashSet<int>? removedIndices, int index)
    {
        if (removedIndices is null)
            return 0;

        var count = 0;
        foreach (var removedIndex in removedIndices)
        {
            if (removedIndex < index)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Builds dictionary change operations. Iterates <paramref name="oldDictionary"/> and
    /// <paramref name="newDictionary"/> directly; non-subject entries are filtered inline so the
    /// caller can pass any <see cref="IDictionary"/> (including passthrough of the user's value)
    /// without a separate materialization step.
    /// </summary>
    /// <param name="oldDictionary">The old dictionary value, or null if none.</param>
    /// <param name="newDictionary">The new dictionary value.</param>
    /// <param name="operations">Output: structural operations (Insert/Remove), or null if none.</param>
    /// <param name="newItemsToProcess">Output: items that are new and need full processing, or null if none.</param>
    /// <param name="removedKeys">Output: keys that were removed, or null if none.</param>
    public void GetDictionaryChanges(
        IDictionary? oldDictionary,
        IDictionary newDictionary,
        out List<SubjectCollectionOperation>? operations,
        out List<(object key, IInterceptorSubject item)>? newItemsToProcess,
        out HashSet<object>? removedKeys)
    {
        operations = null;
        newItemsToProcess = null;
        removedKeys = null;

        // Track removed keys - start with all old subject-valued keys, remove ones that still exist
        // with the same value below.
        HashSet<object>? keysToRemove = null;
        if (oldDictionary is not null)
        {
            foreach (DictionaryEntry entry in oldDictionary)
            {
                if (entry.Value is IInterceptorSubject)
                {
                    keysToRemove ??= [];
                    keysToRemove.Add(entry.Key);
                }
            }
        }

        foreach (DictionaryEntry entry in newDictionary)
        {
            if (entry.Value is not IInterceptorSubject newItem) continue;
            var key = entry.Key;

            if (oldDictionary is not null && oldDictionary.Contains(key) &&
                oldDictionary[key] is IInterceptorSubject oldItem)
            {
                if (ReferenceEquals(oldItem, newItem))
                {
                    keysToRemove?.Remove(key);
                    _retainedDictionaryItems.Add((key, newItem));
                }
                else
                {
                    // Different object at same key - treat as Remove + Insert
                    newItemsToProcess ??= [];
                    newItemsToProcess.Add((key, newItem));
                }
            }
            else
            {
                // New key
                newItemsToProcess ??= [];
                newItemsToProcess.Add((key, newItem));
            }
        }

        // Only return removedKeys if there are actually keys to remove
        if (keysToRemove is { Count: > 0 })
        {
            removedKeys = keysToRemove;
        }
    }

    /// <summary>
    /// Gets the new index for an item.
    /// </summary>
    public int GetNewIndex(IInterceptorSubject item) =>
        _newIndexMap.GetValueOrDefault(item, -1);

    /// <summary>
    /// Gets items that exist in both old and new collections (retained by reference).
    /// </summary>
    public IReadOnlyList<IInterceptorSubject> GetRetainedItems() => _newRetainedOrder;

    /// <summary>
    /// Gets dictionary entries whose key and value (by reference) exist in both old and new.
    /// Populated by <see cref="GetDictionaryChanges"/>.
    /// </summary>
    public IReadOnlyList<(object key, IInterceptorSubject item)> GetRetainedDictionaryItems() => _retainedDictionaryItems;

    /// <summary>
    /// Clears the builder for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _oldIndexMap.Clear();
        _newIndexMap.Clear();
        _oldRetainedIndexMap.Clear();
        _oldRetainedOrder.Clear();
        _newRetainedOrder.Clear();
        _retainedDictionaryItems.Clear();
    }
}
