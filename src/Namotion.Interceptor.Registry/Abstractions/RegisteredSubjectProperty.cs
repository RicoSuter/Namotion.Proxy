using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Abstractions;

#pragma warning disable CS8618, CS9264

public class RegisteredSubjectProperty
{
    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableCollectionPositions;

    [ThreadStatic]
    private static Dictionary<IInterceptorSubject, int>? _reusableChildCursors;

    private readonly List<SubjectPropertyChild> _children = [];
    private ImmutableArray<SubjectPropertyChild> _childrenCache;

    private readonly PropertyAttributeAttribute? _attributeMetadata;

    internal RegisteredSubjectProperty[]? AttributesCache = null; // TODO: Dangerous cache, needs review

    public RegisteredSubjectProperty(RegisteredSubject parent, string name,
        Type type, IReadOnlyCollection<Attribute> reflectionAttributes)
    {
        Parent = parent;
        Type = type;
        ReflectionAttributes = reflectionAttributes;
        Reference = new PropertyReference(parent.Subject, name);

        foreach (var attribute in reflectionAttributes)
        {
            if (attribute is PropertyAttributeAttribute paa)
            {
                _attributeMetadata = paa;
                break;
            }
        }
    }

    /// <summary>
    /// Gets the subject object this property belongs to.
    /// </summary>
    public IInterceptorSubject Subject => Reference.Subject;

    /// <summary>
    /// Gets the name of the property.
    /// </summary>
    public string Name => Reference.Name;

    /// <summary>
    /// Gets the parent subject which contains the property.
    /// </summary>
    public RegisteredSubject Parent { get; }
    
    /// <summary>
    /// Gets the property reference.
    /// </summary>
    public PropertyReference Reference { get; }
    
    /// <summary>
    /// Gets the type of the property.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets all .NET reflection attributes for this property, including inherited attributes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This collection includes attributes from multiple sources in the following order:
    /// </para>
    /// <list type="number">
    ///   <item>Attributes declared directly on the class property (and inherited from base classes)</item>
    ///   <item>Attributes from implemented interface properties (matched by name)</item>
    /// </list>
    /// <para>
    /// The inheritance rules mirror .NET's class inheritance behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item>If an attribute has <c>AllowMultiple=false</c> and exists on both the class
    ///         and interface, only the class attribute is included (class wins)</item>
    ///   <item>If an attribute has <c>AllowMultiple=true</c>, attributes from both class
    ///         and interfaces are included</item>
    ///   <item>Interface attributes are collected in interface declaration order</item>
    /// </list>
    /// </remarks>
    public IReadOnlyCollection<Attribute> ReflectionAttributes { get; }
    
    /// <summary>
    /// Gets the browse name of the property (either the property or attribute name).
    /// </summary>
    public string BrowseName => IsAttribute ? AttributeMetadata.AttributeName : Name;
    
    /// <summary>
    /// Specifies whether the property is an attribute property (property attached to another property).
    /// </summary>
    public bool IsAttribute => _attributeMetadata is not null;

    /// <summary>
    /// Gets the attribute with information about this attribute property.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this property is not an attribute.</exception>
    public PropertyAttributeAttribute AttributeMetadata => _attributeMetadata 
        ?? throw new InvalidOperationException("The property is not an attribute.");
    
