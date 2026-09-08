# Subject Updates

Subject updates enable efficient synchronization of object graphs between server and clients. Instead of sending full object state on every change, only the changed properties are transmitted.

## Flat Structure

Subject updates use a **flat dictionary structure** where all subjects are stored in a single dictionary and referenced by string IDs. This design:

- Eliminates circular reference issues during serialization
- Enables O(1) subject lookup
- Makes debugging easier (all subjects visible at top level)
- Keeps each subject's data in exactly one place

### JSON Format Overview

```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Object", "id": "2" }
    },
    "2": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Object", "id": "1" }
    }
  }
}
```

- `root` - The ID of the root subject
- `subjects` - Dictionary of all subjects, keyed by string ID
- Each subject contains property name → property update mappings
- References to other subjects use `id` (not nested objects)

## Creating Updates

### Complete Update (Full State)

Use for initial synchronization when a client connects:

```csharp
var update = SubjectUpdate.CreateCompleteUpdate(rootSubject, processors);
var json = JsonSerializer.Serialize(update);
```

### Partial Update (Changes Only)

Use for incremental synchronization based on tracked property changes:

```csharp
// Collect changes from the tracking system
var changes = /* SubjectPropertyChange[] from change tracking */;

// Create update containing only changed properties
var update = SubjectUpdate.CreatePartialUpdateFromChanges(rootSubject, changes, processors);
var json = JsonSerializer.Serialize(update);
```

### Filtering with Processors

`ISubjectUpdateProcessor` controls which properties and attributes appear in updates. The `IsIncluded` method is called for each property **and each attribute** during update creation:

```csharp
public class MyProcessor : ISubjectUpdateProcessor
{
    public bool IsIncluded(RegisteredSubjectProperty property)
    {
        // Filter properties and attributes from the update
        return !property.ReflectionAttributes.OfType<MyIgnoreAttribute>().Any();
    }
}
```

Note: Dynamically added attributes (via `AddAttribute`) also expose their provided `Attribute[]` through `ReflectionAttributes`, so the same filtering logic works for both regular properties and dynamic attributes.

### Two-Layer Filtering for Connectors

Connectors that use `ChangeQueueProcessor` for real-time updates have two filtering layers:

1. **`ChangeQueueProcessor.propertyFilter`**: Pre-filters which property changes enter the queue. This prevents unnecessary buffering and partial update creation for properties that will never be sent.

2. **`ISubjectUpdateProcessor.IsIncluded`**: Filters properties and attributes during update creation (both `CreateCompleteUpdate` and `CreatePartialUpdateFromChanges`).

Both layers should apply the same filtering logic. The `propertyFilter` is an optimization for the change path; `IsIncluded` is the authoritative filter that also covers complete updates (initial sync), where no `ChangeQueueProcessor` is involved.

Connectors with an `IPathProvider` can delegate to `pathProvider.IsPropertyIncluded` in both layers to keep filtering consistent:

```csharp
// In ISubjectUpdateProcessor
public bool IsIncluded(RegisteredSubjectProperty property)
    => pathProvider.IsPropertyIncluded(property);

// In ChangeQueueProcessor setup
propertyFilter: propertyReference =>
    propertyReference.TryGetRegisteredProperty() is { } property &&
    pathProvider.IsPropertyIncluded(property)
```

## Applying Updates

### Server-Side (C#)

```csharp
// Apply update from external source (e.g., WebSocket message from client).
// A FromSource origin stamps the applied changes so echo suppression skips
// that source's own outbound path.
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.FromSource(source));

// Apply update as a local change (no source tracking).
subject.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
```

### When a Property Fails to Apply

A property that throws is skipped and the rest of the update still applies, including the properties of nested subjects, for every update kind. Applying is not all-or-nothing.

The call still reports failure once every property has been attempted. A single failure is rethrown as itself, keeping its original type and stack, so a caller catching a specific exception type is unaffected. Several failures are wrapped in an `AggregateException` whose message names the properties.

