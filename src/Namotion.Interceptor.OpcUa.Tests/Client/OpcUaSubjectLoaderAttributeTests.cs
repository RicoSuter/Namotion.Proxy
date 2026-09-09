using Namotion.Interceptor.Registry;
using Namotion.Interceptor.OpcUa.Attributes;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderAttributeTests : OpcUaSubjectLoaderTestsBase
{
    [Fact]
    public async Task WhenAttributeChainExceedsMaxTraversals_ThenLoaderStopsAtConfiguredDepth()
    {
        // Arrange: a 4-level deep chain of variable-typed sub-attributes
        //   Root -> Level1 -> Level2 -> Level3 -> Level4
        // Each level is discovered via the dynamic-attribute path. With
        // MaxAttributeTraversalDepth = 2, the loader processes exactly two rounds
        // of attribute traversal: round 1 adds Level2 to Level1, round 2 adds
        // Level3 to Level2. Round 3 (which would add Level4 to Level3) is
        // aborted by the safety bound.
        var level1Id = new NodeId(7001, 2);
        var level2Id = new NodeId(7002, 2);
        var level3Id = new NodeId(7003, 2);
        var level4Id = new NodeId(7004, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] = [CreateTestReferenceDescription("Level1", new ExpandedNodeId(level1Id))],
            [level1Id] = [CreateTestReferenceDescription("Level2", new ExpandedNodeId(level2Id))],
            [level2Id] = [CreateTestReferenceDescription("Level3", new ExpandedNodeId(level3Id))],
            [level3Id] = [CreateTestReferenceDescription("Level4", new ExpandedNodeId(level4Id))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [level1Id] = (DataTypeIds.Int32, -1),
            [level2Id] = (DataTypeIds.Int32, -1),
            [level3Id] = (DataTypeIds.Int32, -1),
            [level4Id] = (DataTypeIds.Int32, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true),
            maxAttributeTraversalDepth: 2);

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Level1 -> Level2 -> Level3 chain exists, Level4 was aborted by the cap.
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var level1 = registeredSubject.Properties.Single(p => p.Name == "Level1");
        var level2 = level1.TryGetAttribute("Level2");
        Assert.NotNull(level2);
        var level3 = level2.TryGetAttribute("Level3");
        Assert.NotNull(level3);
        Assert.Null(level3.TryGetAttribute("Level4"));
    }

    [Fact]
    public async Task WhenAttributeAlreadyRegisteredByExternalSource_ThenDynamicAttributeIsSkippedWithoutCrash()
    {
        // Arrange: simulate the HomeBlaze ServerStatus@State crash. A lifecycle handler from
        // another source registers a registry attribute (here: "State") on a property *before*
        // the OPC UA loader browses the server. The browse then returns a same-named dynamic
        // Variable child, which the loader's second pass would normally try to AddAttribute,
        // throwing "duplicate key". The safety net in LoadAttributesAsync should detect the
        // existing registration via TryGetAttribute and skip with a warning instead.
        var statusNodeId = new NodeId(7001, 2);
        var stateNodeId = new NodeId(7002, 2);

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId))
            ],
            [statusNodeId] =
            [
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId))
            ]
        });

        var (loader, _, subject) = CreateLoader(shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var registeredSubject = subject.TryGetRegisteredSubject()!;

        var serverStatus = registeredSubject.AddProperty(
            "ServerStatus",
            typeof(int),
            _ => 0,
            (_, _) => { },
            new OpcUaNodeAttribute("ServerStatus", "urn:test", "opc")
            {
                NodeIdentifier = "7001",
                NodeNamespaceUri = "urn:test"
            });

        // Pre-register a "State" attribute via a path other than OPC UA browse (no OpcUaNode-
        // Attribute) so the loader's pass 1 cannot match it via the Mapper.
        object? stateValue = null;
        var preRegisteredState = serverStatus.AddAttribute(
            "State",
            typeof(string),
            _ => stateValue,
            (_, o) => stateValue = o);

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act: must not throw "duplicate key".
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the pre-registered attribute survives unchanged (same reference).
        // Identity check, not just existence: discriminates the safety-net path from a hypo-
        // thetical future "make AddAttribute idempotent by replacing" regression that would
        // also not crash but would silently overwrite the lifecycle-handler registration.
        var stateAfterLoad = serverStatus.TryGetAttribute("State");
        Assert.Same(preRegisteredState, stateAfterLoad);
    }

    [Fact]
    public async Task WhenSiblingParentsStageSameDynamicAttribute_ThenDuplicateIsSkippedWithoutCrash()
    {
        // Arrange: two sibling Variable references share the BrowseName "ServerStatus" but
        // point to different NodeIds, and both resolve to the same declared property via the
        // mapper's BrowseName match. Each parent NodeId exposes a dynamic Variable child
        // named "EngineeringUnits", so both (property, parent) entries stage the same
        // (property, browse name) attribute within one round. The second AddAttribute would
        // throw a duplicate-key exception and abort the load on every retry; the loader
        // must skip the duplicate instead.
        var statusNodeId1 = new NodeId(5001, 2);
        var statusNodeId2 = new NodeId(5002, 2);
        var unitsNodeId1 = new NodeId(6001, 2);
        var unitsNodeId2 = new NodeId(6002, 2);

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId1)),
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId2))
            ],
            [statusNodeId1] = [CreateTestReferenceDescription("EngineeringUnits", new ExpandedNodeId(unitsNodeId1))],
            [statusNodeId2] = [CreateTestReferenceDescription("EngineeringUnits", new ExpandedNodeId(unitsNodeId2))]
        });
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [unitsNodeId1] = (DataTypeIds.String, -1),
            [unitsNodeId2] = (DataTypeIds.String, -1)
        });

        var (loader, _, subject) = CreateLoader(shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var serverStatus = registeredSubject.AddProperty(
            "ServerStatus",
            typeof(int),
            _ => 0,
            (_, _) => { },
            new OpcUaNodeAttribute("ServerStatus", null, "opc"));

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act - must not throw a duplicate-key exception
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: exactly one "EngineeringUnits" attribute exists, bound to the first
        // parent's child NodeId (browse order wins, matching the sibling property dedup).
        Assert.NotNull(serverStatus.TryGetAttribute("EngineeringUnits"));
        Assert.Contains(monitoredItems, item => unitsNodeId1.Equals(item.StartNodeId));
        Assert.DoesNotContain(monitoredItems, item => unitsNodeId2.Equals(item.StartNodeId));
    }

    [Fact]
    public async Task WhenAttributeParentNodeIdWasVisitedInEarlierRound_ThenAttributesAreStillLoaded()
    {
        // Arrange: VarA (NodeId 100) and VarB (NodeId 200) are top-level dynamic Variables.
        // VarA has a dynamic attribute "Quality" -> NodeId 999 (a leaf).
        // VarB has a dynamic attribute "Status"  -> NodeId 100 (the SAME NodeId as VarA's parent).
        //
        // LoadAttributesAsync runs in rounds and skips an entry only when the same
        // (property, parent NodeId) pair was processed in an earlier round:
        //   Round 1: processes (VarA, 100) and (VarB, 200) and creates VarA.Quality and VarB.Status.
        //   Round 2: processes (VarA.Quality, 999) and (VarB.Status, 100). The second pair is new
        //            even though NodeId 100 was browsed in round 1, and the load context's browse
        //            cache serves its children without a second browse, so VarB.Status gets its
        //            own "Quality" sub-attribute. A traversal keyed on NodeIds alone would drop
        //            the entry and its subtree.
        var rootId = new NodeId(1, 0);
        var varAId = new NodeId(100, 2);
        var varBId = new NodeId(200, 2);
        var sharedQualityId = new NodeId(999, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateTestReferenceDescription("VarA", new ExpandedNodeId(varAId)),
                CreateTestReferenceDescription("VarB", new ExpandedNodeId(varBId))
            ],
            [varAId] =
            [
                CreateTestReferenceDescription("Quality", new ExpandedNodeId(sharedQualityId))
            ],
            [varBId] =
            [
                // VarB's "Status" attribute points to the SAME NodeId as VarA itself,
                // creating the cross-round duplicate that the fix must handle.
                CreateTestReferenceDescription("Status", new ExpandedNodeId(varAId))
            ]
            // sharedQualityId is a leaf (no children).
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [varAId] = (DataTypeIds.Int32, -1),
            [varBId] = (DataTypeIds.Int32, -1),
            [sharedQualityId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: VarA has a Quality attribute (round 1 -> round 2 along the normal path).
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var varAProperty = registeredSubject.Properties.Single(p => p.Name == "VarA");
        Assert.NotNull(varAProperty.TryGetAttribute("Quality"));

        // Assert: VarB has a Status attribute, and crucially Status has its own Quality
        // sub-attribute, served from the browse cache for the parent NodeId round 1 browsed.
        var varBProperty = registeredSubject.Properties.Single(p => p.Name == "VarB");
        var statusAttribute = varBProperty.TryGetAttribute("Status");
        Assert.NotNull(statusAttribute);
        Assert.NotNull(statusAttribute.TryGetAttribute("Quality"));
    }

    [Fact]
    public async Task WhenAttributeGraphHasCycle_ThenTraversalTerminatesBeforeTheCap()
    {
        // Arrange: Root -> Value, whose attribute A has the child B, and B's child is A again by
        // NodeId. Following NodeIds alone nests A under B under A without end, and with
        // MaxAttributeTraversalDepth = 100 the cap alone would still create a hundred attributes.
        var valueId = new NodeId(7101, 2);
        var attributeAId = new NodeId(7102, 2);
        var attributeBId = new NodeId(7103, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(valueId))],
            [valueId] = [CreateTestReferenceDescription("A", new ExpandedNodeId(attributeAId))],
            [attributeAId] = [CreateTestReferenceDescription("B", new ExpandedNodeId(attributeBId))],
            [attributeBId] = [CreateTestReferenceDescription("A", new ExpandedNodeId(attributeAId))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [valueId] = (DataTypeIds.Int32, -1),
            [attributeAId] = (DataTypeIds.Int32, -1),
            [attributeBId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true),
            maxAttributeTraversalDepth: 100);

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: exactly Value.A and Value.A.B exist, and the traversal stopped on its own rather
        // than on the cap.
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var value = registeredSubject.Properties.Single(property => property.Name == "Value");
        var attributeA = Assert.Single(value.Attributes);
        var attributeB = Assert.Single(attributeA.Attributes);
        Assert.Empty(attributeB.Attributes);
        Assert.Equal(2, registeredSubject.Properties.Count(property => property.IsAttribute));

        var browseCallCount = mockSession.Invocations.Count(invocation => invocation.Method.Name == nameof(ISession.BrowseAsync));
        Assert.InRange(browseCallCount, 1, 9);
    }
}
