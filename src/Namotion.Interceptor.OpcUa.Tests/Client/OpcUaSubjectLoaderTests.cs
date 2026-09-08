using Namotion.Interceptor.Registry;
using Moq;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderTests : OpcUaSubjectLoaderTestsBase
{
    [Fact]
    public async Task WhenSubjectIsNotRegistered_ThenNoMonitoredItemsAreCreated()
    {
        // Arrange: the one loader test that cannot load the source's own root subject. It exercises
        // the guard for a subject that is absent from the registry, and the source's root subject
        // is registered by construction.
        var (loader, _, _) = CreateLoader();
        var subject = new DynamicSubject(InterceptorSubjectContext.Create()); // no registry

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task WhenTheNodeHasNoChildren_ThenNoMonitoredItemsAreCreated()
    {
        // Arrange
        var (loader, _, subject) = CreateLoader();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]> { [new NodeId(1, 0)] = [] });

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task WhenDynamicPropertiesAreEnabled_ThenUnmatchedChildNodesBecomeDynamicProperties()
    {
        // Arrange
        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("DynamicProperty", new NodeId(2001, 2))
            ]
        });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [new NodeId(2001, 2)] = (DataTypeIds.Int32, -1)
        });

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "DynamicProperty");
    }

    [Fact]
    public async Task WhenTwoChildNodesShareAPropertyName_ThenTheDuplicateIsNotMonitored()
    {
        // Arrange
        var (loader, _, subject) = CreateLoader();
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Add existing property
        registeredSubject.AddProperty("Temperature", _ => 0.0, (_, _) => { });

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("Temperature", new NodeId(2001, 2))
            ]
        });

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - Should not create monitored item for duplicate
        Assert.Empty(result);
    }

    [Fact]
    public async Task WhenAChildNodeTypeCannotBeResolved_ThenNoPropertyIsAdded()
    {
        // Arrange: ReadAsync returns a bad status code so the type cannot be resolved
        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("UnknownTypeProperty", new NodeId(2001, 2))
            ]
        });

        // No DataType mapping for NodeId 2001 => ReadAsync returns BadNodeIdUnknown => type resolves to null
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>());

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - Should not add property with unresolved type
        Assert.Empty(result);
    }

    [Fact]
    public async Task WhenAPropertyIsMonitored_ThenItsNodeIdIsTrackedAgainstThePropertyReference()
    {
        // Arrange
        var subject = new DynamicSubject(CreateSubjectContext());
        var (loader, ownership, source) = CreateLoaderFor(subject);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        registeredSubject.AddProperty(
            "Pressure",
            typeof(double),
            _ => 0.0,
            (_, _) => { },
            new OpcUaNodeAttribute("Pressure", "urn:test", "opc")
            {
                NodeIdentifier = "1002",
                NodeNamespaceUri = "urn:test"
            });

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("Pressure", new ExpandedNodeId("1002", "urn:test"))
            ]
        });

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: one monitored item for the matched property, whose reference is claimed and
        // carries the NodeId the attribute's identifier resolves to in the session's namespace
        // table (urn:test is index 1 there).
        var monitoredItem = Assert.Single(monitoredItems);
        Assert.Equal("Pressure", ((RegisteredSubjectProperty)monitoredItem.Handle!).Name);
        var claimedProperty = Assert.Single(ownership.Properties);
        Assert.Equal("Pressure", claimedProperty.Name);
        Assert.True(source.TryGetNodeId(claimedProperty, out var nodeId));
        Assert.Equal(new NodeId("1002", 1), nodeId);
        Assert.Equal(nodeId, monitoredItem.StartNodeId);
    }

    [Fact]
    public async Task WhenDynamicPropertyHasNumberDataType_ThenPropertyTypeIsDouble()
    {
        // Arrange
        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("NumericValue", new NodeId(3001, 2))
            ]
        });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [new NodeId(3001, 2)] = (DataTypeIds.Double, -1)
        });

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.Single(p => p.Name == "NumericValue");
        Assert.Equal(typeof(double), property.Type);
    }

    [Fact]
    public async Task WhenDynamicPropertyHasExtensionObjectDataType_ThenPropertyTypeIsExtensionObject()
    {
        // Arrange
        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("ComplexValue", new NodeId(3002, 2))
            ]
        });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [new NodeId(3002, 2)] = (DataTypeIds.Structure, -1)
        });

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.Single(p => p.Name == "ComplexValue");
        Assert.Equal(typeof(ExtensionObject), property.Type);
    }

    [Fact]
    public async Task WhenAddressSpaceHasCycle_ThenLoaderTerminatesWithoutInfiniteRecursion()
    {
        // Arrange: Root -> ChildA -> ChildB -> BackToRoot (cycle back to root's NodeId).
        // The loader must terminate and not recurse infinitely.
        var rootId = new NodeId(1, 0);
        var nodeAId = new NodeId(1001, 2);
        var nodeBId = new NodeId(1002, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateObjectReferenceDescription("ChildA", new ExpandedNodeId(nodeAId))
            ],
            [nodeAId] =
            [
                CreateObjectReferenceDescription("ChildB", new ExpandedNodeId(nodeBId))
            ],
            [nodeBId] =
            [
                CreateObjectReferenceDescription("BackToRoot", new ExpandedNodeId(rootId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: loader terminated and traversed both levels before hitting the cycle
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "ChildA");

        var childASubject = registeredSubject.Properties.Single(p => p.Name == "ChildA").GetValue() as IInterceptorSubject;
        Assert.NotNull(childASubject);
        var childARegistered = childASubject.TryGetRegisteredSubject()!;
        Assert.Contains(childARegistered.Properties, p => p.Name == "ChildB");
    }

    [Fact]
    public async Task WhenBrowseNameIsNull_ThenNodeIsSkippedWithoutCrash()
    {
        // Arrange: a child node has a null BrowseName.Name (malformed server response).
        // The loader should skip it gracefully, not throw NullReferenceException.
        var rootId = new NodeId(1, 0);
        var goodNodeId = new NodeId(2001, 2);
        var badNodeId = new NodeId(2002, 2);

        var nullBrowseNameRef = new ReferenceDescription
        {
            BrowseName = new QualifiedName(null),
            NodeId = new ExpandedNodeId(badNodeId),
            NodeClass = NodeClass.Variable
        };

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                nullBrowseNameRef,
                CreateTestReferenceDescription("GoodVar", new ExpandedNodeId(goodNodeId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [goodNodeId] = (DataTypeIds.Int32, -1),
            [badNodeId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act: should not throw
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the good node was processed despite the bad sibling
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "GoodVar");
    }

    [Fact]
    public async Task WhenALoadedChildSubjectLeavesTheGraph_ThenTheSourceReleasesItsClaims()
    {
        // Arrange: Root.Sensor is a loaded child carrying one claimed variable. This pins the
        // fixture shape as much as the release itself. SourceOwnershipManager subscribes to the
        // LifecycleInterceptor reachable from source.RootSubject.Context, so a fixture that rooted
        // the source on one subject and loaded another would wire the detach callback to an
        // interceptor the loaded graph never raises anything on, and every subject-detach path
        // (rollback, reload, reassignment) would silently go untested.
        var rootId = new NodeId(1, 0);
        var sensorId = new NodeId(2001, 2);
        var temperatureId = new NodeId(2002, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] = [CreateObjectReferenceDescription("Sensor", new ExpandedNodeId(sensorId))],
            [sensorId] = [CreateTestReferenceDescription("Temperature", new ExpandedNodeId(temperatureId))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [temperatureId] = (DataTypeIds.Double, -1)
        });

        var (loader, ownership, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var sensorProperty = registeredSubject.Properties.Single(property => property.Name == "Sensor");
        var sensorSubject = Assert.IsAssignableFrom<IInterceptorSubject>(sensorProperty.GetValue());

        var claimedProperty = Assert.Single(ownership.Properties);
        Assert.Same(sensorSubject, claimedProperty.Subject);

        // Act: drop the child out of the graph, the same lifecycle transition a rollback or a
        // reload that no longer sees the node produces.
        sensorProperty.SetValue(null);

        // Assert
        Assert.Empty(ownership.Properties);
    }
}
