using System.Collections.Immutable;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathExtensionsTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }


    // --- Immutable dictionary values behind a broadly declared property ---

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndKeyIsAbsent_ThenSubjectPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[absent]");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndKeyHasWrongType_ThenSubjectPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        // A numeric segment is parsed into an int, which cannot be a key of a string-keyed dictionary.
        var result = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[1]");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndIntermediateKeyIsAbsent_ThenPropertyPathResolvesToNull()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[absent].Value");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenPropertyHoldsImmutableDictionaryAndKeyExists_ThenSubjectPathResolves()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestBroadContainer(context) { Name = "Root" };
        container.Items = new Dictionary<string, TestItem> { ["key1"] = item }.ToImmutableDictionary();
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[key1]");

        // Assert
        Assert.NotNull(result);
        Assert.Same(item, result.Subject);
    }

    // --- TryGetPropertyFromPath ---

    [Fact]
    public void TryGetPropertyFromPath_SimpleProperty_ReturnsPropertyWithNullIndex()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Name");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Name", property.Name);
        Assert.Null(index);
    }

    [Fact]
    public void TryGetPropertyFromPath_DictionaryPath_ReturnsPropertyWithStringIndex()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[key1]");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Items", property.Name);
        Assert.Equal("key1", index);
    }

    [Fact]
    public void TryGetPropertyFromPath_NestedDictionaryProperty_ReturnsLeafProperty()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[key1].Value");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Value", property.Name);
        Assert.Null(index);
    }

    [Fact]
    public void TryGetPropertyFromPath_NonExistentPath_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "NonExistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetPropertyFromPath_NonExistentDictionaryKey_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["exists"] = new TestItem(context) { Value = "v" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act - the key "missing" does not exist in the dictionary,
        // so navigating through it to .Value should fail
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[missing].Value");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetPropertyFromPath_EmptyPath_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "");

        // Assert
        Assert.Null(result);
    }

    // --- TryGetSubjectFromPath ---

    [Fact]
    public void TryGetSubjectFromPath_DictionaryIndex_ReturnsChildSubject()
    {
        // Arrange
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[key1]");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(item, resolved.Subject);
    }

    [Fact]
    public void TryGetSubjectFromPath_NestedDictionaries_ResolvesDeepSubject()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new TestItem(context) { Value = "leaf" };
        var middle = new TestItem(context) { Value = "middle" };
        middle.Children["leaf"] = leaf;
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["middle"] = middle;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[middle].Children[leaf]");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(leaf, resolved.Subject);
    }

    [Fact]
    public void TryGetSubjectFromPath_NonExistentKey_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Items[nonexistent]");

        // Assert
        Assert.Null(resolved);
    }

    [Fact]
    public void TryGetSubjectFromPath_NonExistentProperty_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Bogus[key]");

        // Assert
        Assert.Null(resolved);
    }

    // --- GetPropertiesFromPaths ---

    [Fact]
    public void GetPropertiesFromPaths_MultiplePaths_ReturnsTuplesWithIndices()
    {
        // Arrange
        var context = CreateContext();
        var item1 = new TestItem(context) { Value = "v1" };
        var item2 = new TestItem(context) { Value = "v2" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["a"] = item1;
        container.Items["b"] = item2;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        var paths = new[] { "Name", "Items[a]", "Items[b].Value" };

        // Act
        var results = pathProvider.GetPropertiesFromPaths(rootRegistered, paths).ToList();

        // Assert
        Assert.Equal(3, results.Count);

        // "Name" - simple property, no index
        Assert.Equal("Name", results[0].Property.Name);
        Assert.Null(results[0].Index);

        // "Items[a]" - dictionary property with string index
        Assert.Equal("Items", results[1].Property.Name);
        Assert.Equal("a", results[1].Index);

        // "Items[b].Value" - nested property, no index on the leaf
        Assert.Equal("Value", results[2].Property.Name);
        Assert.Null(results[2].Index);
    }

    [Fact]
    public void GetPropertiesFromPaths_SkipsInvalidPaths()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        var paths = new[] { "Name", "DoesNotExist", "AlsoMissing" };

        // Act
        var results = pathProvider.GetPropertiesFromPaths(rootRegistered, paths).ToList();

        // Assert
        Assert.Single(results);
        Assert.Equal("Name", results[0].Property.Name);
    }

    [Fact]
    public void TryGetPropertyFromPath_InlinePaths_SegmentIsUsedAsDictionaryKey()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Child" };
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act — "Servers" is the dictionary key, not a property name
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Servers");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Children", property.Name);
        Assert.Equal("Servers", index);
    }

    [Fact]
    public void TryGetPropertyFromPath_InlinePaths_NestedProperty()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "OpcUaServer" };
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act — navigate through InlinePaths to a property on the child
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Servers.Name");

        // Assert
        Assert.NotNull(result);
        var (property, index) = result.Value;
        Assert.Equal("Name", property.Name);
        Assert.Null(index);
        Assert.Equal("OpcUaServer", property.GetValue());
    }

    [Fact]
    public void TryGetSubjectFromPath_InlinePaths_ResolvesChildSubject()
    {
        // Arrange
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Server" };
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = child;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Servers");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(child, resolved.Subject);
    }

    [Fact]
    public void TryGetSubjectFromPath_InlinePaths_NestedContainers()
    {
        // Arrange
        var context = CreateContext();
        var leaf = new TestInlineContainer(context) { Name = "OpcUaServer" };
        var middle = new TestInlineContainer(context) { Name = "Servers" };
        middle.Children["OpcUaServer"] = leaf;
        var root = new TestInlineContainer(context) { Name = "Root" };
        root.Children["Servers"] = middle;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act — two levels of InlinePaths flattening
        var resolved = pathProvider.TryGetSubjectFromPath(rootRegistered, "Servers.OpcUaServer");

        // Assert
        Assert.NotNull(resolved);
        Assert.Same(leaf, resolved.Subject);
    }

    [Fact]
    public void TryGetPath_InlinePaths_EmitsKeyAsPlainSegment()
    {
        // Arrange — set dictionary via property setter to trigger lifecycle tracking
        var context = CreateContext();
        var child = new TestInlineContainer(context) { Name = "Server" };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["Servers"] = child }
        };
        var pathProvider = DefaultPathProvider.Instance;

        var childRegistered = child.TryGetRegisteredSubject()!;
        var nameProperty = childRegistered.Properties
            .First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(pathProvider, root);

        // Assert — should be "Servers.Name", not "Children[Servers].Name"
        Assert.Equal("Servers.Name", path);
    }

    [Fact]
    public void TryGetPath_InlinePaths_NestedContainers()
    {
        // Arrange — set dictionaries via property setters
        var context = CreateContext();
        var leaf = new TestInlineContainer(context) { Name = "OpcUaServer" };
        var middle = new TestInlineContainer(context)
        {
            Name = "Servers",
            Children = new Dictionary<string, TestInlineContainer> { ["OpcUaServer"] = leaf }
        };
        var root = new TestInlineContainer(context)
        {
            Name = "Root",
            Children = new Dictionary<string, TestInlineContainer> { ["Servers"] = middle }
        };
        var pathProvider = DefaultPathProvider.Instance;

        var leafRegistered = leaf.TryGetRegisteredSubject()!;
        var nameProperty = leafRegistered.Properties
            .First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(pathProvider, root);

        // Assert — should be "Servers.OpcUaServer.Name"
        Assert.Equal("Servers.OpcUaServer.Name", path);
    }

    [Fact]
    public void TryGetPath_MixedInlineAndRegular_FormatsCorrectly()
    {
        // Arrange — set dictionary via property setter
        var context = CreateContext();
        var inlineChild = new TestInlineContainer(context) { Name = "Leaf" };
        var inlineRoot = new TestInlineContainer(context)
        {
            Name = "Folder",
            Children = new Dictionary<string, TestInlineContainer> { ["Leaf"] = inlineChild }
        };
        var pathProvider = DefaultPathProvider.Instance;

        var leafRegistered = inlineChild.TryGetRegisteredSubject()!;
        var nameProperty = leafRegistered.Properties
            .First(p => p.Name == "Name");

        // Act
        var path = nameProperty.TryGetPath(pathProvider, inlineRoot);

        // Assert — just one level of inline: "Leaf.Name"
        Assert.Equal("Leaf.Name", path);
    }

    [Fact]
    public void TryGetPropertyFromPath_InlinePaths_NonExistentKey_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestInlineContainer(context) { Name = "Root" };
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = root.TryGetRegisteredSubject()!;

        // Act
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "NonExistent.Name");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetPropertyFromPath_WithoutInlinePaths_BracketNotationStillWorks()
    {
        // Arrange — regular TestContainer with bracket notation
        var context = CreateContext();
        var item = new TestItem(context) { Value = "hello" };
        var container = new TestContainer(context) { Name = "Root" };
        container.Items["key1"] = item;
        var pathProvider = DefaultPathProvider.Instance;
        var rootRegistered = container.TryGetRegisteredSubject()!;

        // Act — bracket notation still works for non-InlinePaths dictionaries
        var result = pathProvider.TryGetPropertyFromPath(rootRegistered, "Items[key1].Value");

        // Assert
        Assert.NotNull(result);
        var (property, _) = result.Value;
        Assert.Equal("Value", property.Name);
    }

    [Fact]
    public void WhenRootProperty_ThenTryGetPathReturnsPropertyName()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = property.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Name", path);
    }

    [Fact]
    public void WhenUsingDefaultProviderOverload_ThenDelegatesToDefaultProvider()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the convenience overload uses DefaultPathProvider.Instance and threads rootSubject through
        var absolute = firstNameProperty.TryGetPath();
        var relative = firstNameProperty.TryGetPath(parent);

        // Assert
        Assert.Equal("Father.FirstName", absolute);
        Assert.Equal(firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null), absolute);
        Assert.Equal(firstNameProperty.TryGetPath(DefaultPathProvider.Instance, parent), relative);
    }

    [Fact]
    public void WhenNestedSubjectProperty_ThenTryGetPathReturnsDottedPath()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenCustomSeparator_ThenTryGetPathUsesSeparator()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(new DefaultPathProvider('/'), null);

        // Assert
        Assert.Equal("Father/FirstName", path);
    }

    [Fact]
    public void WhenRootSubjectProvided_ThenTryGetPathStopsAtRoot()
    {
        // Arrange
        var context = CreateContext();
        var grandparent = new Models.Person(context) { FirstName = "Grandparent" };
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        grandparent.Father = parent;
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var pathFromParent = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, parent);
        var pathFromGrandparent = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, grandparent);
        var pathAbsolute = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Father.FirstName", pathFromParent);
        Assert.Equal("Father.Father.FirstName", pathFromGrandparent);
        Assert.Equal("Father.Father.FirstName", pathAbsolute);
    }

    [Fact]
    public void WhenRootSubjectNotInParentChain_ThenTryGetPathReturnsNull()
    {
        // Arrange - 'unrelated' is a separate root that is not an ancestor of the property
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var unrelated = new Models.Person(context) { FirstName = "Unrelated" };
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the property is not reachable from 'unrelated', so there is no relative path
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, unrelated);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectIsReachableFromRootViaSecondParent_ThenTryGetPathResolvesViaThatParent()
    {
        // Arrange - 'shared' has two parents; only the second is reachable from 'root'
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var other = new Models.Person(context) { FirstName = "Other" };
        var shared = new Models.Person(context) { FirstName = "Shared" };

        // 'shared' is referenced first by 'other' (not under root) and then by root.Father
        other.Father = shared;
        root.Father = shared;
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the first parent ('other') does not reach 'root', so the search must use the second
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenRootSubjectIsPropertyOwner_ThenTryGetPathReturnsPropertyName()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, child);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Fact]
    public void WhenSubjectParentChainHasCycle_ThenTryGetPathReturnsNull()
    {
        // Arrange - mutual Father references form a cycle in the parent chain
        var context = CreateContext();
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var firstNameProperty = a.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - a cycle has no finite path, so it is reported as null rather than throwing
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectReachableFromRootViaTwoAcyclicPaths_ThenTryGetPathResolves()
    {
        // Arrange - a diamond: 'shared' is reachable from 'root' through both Father->A and Mother->B,
        // so the per-branch cycle pruning must not falsely prune the shared subject.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        var shared = new Models.Person(context) { FirstName = "Shared" };
        root.Father = a;
        root.Mother = b;
        a.Father = shared; // shared's first parent
        b.Father = shared; // shared's second parent
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert - resolves via the first parent that reaches the root
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenSharedDeadEndIsMemoizedDuringSearch_ThenRootStillResolvesViaAnotherBranch()
    {
        // Arrange - 'leaf' has three parents. The first two (p1, p2) lead only to a shared dead-end
        // subject ('dead') that cannot reach the root, so the search explores 'dead' via p1, memoizes it
        // as unreachable, then hits that memo when reaching 'dead' again via p2. The third parent ('good')
        // reaches the root. This exercises the unreachable-memoization path and confirms a memoized
        // dead-end does not prevent the root being found through another branch.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var good = new Models.Person(context) { FirstName = "Good" };
        var p1 = new Models.Person(context) { FirstName = "P1" };
        var p2 = new Models.Person(context) { FirstName = "P2" };
        var dead = new Models.Person(context) { FirstName = "Dead" };
        var leaf = new Models.Person(context) { FirstName = "Leaf" };

        p1.Father = leaf;   // leaf's first parent
        p2.Father = leaf;   // leaf's second parent
        good.Father = leaf; // leaf's third parent (the only one reaching the root)
        dead.Father = p1;   // p1 -> dead (dead end)
        dead.Mother = p2;   // p2 -> the same shared dead end
        root.Father = good; // good -> root
        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenParentChainHasCycleButRootReachableViaOtherBranch_ThenTryGetPathResolves()
    {
        // Arrange - 'shared' has a cyclic first parent (cyclicX <-> cyclicY) that never reaches the root,
        // and a second parent 'good' that does. The cycle on the first branch must not prevent the search
        // from finding the root through the acyclic branch.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var good = new Models.Person(context) { FirstName = "Good" };
        var cyclicX = new Models.Person(context) { FirstName = "X" };
        var cyclicY = new Models.Person(context) { FirstName = "Y" };
        var shared = new Models.Person(context) { FirstName = "Shared" };

        cyclicX.Mother = shared;  // shared's first parent (leads into a cycle)
        good.Father = shared;     // shared's second parent (reaches the root)
        cyclicX.Father = cyclicY;
        cyclicY.Father = cyclicX; // cyclicX <-> cyclicY cycle
        root.Father = good;       // good is reachable from root
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenMultiParentChainIsCyclicAndRootUnreachable_ThenTryGetPathReturnsNull()
    {
        // Arrange - 'leaf' has two parents (forcing the multi-parent search): one into a cycle, one a
        // dead end. The provided root is reachable from neither, so the result is null (not a throw).
        var context = CreateContext();
        var unrelated = new Models.Person(context) { FirstName = "Unrelated" };
        var leaf = new Models.Person(context) { FirstName = "Leaf" };
        var cyclic = new Models.Person(context) { FirstName = "Cyclic" };
        var deadEnd = new Models.Person(context) { FirstName = "DeadEnd" };

        cyclic.Father = leaf;     // leaf's first parent
        deadEnd.Mother = leaf;    // leaf's second parent (forces the multi-parent search)
        leaf.Father = cyclic;     // leaf <-> cyclic cycle on the first branch
        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, unrelated);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenMultiParentChainIsVeryDeep_ThenTryGetPathResolvesWithoutStackOverflow()
    {
        // Arrange - a parent chain far deeper than recursion would tolerate, with a multi-parent branch at
        // the leaf to force the iterative search. Because the search uses an explicit stack (not the call
        // stack) and is not depth-bounded, it resolves the full path instead of overflowing or giving up.
        const int depth = 10_000;
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var chain = new List<Models.Person> { root };
        for (var i = 0; i < depth; i++)
        {
            var next = new Models.Person(context) { FirstName = $"P{i}" };
            chain[^1].Father = next; // chain[i] is the parent of chain[i+1]
            chain.Add(next);
        }

        var leaf = chain[^1];
        var extra = new Models.Person(context) { FirstName = "Extra" };
        extra.Mother = leaf; // second parent for the leaf forces the multi-parent search

        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert - resolves to "Father" repeated for each hop up the chain, then the leaf property
        Assert.NotNull(path);
        var segments = path!.Split('.');
        Assert.Equal(depth + 1, segments.Length);
        Assert.Equal("FirstName", segments[^1]);
        Assert.All(segments[..^1], segment => Assert.Equal("Father", segment));
    }

    [Fact]
    public void WhenPropertyIsCollectionElement_ThenDefaultProviderPathIncludesBracketIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new TryGetPathIndexRoot(context);
        root.Items = [new TryGetPathIndexChild(context), new TryGetPathIndexChild(context)];
        var nameProperty = root.Items[1].TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Items[1].Name", path);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexRoot
{
    public partial TryGetPathIndexChild[] Items { get; set; }

    public TryGetPathIndexRoot()
    {
        Items = [];
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexChild
{
    public partial string Name { get; set; }

    public TryGetPathIndexChild()
    {
        Name = "";
    }
}