Two limits follow from applying in place rather than staging:

- A collection or dictionary item whose own property fails is still inserted, carrying a default for that property, so the parent references a new item that is only partly populated.
- A failure raised by the collection or dictionary machinery itself, such as the out-of-range index described in [Applying Collection Updates](#applying-collection-updates), is contained at that property but abandons the remaining items of it.

What a caller does with the failure is its own decision; see [Inbound Update Error Handling](connectors.md#inbound-update-error-handling) for what the built-in connector infrastructure does.

## Property Update Kinds

| Kind | Description |
|------|-------------|
| `Value` | Scalar value (string, number, boolean, etc.) |
| `Object` | Single nested subject (referenced by ID) |
| `Collection` | Index-based array or list of subjects |
| `Dictionary` | Key-based dictionary of subjects |

### Value Property

```json
{
  "kind": "Value",
  "value": "John",
  "timestamp": "2024-01-10T12:00:00Z"
}
```

### Object Property

References another subject by ID:

```json
{
  "kind": "Object",
  "id": "2"
}
```

A null reference omits the `id` field:

```json
{
  "kind": "Object"
}
```

### Collection Property

For index-based collections (arrays, lists):

```json
{
  "kind": "Collection",
  "operations": [ ... ],
  "items": [ ... ],
  "count": 5
}
```

### Dictionary Property

For key-based dictionaries. Works the same as Collection but uses string keys instead of integer indices, and does not support Move operations:

```json
{
  "kind": "Dictionary",
  "operations": [ ... ],
  "items": [ ... ],
  "count": 5
}
```

## How Collection Updates Work

Collections (arrays and dictionaries) use a **two-phase approach** that separates structural changes from property updates. This design:

- Minimizes payload size (move operations contain only indices, not full objects)
- Preserves object identity during reordering
- Enables efficient sparse updates (only changed items are transmitted)

### Phase 1: Structural Operations

Apply structural changes in two sub-phases:

**Sub-phase 1a: Remove and Insert operations** are applied sequentially in the order they appear:
- `Remove` operations are sent in **descending index order** so each remove doesn't affect subsequent removes
- `Insert` operations reference the final target position
- All `Remove` operations precede all `Insert` operations, for dictionaries and for collections alike, so replacing the value at an existing key removes the old entry before the replacement is inserted

**Sub-phase 1b: Move operations** are applied atomically using snapshot semantics:
- All moves reference the state **after** removes/inserts have been applied
- Multiple moves are applied simultaneously (each move reads from the snapshot)
- Move `fromIndex` accounts for prior removes (intermediate index, not original)

| Operation | Index semantics |
|-----------|-----------------|
| `Remove` | Original index, descending order |
| `Insert` | Final target index |
| `Move` | `fromIndex`: intermediate (after removes), `index`: final target |

**Example: Remove + Move**

Transform `[A, B, C]` → `[C, B]` (remove A, swap remaining):

```json
{
  "operations": [
    { "action": "Remove", "index": 0 },
    { "action": "Move", "fromIndex": 1, "index": 0 }
  ]
}
```

After `Remove(0)`: `[B, C]` (indices 0, 1)
After `Move(from=1, to=0)`: `[C, B]`

Note: The move's `fromIndex` is 1 (C's position after the remove), not 2 (C's original position).

### Phase 2: Property Updates

Then, apply sparse property updates. The index/key references the **final position after structural operations**.

### Example: Remove + Property Change

Before: `[A, B, C]` where C.name = "Charlie"
After: `[A, C]` where C.name = "Charles"

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Remove", "index": 1 }
  ],
  "items": [
    { "index": 1, "id": "3" }
  ],
  "count": 2
}
```

The subject with ID "3" (C) has its property update in the `subjects` dictionary:

```json
{
  "subjects": {
    "3": {
      "name": { "kind": "Value", "value": "Charles" }
    }
  }
}
```

Note: The property update uses index `1` (C's final position after B was removed).

### Example: Reorder Without Data

Before: `[A, B, C]`
After: `[C, A, B]`

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Move", "fromIndex": 2, "index": 0 }
  ],
  "count": 3
}
```

