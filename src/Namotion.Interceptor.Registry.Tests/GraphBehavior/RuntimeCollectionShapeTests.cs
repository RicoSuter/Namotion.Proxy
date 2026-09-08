using System.Collections;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

public class RuntimeCollectionShapeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnObjectCollectionRetainsDuplicateChildren_ThenRegistryIndicesMatchOwnership(bool useList)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new RuntimeCollectionRoot(context);
        var child = new RuntimeCollectionRoot();
        var replacement = new RuntimeCollectionRoot();
        root.Payload = useList ? new List<object> { child, child } : new object[] { child, child };

        // Act
        root.Payload = useList ? new List<object> { child, replacement, child } : new object[] { child, replacement, child };

        // Assert
        AssertIndices(root, child, [0, 2]);
        AssertIndices(root, replacement, [1]);
    }

    [Fact]
    public void WhenATypedCollectionRetainsDuplicateChildren_ThenRegistryIndicesMatchOwnership()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new RuntimeCollectionRoot(context);
        var child = new RuntimeCollectionRoot();
        var replacement = new RuntimeCollectionRoot();
        root.Children = [child, child];

        // Act
        root.Children = [child, replacement, child];

        // Assert
        AssertIndices(root, child, [0, 2], nameof(root.Children));
        AssertIndices(root, replacement, [1], nameof(root.Children));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenADictionaryRekeysDuplicateChildren_ThenRegistryPreservesTheKeys(bool readOnly, bool declaredDictionary)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new RuntimeCollectionRoot(context);
        var child = new RuntimeCollectionRoot();
        var replacement = new RuntimeCollectionRoot();
        var firstKey = new object();
        var secondKey = new object();
        var movedKey = new object();
        var replacementKey = new object();
        var initial = new Dictionary<object, RuntimeCollectionRoot> { [firstKey] = child, [secondKey] = child };
        if (declaredDictionary) root.ByKey = readOnly ? new DictionaryView(initial) : initial;
        else root.Payload = readOnly ? new DictionaryView(initial) : initial;
        var updated = new Dictionary<object, RuntimeCollectionRoot>
        {
            [secondKey] = child, [replacementKey] = replacement, [movedKey] = child
        };

        // Act
        if (declaredDictionary) root.ByKey = readOnly ? new DictionaryView(updated) : updated;
        else root.Payload = readOnly ? new DictionaryView(updated) : updated;

        // Assert
        var propertyName = declaredDictionary ? nameof(root.ByKey) : nameof(root.Payload);
        AssertIndices(root, child, [secondKey, movedKey], propertyName);
        AssertIndices(root, replacement, [replacementKey], propertyName);
    }

    private static void AssertIndices(RuntimeCollectionRoot root, RuntimeCollectionRoot child, object[] expected, string propertyName = nameof(RuntimeCollectionRoot.Payload))
    {
        var property = root.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        AssertSameOccurrences(expected, child.GetParents().Select(parent => parent.Index));
        AssertSameOccurrences(expected, child.TryGetRegisteredSubject()!.Parents.Select(parent => parent.Index));
        AssertSameOccurrences(expected, property.Children.Where(entry => ReferenceEquals(entry.Subject, child)).Select(entry => entry.Index));
        Assert.Equal(expected.Length, child.GetReferenceCount());
    }

    private static void AssertSameOccurrences(object[] expected, IEnumerable<object?> actual)
    {
        var indices = actual.ToArray();
        Assert.Equal(expected.Length, indices.Length);
        foreach (var index in expected) Assert.Single(indices, candidate => Equals(candidate, index));
    }

    private sealed class DictionaryView(Dictionary<object, RuntimeCollectionRoot> entries) : IReadOnlyDictionary<object, RuntimeCollectionRoot>
    {
        public RuntimeCollectionRoot this[object key] => entries[key];
        public IEnumerable<object> Keys => entries.Keys;
        public IEnumerable<RuntimeCollectionRoot> Values => entries.Values;
        public int Count => entries.Count;
        public bool ContainsKey(object key) => entries.ContainsKey(key);
        public bool TryGetValue(object key, out RuntimeCollectionRoot value) => entries.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<object, RuntimeCollectionRoot>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

[InterceptorSubject]
public partial class RuntimeCollectionRoot
{
    public partial object? Payload { get; set; }
    public partial RuntimeCollectionRoot[]? Children { get; set; }
    public partial IReadOnlyDictionary<object, RuntimeCollectionRoot>? ByKey { get; set; }
}