    /// <summary>
    /// Checks whether this property has child subjects, which can be either
    /// a subject reference, a collection of subjects, or a dictionary of subjects.
    /// </summary>
    public bool CanContainSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.CanContainSubjects();
    }

    /// <summary>
    /// Gets a value indicating whether this property references another subject.
    /// </summary>
    public bool IsSubjectReference
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectReferenceType();
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subjects with a collection.
    /// </summary>
    public bool IsSubjectCollection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectCollectionType();
    }

    /// <summary>
    /// Gets a value indicating whether this property references multiple subjects with a dictionary.
    /// </summary>
    public bool IsSubjectDictionary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type.IsSubjectDictionaryType();
    }

    /// <summary>
    /// Gets a value indicating whether the property has a getter.
    /// </summary>
    public bool HasGetter => Reference.Metadata.GetValue is not null;

    /// <summary>
    /// Gets a value indicating whether the property has a setter.
    /// </summary>
    public bool HasSetter => Reference.Metadata.SetValue is not null;

    /// <summary>
    /// Gets the current value of the property.
    /// </summary>
    /// <returns>The value.</returns>
    public object? GetValue()
    {
        return Reference.Metadata.GetValue?.Invoke(Subject);
    }
    
    /// <summary>
    /// Sets the value of the property.
    /// </summary>
    /// <param name="value">The value.</param>
    public void SetValue(object? value)
    {
        Reference.Metadata.SetValue?.Invoke(Subject, value);
    }

    /// <summary>
    /// Gets the collection or dictionary items of the property.
    /// Thread-safe: Lock on private readonly List ensures thread-safe access.
    /// Performance: Returns cached ImmutableArray - only rebuilds when invalidated.
    /// </summary>
    public ImmutableArray<SubjectPropertyChild> Children
    {
        get
        {
            lock (_children)
            {
                if (_childrenCache.IsDefault)
                {
                    _childrenCache = [.. _children];
                }

                return _childrenCache;
            }
        }
    }
    
    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, TProperty?>? getValue,
        Action<IInterceptorSubject, TProperty?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty), 
            getValue is not null ? x => (TProperty)getValue(x)! : null, 
            setValue is not null ? (x, y) => setValue(x, (TProperty)y!) : null, 
            attributes);
    }

    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddAttribute<TProperty>(
        string name,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?>? setValue = null,
        params Attribute[] attributes)
    {
        return AddAttribute(name, typeof(TProperty), getValue, setValue, attributes);
    }

    /// <summary>
    /// Adds an attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="type">The type of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddAttribute(
        string name, Type type, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        params Attribute[] attributes)
    {
        var propertyName = $"{Name}@{name}";
        
        var attribute = Parent.AddProperty(
            propertyName,
            type, getValue, setValue,
            attributes
                .Concat([new PropertyAttributeAttribute(Name, name)])
                .ToArray());

        return attribute;
    }

    /// <summary>
    /// Adds a derived attribute to the property.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="type">The type of the attribute.</param>
    /// <param name="getValue">The value getter function.</param>
    /// <param name="setValue">The value setter action.</param>
    /// <param name="attributes">The .NET reflection attributes of the attribute.</param>
    /// <returns>The created attribute property.</returns>
    public RegisteredSubjectProperty AddDerivedAttribute(
        string name, Type type, 
        Func<IInterceptorSubject, object?>? getValue, 
        Action<IInterceptorSubject, object?>? setValue, 
        params Attribute[] attributes)
    {
        var propertyName = $"{Name}@{name}";
        
        var attribute = Parent.AddDerivedProperty(
            propertyName,
            type, getValue, setValue,
            attributes
                .Concat([new PropertyAttributeAttribute(Name, name)])
                .ToArray());

        return attribute;
    }

    /// <summary>
    /// Gets all attributes which are attached to this property.
    /// </summary>
    public RegisteredSubjectProperty[] Attributes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => AttributesCache = (AttributesCache ?? Parent.GetPropertyAttributes(Name).ToArray());
    }
    
    /// <summary>
    /// Gets a property attribute by name.
    /// </summary>
    /// <param name="attributeName">The attribute name to find.</param>
    /// <returns>The attribute property.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty? TryGetAttribute(string attributeName)
    {
        return Parent.TryGetPropertyAttribute(Name, attributeName);
    } 

    /// <summary>
    /// Gets the attribute property this property is attached to.
    /// </summary>
    /// <returns>The property.</returns>
    /// <exception cref="InvalidOperationException">Thrown when this property is not an attribute.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the property this attribute is attached could not be found.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubjectProperty GetAttributedProperty()
    {
        return Parent.TryGetProperty(AttributeMetadata.PropertyName) ??
            throw new InvalidOperationException($"The attributed property '{AttributeMetadata.PropertyName}' could not be found on the parent subject.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator PropertyReference(RegisteredSubjectProperty property)
    {
        return property.Reference;
    }

    internal void ClearChildren()
    {
        lock (_children)
        {
            _children.Clear();
            _childrenCache = default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            // No Contains check needed - LifecycleInterceptor already guarantees
            // no duplicates via HashSet<PropertyReference?> in _attachedSubjects
            _children.Add(child);
            _childrenCache = default;
        }
    }

    internal void RemoveChild(SubjectPropertyChild child)
    {
        lock (_children)
        {
            var index = -1;
            if (IsSubjectCollection)
            {
                // For collections, match by Subject only. The Index field represents
                // the collection position which shifts as items are removed.
                // Search backwards because LifecycleInterceptor detaches in reverse
                // collection order, making each lookup O(1) instead of O(n).
                var subject = child.Subject;
                for (var i = _children.Count - 1; i >= 0; i--)
                {
                    if (_children[i].Subject == subject)
                    {
                        index = i;
                        break;
                    }
                }
            }
            else
            {
                index = _children.IndexOf(child);
            }

            if (index == -1)
                return;

            _children.RemoveAt(index);
            _childrenCache = default;
        }
    }

    /// <summary>
    /// Syncs children's indices and parent entries with the live collection.
    /// Must be called while LifecycleInterceptor's _attachedSubjects lock is held,
    /// because this method acquires _children then _knownSubjects, which is the inverse of
    /// HandleLifecycleChange's lock order. The outer _attachedSubjects lock serializes
    /// both paths and prevents deadlock.
    /// </summary>
    /// <param name="collectionValue">The current collection value (passed from caller to avoid re-reading through interceptors).</param>
    /// <param name="registry">The subject registry (passed from caller to avoid repeated service resolution per child).</param>
    internal void RefreshCollectionIndices(object? collectionValue, ISubjectRegistry registry)
    {
        // Anything that is not an ordinal collection is offered to the keyed refresh, which no-ops
        // unless the value really carries keyed entries. That covers a dictionary reaching an
        // object-declared property, where the declared type reveals nothing about its shape.
        if (!IsSubjectCollection)
        {
            RefreshKeyedIndices(collectionValue, registry);
            return;
        }

        lock (_children)
        {
            var collectionPositions = BuildCollectionPositions(collectionValue, _children.Count);
            if (collectionPositions is null)
                return;

            for (var i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                if (!collectionPositions.TryGetValue(child.Subject, out var newIndex))
                    continue;

                // Compare unboxed to avoid allocating a boxed int when index hasn't changed
                if (child.Index is int oldIndex && oldIndex == newIndex)
                    continue;

                var boxedNewIndex = (object)newIndex;
                _children[i] = child with { Index = boxedNewIndex };

                // child is a readonly record struct snapshot from before the update above,
                // so child.Index still holds the old value, which is correct for the oldIndex parameter.
                registry.TryGetRegisteredSubject(child.Subject)?.UpdateParentIndex(this, child.Index, boxedNewIndex);
            }

            // Sort children to match live collection order
            _children.Sort(static (a, b) => ((int)a.Index!).CompareTo((int)b.Index!));
            _childrenCache = default;

            // Release references so subjects can be GC'd on idle threads
            collectionPositions.Clear();
        }
    }

    /// <summary>
    /// Syncs children's keys and parent entries with the live keyed value. A retained child whose key
    /// moved keeps its registration, so nothing else republishes its index: the lifecycle rewrites the
    /// ownership edge and this rewrites the projection of it.
    /// </summary>
    /// <remarks>
    /// Occurrences of one subject are interchangeable, so the entries are paired with the child slots
    /// of the same subject in enumeration order, and the cursor makes that pairing one pass rather
    /// than a rescan per entry. Locking matches the collection path: <c>_children</c> then
    /// <c>_knownSubjects</c> through the registry, which the lifecycle's outer topology lock
    /// serializes.
    /// </remarks>
    private void RefreshKeyedIndices(object? keyedValue, ISubjectRegistry registry)
    {
        if (keyedValue is null)
            return;

        lock (_children)
        {
            if (_children.Count == 0)
                return;

            var cursors = _reusableChildCursors ??= new Dictionary<IInterceptorSubject, int>(ReferenceEqualityComparer.Instance);
            cursors.Clear();

            if (keyedValue is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subject)
                    {
                        MoveChildToKey(cursors, subject, entry.Key, registry);
                    }
                }
            }
            else if (keyedValue is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is not null && SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject))
                    {
                        MoveChildToKey(cursors, subject, key, registry);
                    }
                }
            }

            // Release references so subjects can be GC'd on idle threads
            cursors.Clear();
        }
    }

    /// <summary>
    /// Points the subject's next unpaired child slot at the key, if that slot names a different one.
    /// Caller must hold <c>_children</c>.
    /// </summary>
    private void MoveChildToKey(Dictionary<IInterceptorSubject, int> cursors, IInterceptorSubject subject, object? key, ISubjectRegistry registry)
    {
        for (var i = cursors.GetValueOrDefault(subject); i < _children.Count; i++)
        {
            var child = _children[i];
            if (!ReferenceEquals(child.Subject, subject))
                continue;

            cursors[subject] = i + 1;
            if (!Equals(child.Index, key))
            {
                _children[i] = child with { Index = key };
                _childrenCache = default;

                // child is a snapshot from before the update, so its Index is still the old key.
                registry.TryGetRegisteredSubject(subject)?.UpdateParentIndex(this, child.Index, key);
            }

            return;
        }
    }

    /// <summary>
    /// Maps each subject in the collection to its current position.
    /// Uses IList indexed access when available; falls back to ICollection foreach,
    /// then IEnumerable for read-only types that implement neither.
    /// Reuses a ThreadStatic dictionary to avoid allocations.
    /// </summary>
    /// <remarks>
    /// Keyed by reference: a collection slot is a position of one instance, and a hand-written
    /// subject overriding Equals/GetHashCode would otherwise merge two equal siblings into one
    /// entry and give both of them the last one's position.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<IInterceptorSubject, int>? BuildCollectionPositions(object? value, int capacityHint)
    {
        if (value is null)
            return null;

        var collectionPositions = _reusableCollectionPositions;
        collectionPositions?.Clear();

        if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index] is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint, ReferenceEqualityComparer.Instance);
                    collectionPositions[subject] = index;
                }
            }
        }
        else if (value is ICollection collection)
        {
            var index = 0;
            foreach (var item in collection)
            {
                if (item is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint, ReferenceEqualityComparer.Instance);
                    collectionPositions[subject] = index;
                }
                index++;
            }
        }
        else if (value is IEnumerable enumerable and not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item is IInterceptorSubject subject)
                {
                    collectionPositions ??= _reusableCollectionPositions = new Dictionary<IInterceptorSubject, int>(capacityHint, ReferenceEqualityComparer.Instance);
                    collectionPositions[subject] = index;
                }
                index++;
            }
        }

        return collectionPositions;
    }
}
