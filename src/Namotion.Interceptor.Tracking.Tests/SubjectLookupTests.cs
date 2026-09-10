using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class SubjectLookupTests
{
    [Fact]
    public void WhenValueIsList_ThenReturnsSubjectAtIndex()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var list = new List<Person> { new() { FirstName = "Bob" }, person, new() { FirstName = "Carol" } };

        // Act
        var result = SubjectLookup.FindSubjectInCollection(list, 1);

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenValueIsArray_ThenReturnsSubjectAtIndex()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        Person[] array = [new() { FirstName = "Bob" }, person];

        // Act
        var result = SubjectLookup.FindSubjectInCollection(array, 1);

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenValueIsNonListEnumerable_ThenReturnsSubjectAtIndex()
    {
        // Arrange
        var person = new Person { FirstName = "Target" };
        var items = ImmutableQueue.Create<Person>(
            new Person { FirstName = "First" },
            person,
            new Person { FirstName = "Third" });

        // Act
        var result = SubjectLookup.FindSubjectInCollection(items, 1);

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenListElementIsNotSubject_ThenReturnsNull()
    {
        // Arrange
        var list = new List<object> { "hello", 42 };

        // Act
        var result = SubjectLookup.FindSubjectInCollection(list, 0);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenIndexExceedsEnumerableCount_ThenReturnsNull()
    {
        // Arrange
        var items = ImmutableQueue.Create<Person>(new Person { FirstName = "Only" });

        // Act
        var result = SubjectLookup.FindSubjectInCollection(items, 5);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsNotEnumerable_ThenReturnsNull()
    {
        // Arrange & Act
        var result = SubjectLookup.FindSubjectInCollection(42, 0);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsDictionary_ThenReturnsSubjectAtKey()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var dict = new Dictionary<string, Person> { ["key1"] = person };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dict, "key1");

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenDictionaryKeyMissing_ThenReturnsNull()
    {
        // Arrange
        var dict = new Dictionary<string, Person> { ["key1"] = new() { FirstName = "Alice" } };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dict, "nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsReadOnlyDictionary_ThenReturnsSubjectViaKvpFallback()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var wrapper = new ReadOnlyDictionaryWrapper<string, Person>(
            new Dictionary<string, Person> { ["found"] = person });

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(wrapper, "found");

        // Assert
        Assert.Same(person, result);
    }

    [Fact]
    public void WhenReadOnlyDictionaryIsProbedWithAWrongTypedKey_ThenReturnsNull()
    {
        // Arrange: a reconcile probes one value of a property with the other value's occurrence
        // index, so an ordinal reaches a keyed lookup whenever the property changed shape.
        var wrapper = new ReadOnlyDictionaryWrapper<string, Person>(
            new Dictionary<string, Person> { ["exists"] = new() { FirstName = "A" } });

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(wrapper, 0);

        // Assert: answers null like the IDictionary path rather than throwing on the conversion.
        Assert.Null(result);
    }

    [Fact]
    public void WhenReadOnlyDictionaryKeyMissing_ThenReturnsNull()
    {
        // Arrange
        var wrapper = new ReadOnlyDictionaryWrapper<string, Person>(
            new Dictionary<string, Person> { ["exists"] = new() { FirstName = "A" } });

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(wrapper, "missing");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenDictionaryValueIsNotSubject_ThenReturnsNull()
    {
        // Arrange
        var dict = new Dictionary<string, string> { ["key"] = "not a subject" };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dict, "key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenValueIsNotDictionaryOrEnumerable_ThenReturnsNull()
    {
        // Arrange & Act
        var result = SubjectLookup.FindSubjectInDictionary(42, "key");

        // Assert
        Assert.Null(result);
    }


    /// <summary>
    /// Every BCL dictionary shape a subject dictionary property can legally hold, keyed by string.
    /// The non-generic ones (Hashtable, OrderedDictionary, ListDictionary, HybridDictionary) key on
    /// object, so a "wrong-typed" key is not expressible for them and only the generic ones carry
    /// the interesting cases.
    /// </summary>
    private static IDictionary CreateStringKeyedDictionary(string kind, string key, Person person)
    {
        var source = new Dictionary<string, Person> { [key] = person };
        return kind switch
        {
            "Dictionary" => source,
            "SortedDictionary" => new SortedDictionary<string, Person>(source),
            "SortedList" => new SortedList<string, Person>(source),
            "ConcurrentDictionary" => new ConcurrentDictionary<string, Person>(source),
            "ReadOnlyDictionary" => new ReadOnlyDictionary<string, Person>(source),
            "FrozenDictionary" => (IDictionary)source.ToFrozenDictionary(),
            "ImmutableDictionary" => (IDictionary)source.ToImmutableDictionary(),
            "ImmutableSortedDictionary" => (IDictionary)source.ToImmutableSortedDictionary(),
            // The builders are intolerant in the same way their immutable dictionaries are, and the
            // plain one additionally throws for a null key, so they are covered explicitly.
            "ImmutableDictionaryBuilder" => (IDictionary)ToBuilder(source.ToImmutableDictionary()),
            "ImmutableSortedDictionaryBuilder" => (IDictionary)ToBuilder(source.ToImmutableSortedDictionary()),
            "Hashtable" => new Hashtable { [key] = person },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static ImmutableDictionary<string, Person>.Builder ToBuilder(ImmutableDictionary<string, Person> source) => source.ToBuilder();

    private static ImmutableSortedDictionary<string, Person>.Builder ToBuilder(ImmutableSortedDictionary<string, Person> source) => source.ToBuilder();

    [Theory]
    [InlineData("Dictionary")]
    [InlineData("SortedDictionary")]
    [InlineData("SortedList")]
    [InlineData("ConcurrentDictionary")]
    [InlineData("ReadOnlyDictionary")]
    [InlineData("FrozenDictionary")]
    [InlineData("ImmutableDictionary")]
    [InlineData("ImmutableSortedDictionary")]
    [InlineData("ImmutableDictionaryBuilder")]
    [InlineData("ImmutableSortedDictionaryBuilder")]
    [InlineData("Hashtable")]
    public void WhenDictionaryKeyMatches_ThenReturnsSubjectForEveryDictionaryShape(string kind)
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var dictionary = CreateStringKeyedDictionary(kind, "key1", person);

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dictionary, "key1");

        // Assert
        Assert.Same(person, result);
    }

    [Theory]
    [InlineData("Dictionary")]
    [InlineData("SortedDictionary")]
    [InlineData("SortedList")]
    [InlineData("ConcurrentDictionary")]
    [InlineData("ReadOnlyDictionary")]
    [InlineData("FrozenDictionary")]
    [InlineData("ImmutableDictionary")]
    [InlineData("ImmutableSortedDictionary")]
    [InlineData("ImmutableDictionaryBuilder")]
    [InlineData("ImmutableSortedDictionaryBuilder")]
    [InlineData("Hashtable")]
    public void WhenDictionaryKeyIsAbsent_ThenReturnsNullForEveryDictionaryShape(string kind)
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var dictionary = CreateStringKeyedDictionary(kind, "key1", person);

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dictionary, "absent");

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Dictionary")]
    [InlineData("SortedDictionary")]
    [InlineData("SortedList")]
    [InlineData("ConcurrentDictionary")]
    [InlineData("ReadOnlyDictionary")]
    [InlineData("FrozenDictionary")]
    [InlineData("ImmutableDictionary")]
    [InlineData("ImmutableSortedDictionary")]
    [InlineData("ImmutableDictionaryBuilder")]
    [InlineData("ImmutableSortedDictionaryBuilder")]
    [InlineData("Hashtable")]
    public void WhenDictionaryKeyHasWrongType_ThenReturnsNullForEveryDictionaryShape(string kind)
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var dictionary = CreateStringKeyedDictionary(kind, "key1", person);

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dictionary, 1);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenIntKeyedImmutableDictionaryIsQueriedWithStringKey_ThenReturnsNull()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        var dictionary = new Dictionary<int, Person> { [1] = person }.ToImmutableDictionary();

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dictionary, "1");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenReadOnlyDictionaryWrapperKeyHasWrongType_ThenReturnsNull()
    {
        // Arrange
        var wrapper = new ReadOnlyDictionaryWrapper<string, Person>(
            new Dictionary<string, Person> { ["exists"] = new() { FirstName = "A" } });

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(wrapper, 1);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenGenericDictionaryKeyIsNull_ThenReturnsNull()
    {
        // Arrange
        var dictionary = new Dictionary<string, Person> { ["key1"] = new() { FirstName = "Alice" } };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dictionary, null!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenNonGenericDictionaryKeyIsNull_ThenReturnsNull()
    {
        // Arrange
        var dictionary = new Hashtable { ["key1"] = new Person { FirstName = "Alice" } };

        // Act
        var result = SubjectLookup.FindSubjectInDictionary(dictionary, null!);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenItemIsKvpWithSubjectValue_ThenReturnsTrueWithKeyAndSubject()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        object item = new KeyValuePair<string, Person>("myKey", person);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.True(result);
        Assert.Equal("myKey", key);
        Assert.Same(person, subject);
    }

    [Fact]
    public void WhenItemIsKvpWithNonSubjectValue_ThenReturnsFalse()
    {
        // Arrange
        object item = new KeyValuePair<string, int>("myKey", 42);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.False(result);
        Assert.Null(key);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenItemIsKvpWithNullValue_ThenReturnsFalse()
    {
        // Arrange
        object item = new KeyValuePair<string, Person?>("myKey", null);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.False(result);
        Assert.Null(key);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenItemIsNotKvp_ThenReturnsFalse()
    {
        // Arrange
        object item = "just a string";

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.False(result);
        Assert.Null(key);
        Assert.Null(subject);
    }

    [Fact]
    public void WhenItemIsIntegerKvpWithSubjectValue_ThenReturnsTrueWithIntKey()
    {
        // Arrange
        var person = new Person { FirstName = "Alice" };
        object item = new KeyValuePair<int, Person>(7, person);

        // Act
        var result = SubjectLookup.TryGetSubjectFromKeyValuePair(item, out var key, out var subject);

        // Assert
        Assert.True(result);
        Assert.Equal(7, key);
        Assert.Same(person, subject);
    }

    [Fact]
    public void WhenCalledRepeatedlyWithSameType_ThenCacheIsUsed()
    {
        // Arrange
        var person1 = new Person { FirstName = "A" };
        var person2 = new Person { FirstName = "B" };
        object item1 = new KeyValuePair<string, Person>("k1", person1);
        object item2 = new KeyValuePair<string, Person>("k2", person2);

        // Act
        SubjectLookup.TryGetSubjectFromKeyValuePair(item1, out var key1, out var subject1);
        SubjectLookup.TryGetSubjectFromKeyValuePair(item2, out var key2, out var subject2);

        // Assert
        Assert.Equal("k1", key1);
        Assert.Same(person1, subject1);
        Assert.Equal("k2", key2);
        Assert.Same(person2, subject2);
    }

    /// <summary>
    /// The intolerant types are learned rather than enumerated, so the mechanism itself needs a test:
    /// the outcome is correct either way, and only the second lookup distinguishes a type that was
    /// remembered from one that throws and is caught on every call.
    /// </summary>
    [Fact]
    public void WhenAnIntolerantDictionaryHasThrownOnce_ThenTheTypeIsRoutedAroundTheIndexerAfterwards()
    {
        // Arrange: a dictionary whose object indexer throws for an absent key, and which counts how
        // often that indexer is reached.
        var person = new Person { FirstName = "found" };
        var dictionary = new ThrowingCountingDictionary<string, Person> { ["present"] = person };

        // Act: the first absent lookup reaches the indexer and is caught; later ones must not.
        var first = SubjectLookup.FindSubjectInDictionary(dictionary, "absent");
        var indexerCallsAfterFirst = dictionary.IndexerCalls;
        var second = SubjectLookup.FindSubjectInDictionary(dictionary, "absent");
        var third = SubjectLookup.FindSubjectInDictionary(dictionary, "present");

        // Assert: every call answers correctly, and the throwing indexer was entered exactly once.
        Assert.Null(first);
        Assert.Null(second);
        Assert.Same(person, third);
        Assert.Equal(1, indexerCallsAfterFirst);
        Assert.Equal(1, dictionary.IndexerCalls);
    }

    /// <summary>Throws from the object indexer the way the immutable dictionaries do, and counts entries.</summary>
    private sealed class ThrowingCountingDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDictionary
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _inner = new();

        public int IndexerCalls { get; private set; }

        object? IDictionary.this[object key]
        {
            get
            {
                IndexerCalls++;
                return _inner[(TKey)key];
            }
            set => throw new NotSupportedException();
        }

        public TValue this[TKey key] { get => _inner[key]; set => _inner[key] = value; }
        public ICollection<TKey> Keys => _inner.Keys;
        public ICollection<TValue> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        public bool IsFixedSize => false;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        ICollection IDictionary.Keys => _inner.Keys;
        ICollection IDictionary.Values => _inner.Values;
        public void Add(TKey key, TValue value) => _inner.Add(key, value);
        public void Add(KeyValuePair<TKey, TValue> item) => _inner.Add(item.Key, item.Value);
        public void Add(object key, object? value) => throw new NotSupportedException();
        public void Clear() => _inner.Clear();
        public bool Contains(object key) => _inner.ContainsKey((TKey)key);
        public bool Contains(KeyValuePair<TKey, TValue> item) => _inner.ContainsKey(item.Key);
        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public bool Remove(TKey key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<TKey, TValue> item) => _inner.Remove(item.Key);
        public void Remove(object key) => throw new NotSupportedException();
        public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
        IDictionaryEnumerator IDictionary.GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }
}