Move operations contain only indices - no item data is transmitted, keeping payloads small even for large objects.

### Example: Insert New Item

```json
{
  "kind": "Collection",
  "operations": [
    { "action": "Insert", "index": 1, "id": "5" }
  ],
  "count": 3
}
```

The new item's data is in the `subjects` dictionary:

```json
{
  "subjects": {
    "5": {
      "name": { "kind": "Value", "value": "New Item" }
    }
  }
}
```

### Complete vs Partial Collection Updates

**Partial updates** (incremental changes) have `operations` for structural changes:

```json
{
  "kind": "Collection",
  "operations": [ { "action": "Remove", "index": 1 } ],
  "items": [ { "index": 0, "id": "2" } ],
  "count": 2
}
```

**Complete updates** (initial sync) have no `operations` - just all items in `items`:

```json
{
  "kind": "Collection",
  "items": [
    { "index": 0, "id": "1" },
    { "index": 1, "id": "2" }
  ],
  "count": 2
}
```

### Applying Collection Updates

When applying sparse property updates from the `items` array, the `index` must be valid according to the declared `count`:

| Condition | Behavior |
|-----------|----------|
| `index < count` | Valid - update or create item at that position |
| `index >= count` | **Error** - throws `InvalidOperationException` |
| `count` not specified | Index validated against current collection size |

**Important:** The `count` field declares the final expected size of the collection. Any `index` in the `items` array must satisfy `index < count`. An index >= count indicates a malformed update (bug in the sender) and will throw an exception.

For complete updates (no `operations`), items at indices that don't exist locally are created sequentially. For partial updates, indices reference the final position after structural operations have been applied.

## Circular References

Circular references are handled naturally by the flat structure. Each subject appears exactly once in the `subjects` dictionary, and references use string IDs:

```json
{
  "root": "1",
  "subjects": {
    "1": {
      "name": { "kind": "Value", "value": "Parent" },
      "child": { "kind": "Object", "id": "2" }
    },
    "2": {
      "name": { "kind": "Value", "value": "Child" },
      "parent": { "kind": "Object", "id": "1" }
    }
  }
}
```

No special `reference` field is needed - the `id` field always points to a subject in the dictionary.

## Null Collections and Dictionaries

When a collection or dictionary property is set to `null`, it is represented as `Kind=Value, Value=null`, the same as any other null property value:

```json
{
  "kind": "Value",
  "value": null
}
```

Note: In partial updates, `Kind=Collection/Dictionary` entries with no operations are **path nodes**: they describe the structural parent-to-child reference so the apply side can navigate the tree, not the collection's new state. An empty collection in a complete update is represented with `count: 0` and no items.

## Limitations

- **No "clear collection" operation**: clearing N items emits N individual Remove operations.
- **Non-subject collections** (`List<int>`, `Dictionary<string, string>`) use value-replacement semantics (full replacement, no granular diffing). Only `IInterceptorSubject` collections support structural diffs.
- **Conflict resolution** is last-applied-wins by message arrival order with eventual consistency via reconnection.
- **Dictionary keys** are normalized to strings during transport. Non-string keys (int, enum) must be convertible via `Convert.ChangeType` or `Enum.Parse`.

## Attributes

Properties can have attributes (metadata) that are updated alongside values:

```json
{
  "kind": "Value",
  "value": 25.5,
  "timestamp": "2024-01-10T12:00:00Z",
  "attributes": {
    "unit": { "kind": "Value", "value": "celsius" },
    "quality": { "kind": "Value", "value": "good" }
  }
}
```
