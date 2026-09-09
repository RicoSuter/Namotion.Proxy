using Namotion.Interceptor.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderBatchingTests : OpcUaSubjectLoaderTestsBase
{
    [Fact]
    public async Task WhenNodeCountExceedsMaxNodesPerBrowse_ThenBrowseIsChunked()
    {
        // Arrange: set MaxNodesPerBrowse = 2, tree has 4 leaf variables as dynamic properties.
        // Phase 5 (LoadAttributesAsync) batch-browses all 4 variable nodes at once
        // via BrowseManyNodesAsync, which must chunk into multiple BrowseAsync calls.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var var3Id = new NodeId(2003, 2);
        var var4Id = new NodeId(2004, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id)),
                CreateTestReferenceDescription("Var3", new ExpandedNodeId(var3Id)),
                CreateTestReferenceDescription("Var4", new ExpandedNodeId(var4Id))
            ]
            // Variable leaf nodes have no children in the browse tree, so they return empty results.
        };

        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [var3Id] = (DataTypeIds.Int32, -1),
            [var4Id] = (DataTypeIds.String, -1)
        };

        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits
        {
            MaxNodesPerBrowse = 2,
            MaxNodesPerRead = 0 // unlimited reads
        });
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, dataTypes);

        var (loader, ownership, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: all 4 variable properties are discovered and monitored
        Assert.Equal(4, monitoredItems.Count);

        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(var3Id, monitoredNodeIds);
        Assert.Contains(var4Id, monitoredNodeIds);

        // Assert: all properties are owned by the source
        Assert.Equal(4, ownership.Properties.Count());

        // Assert: exactly one BrowseAsync call per tree level, chunked by MaxNodesPerBrowse.
        // Call 1: the root node (1 node).
        // Calls 2 and 3: Phase 5 batch-browses the 4 variable nodes for attribute discovery,
        //                split into 2 calls of 2 by the MaxNodesPerBrowse = 2 limit.
        // An exact count is what pins the batching: a loader that browsed node by node would
        // need 5 calls here and would still satisfy any lower bound.
        var nodesPerBrowseCall = mockSession.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ISession.BrowseAsync))
            .Select(invocation => ((BrowseDescriptionCollection)invocation.Arguments[3]).Count)
            .ToArray();
        Assert.Equal(3, nodesPerBrowseCall.Length);

        // Assert: at least one call carried more than one NodeId. This is what actually separates
        // a batched loader from a per-node one. The call count alone could be met by a per-node
        // loader on a smaller tree, but only batching puts several nodes in one request.
        Assert.Contains(nodesPerBrowseCall, nodeCount => nodeCount > 1);

        // Assert: no call exceeded the configured MaxNodesPerBrowse of 2. A real server rejects an
        // oversized request outright, so a chunking regression fails here on its own terms instead
        // of only as a surprising total call count.
        Assert.All(nodesPerBrowseCall, nodeCount => Assert.True(nodeCount <= 2));
    }

    /// <summary>
    /// Multi-level tree with mixed node types:
    ///   Plant (root Object)
    ///   ├── Sensors (Object, collection via [N] pattern)
    ///   │   ├── Sensors[0] (Object) → Value (Variable, float)
    ///   │   ├── Sensors[1] (Object) → Value (Variable, float)
    ///   │   └── Sensors[2] (Object) → Value (Variable, float)
    ///   ├── Settings (Object, single subject reference)
    ///   │   └── Parameter1 (Variable, int)
    ///   └── Status (Variable, double)
    /// </summary>
    [Fact]
    public async Task WhenLoadingMultiLevelTreeWithMixedNodeTypes_ThenAllMonitoredItemsAndStructureAreCorrect()
    {
        // Arrange: NodeId constants for every node in the tree
        var simulationId = new NodeId(100, 2);
        var sensorsId = new NodeId(200, 2);
        var sensor0Id = new NodeId(201, 2);
        var sensor1Id = new NodeId(202, 2);
        var sensor2Id = new NodeId(203, 2);
        var sensor0ValueId = new NodeId(211, 2);
        var sensor1ValueId = new NodeId(212, 2);
        var sensor2ValueId = new NodeId(213, 2);
        var settingsId = new NodeId(300, 2);
        var parameter1Id = new NodeId(301, 2);
        var statusId = new NodeId(400, 2);

        // Build browse tree: map each NodeId to its children
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [simulationId] =
            [
                CreateObjectReferenceDescription("Sensors", new ExpandedNodeId(sensorsId)),
                CreateObjectReferenceDescription("Settings", new ExpandedNodeId(settingsId)),
                CreateTestReferenceDescription("Status", new ExpandedNodeId(statusId))
            ],
            [sensorsId] =
            [
                CreateObjectReferenceDescription("Sensors[0]", new ExpandedNodeId(sensor0Id)),
                CreateObjectReferenceDescription("Sensors[1]", new ExpandedNodeId(sensor1Id)),
                CreateObjectReferenceDescription("Sensors[2]", new ExpandedNodeId(sensor2Id))
            ],
            [sensor0Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensor0ValueId))],
            [sensor1Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensor1ValueId))],
            [sensor2Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensor2ValueId))],
            [settingsId] = [CreateTestReferenceDescription("Parameter1", new ExpandedNodeId(parameter1Id))]
        };

        // DataType mapping for Variable nodes
        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [sensor0ValueId] = (DataTypeIds.Float, -1),
            [sensor1ValueId] = (DataTypeIds.Float, -1),
            [sensor2ValueId] = (DataTypeIds.Float, -1),
            [parameter1Id] = (DataTypeIds.Int32, -1),
            [statusId] = (DataTypeIds.Double, -1)
        };

        var mockSession = CreateMockSession();

        // Mock BrowseAsync: return children for known nodes, empty for others (leaf Variables).
        // Handles multi-node BrowseDescriptionCollections from BrowseManyNodesAsync.
        SetupBrowseAsync(mockSession, browseTree);

        // Mock ReadAsync: return DataType + ValueRank for Variable nodes
        SetupReadAsync(mockSession, dataTypes);

        var (loader, ownership, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateObjectReferenceDescription("Plant", new ExpandedNodeId(simulationId));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: structure
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Contains("Sensors", propertyNames);
        Assert.Contains("Settings", propertyNames);
        Assert.Contains("Status", propertyNames);

        // Assert: Sensors is a collection with 3 items
        var sensorsProperty = registeredSubject.Properties.Single(p => p.Name == "Sensors");
        Assert.Equal(typeof(DynamicSubject[]), sensorsProperty.Type);
        var collection = sensorsProperty.GetValue() as DynamicSubject[];
        Assert.NotNull(collection);
        Assert.Equal(3, collection.Length);

        // Assert: each collection item has a Value property of type float
        foreach (var item in collection)
        {
            var itemRegistered = item.TryGetRegisteredSubject()!;
            var valueProperty = itemRegistered.Properties.Single(p => p.Name == "Value");
            Assert.Equal(typeof(float), valueProperty.Type);
        }

        // Assert: Settings is a single subject reference with Parameter1
        var settingsProperty = registeredSubject.Properties.Single(p => p.Name == "Settings");
        Assert.Equal(typeof(DynamicSubject), settingsProperty.Type);
        var settingsSubject = settingsProperty.GetValue() as IInterceptorSubject;
        Assert.NotNull(settingsSubject);
        var settingsRegistered = settingsSubject.TryGetRegisteredSubject()!;
        var param1 = settingsRegistered.Properties.Single(p => p.Name == "Parameter1");
        Assert.Equal(typeof(int), param1.Type);

        // Assert: Status is a scalar double
        var statusProperty = registeredSubject.Properties.Single(p => p.Name == "Status");
        Assert.Equal(typeof(double), statusProperty.Type);

        // Assert: monitored items count (3 Sensor Values + Parameter1 + Status = 5)
        Assert.Equal(5, monitoredItems.Count);

        // Assert: monitored items point to the correct NodeIds
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(sensor0ValueId, monitoredNodeIds);
        Assert.Contains(sensor1ValueId, monitoredNodeIds);
        Assert.Contains(sensor2ValueId, monitoredNodeIds);
        Assert.Contains(parameter1Id, monitoredNodeIds);
        Assert.Contains(statusId, monitoredNodeIds);

        // Assert: all monitored item properties are owned by the source
        Assert.Equal(5, ownership.Properties.Count());

        // Assert: one browse call per level plus one per attribute round, five in total.
        // Call 1: the root, Plant.
        // Call 2: the object-type browse of Plant's dynamic Object children, Sensors and Settings,
        //         which also fills the browse cache for both.
        // Call 3: the next level in one call. Sensors[0..2] and Settings are its subjects; Settings
        //         is served from the cache, so the request carries the three sensors.
        // Call 4: the attribute browse of that level's four value variables (three Values and
        //         Parameter1).
        // Call 5: the attribute browse of Plant's own value variable, Status. The attribute phase is
        //         the last phase of a level, so it runs after the recursion.
        // The container browse of Sensors is a cache hit from call 2. A loader that recursed once
        // per branch kind browsed the sensors and Settings in separate passes and needed six calls.
        var browseCallCount = mockSession.Invocations.Count(invocation => invocation.Method.Name == nameof(ISession.BrowseAsync));
        Assert.Equal(5, browseCallCount);
    }

    /// <summary>
    /// Every subject on three levels carries both a collection and a subject reference, so the
    /// tree forks into both branch kinds at every level:
    ///   Root (Object)
    ///   ├── Items (Object, collection) → Item[0] (Object) → { Items, Child } again
    ///   └── Child (Object, subject reference) → { Items, Child } again
    /// The eight subjects on the fourth level each carry one Value variable.
    /// </summary>
    [Fact]
    public async Task WhenEveryLevelCarriesACollectionAndASubjectReference_ThenBrowseCallCountGrowsWithDepthNotWithBranches()
    {
        // Arrange
        var rootId = new NodeId(1, 0);
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>();
        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>();
        var nextIdentifier = 1000u;

        NodeId NextNodeId() => new(nextIdentifier++, 2);

        void AddLevels(NodeId subjectId, int remainingContainerLevels)
        {
            if (remainingContainerLevels == 0)
            {
                var valueId = NextNodeId();
                browseTree[subjectId] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(valueId))];
                dataTypes[valueId] = (DataTypeIds.Float, -1);
                return;
            }

            var itemsId = NextNodeId();
            var itemId = NextNodeId();
            var childId = NextNodeId();
            browseTree[subjectId] =
            [
                CreateObjectReferenceDescription("Items", new ExpandedNodeId(itemsId)),
                CreateObjectReferenceDescription("Child", new ExpandedNodeId(childId))
            ];
            browseTree[itemsId] = [CreateObjectReferenceDescription("Item[0]", new ExpandedNodeId(itemId))];
            AddLevels(itemId, remainingContainerLevels - 1);
            AddLevels(childId, remainingContainerLevels - 1);
        }

        AddLevels(rootId, 3);

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, dataTypes);

        var (loader, ownership, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: every leaf Value is monitored and owned, so the whole tree was loaded.
        Assert.Equal(8, monitoredItems.Count);
        Assert.Equal(8, ownership.Properties.Count());
        Assert.Equal(dataTypes.Keys.ToHashSet(), monitoredItems.Select(item => item.StartNodeId).ToHashSet());

        // Assert: two browse calls per container level, then two for the leaf level, eight in total.
        // Each container level costs the browse of its subjects and the object-type browse of their
        // dynamic Object children. The object-type browse also fills the browse cache, so the
        // container browse of every Items node is a cache hit and every Child subject reference is
        // served from the cache when its level is browsed: a level's subject browse carries only the
        // Item[0] subjects, however many collections and references led to the level.
        // Level 0: Root (1 node), then Items and Child (2 nodes).
        // Level 1: Item[0] (1 node), then the 4 dynamic Object children of Item[0] and Child.
        // Level 2: 2 Item[0] nodes, then 8 dynamic Object children.
        // Level 3: 4 Item[0] nodes, then the attribute browse of the 8 Value variables.
        // A loader that recursed separately below collections and below subject references browsed
        // each branch's subtree in its own pass: with C(d) the calls for an uncached subject with d
        // container levels below it and K(d) for a cached one, C(0) = 2, K(0) = 1,
        // C(d) = 2 + C(d - 1) + K(d - 1) and K(d) = 1 + C(d - 1) + K(d - 1), which is 23 calls here.
        var nodesPerBrowseCall = mockSession.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ISession.BrowseAsync))
            .Select(invocation => ((BrowseDescriptionCollection)invocation.Arguments[3]).Count)
            .ToArray();
        Assert.Equal([1, 2, 1, 4, 2, 8, 4, 8], nodesPerBrowseCall);
    }

    [Fact]
    public async Task WhenBrowseReturnsContinuationPoints_ThenAllReferencesAreCollected()
    {
        // Arrange: the root node has 3 children but the server returns them across
        // two pages (initial browse returns 2 + continuation point, BrowseNext returns 1).
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var var3Id = new NodeId(2003, 2);

        var continuationToken = new byte[] { 0xCA, 0xFE };

        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    if (desc.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References =
                            [
                                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
                            ],
                            ContinuationPoint = continuationToken
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        References = [CreateTestReferenceDescription("Var3", new ExpandedNodeId(var3Id))]
                    }
                ],
                DiagnosticInfos = []
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [var3Id] = (DataTypeIds.Int32, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: all 3 variables discovered (2 from initial + 1 from continuation)
        Assert.Equal(3, monitoredItems.Count);
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(var3Id, monitoredNodeIds);
    }

    [Fact]
    public async Task WhenBrowseNextRejectsBatch_ThenRetriesWithSmallerBatches()
    {
        // Arrange: two dynamic variable nodes each have a continuation point on initial
        // browse (phase 5 / LoadAttributesAsync). The server rejects any
        // BrowseNextAsync call with more than 1 continuation point, forcing a split-and-retry.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var attr1Id = new NodeId(3001, 2);
        var attr2Id = new NodeId(3002, 2);

        var continuationToken1 = new byte[] { 0x01 };
        var continuationToken2 = new byte[] { 0x02 };

        var mockSession = CreateMockSession();

        // BrowseAsync: root returns Var1+Var2; Var1 and Var2 each return no immediate
        // children but carry a continuation point that yields one attribute child each.
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    if (desc.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References =
                            [
                                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
                            ]
                        });
                    }
                    else if (desc.NodeId == var1Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = [],
                            ContinuationPoint = continuationToken1
                        });
                    }
                    else if (desc.NodeId == var2Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = [],
                            ContinuationPoint = continuationToken2
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // BrowseNextAsync: reject batches > 1, return one attribute child per variable
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                if (continuationPoints.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new BrowseResultCollection();
                foreach (var cp in continuationPoints)
                {
                    if (cp.SequenceEqual(continuationToken1))
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr1", new ExpandedNodeId(attr1Id))]
                        });
                    }
                    else if (cp.SequenceEqual(continuationToken2))
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr2", new ExpandedNodeId(attr2Id))]
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseNextResponse { Results = results, DiagnosticInfos = [] };
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [attr1Id] = (DataTypeIds.Int32, -1),
            [attr2Id] = (DataTypeIds.String, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: all 4 nodes monitored (2 variables + 2 attribute children from continuation)
        Assert.Equal(4, monitoredItems.Count);
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(attr1Id, monitoredNodeIds);
        Assert.Contains(attr2Id, monitoredNodeIds);
    }

    [Fact]
    public async Task WhenObjectChildrenHaveStringBracketNames_ThenDictionaryPropertyIsCreated()
    {
        // Arrange: an Object node's children use bracket-string naming (e.g., "Device[SensorA]")
        // which classifies the parent as IReadOnlyDictionary<string, DynamicSubject>.
        var devicesId = new NodeId(300, 2);
        var sensorAId = new NodeId(301, 2);
        var sensorBId = new NodeId(302, 2);
        var sensorAValueId = new NodeId(311, 2);
        var sensorBValueId = new NodeId(312, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateObjectReferenceDescription("Devices", new ExpandedNodeId(devicesId))
            ],
            [devicesId] =
            [
                CreateObjectReferenceDescription("Device[SensorA]", new ExpandedNodeId(sensorAId)),
                CreateObjectReferenceDescription("Device[SensorB]", new ExpandedNodeId(sensorBId))
            ],
            [sensorAId] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensorAValueId))],
            [sensorBId] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensorBValueId))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [sensorAValueId] = (DataTypeIds.Float, -1),
            [sensorBValueId] = (DataTypeIds.Float, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Devices is a dictionary with 2 entries
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var devicesProperty = registeredSubject.Properties.Single(p => p.Name == "Devices");
        Assert.Equal(typeof(IReadOnlyDictionary<string, DynamicSubject>), devicesProperty.Type);

        var dictionary = devicesProperty.GetValue() as IReadOnlyDictionary<string, DynamicSubject>;
        Assert.NotNull(dictionary);
        Assert.Equal(2, dictionary.Count);
        Assert.True(dictionary.ContainsKey("SensorA"));
        Assert.True(dictionary.ContainsKey("SensorB"));

        // Assert: each entry has a Value property
        foreach (var entry in dictionary.Values)
        {
            var entryRegistered = entry.TryGetRegisteredSubject()!;
            var valueProperty = entryRegistered.Properties.Single(p => p.Name == "Value");
            Assert.Equal(typeof(float), valueProperty.Type);
        }

        // Assert: 2 monitored items (one per sensor Value)
        Assert.Equal(2, monitoredItems.Count);
    }

    [Fact]
    public async Task WhenBrowseNextSpansMultipleRoundsAndOneRoundIsRejected_ThenAllReferencesAreCollected()
    {
        // Arrange: two top-level variables each return a continuation point on initial
        // browse. BrowseNext round 1 returns refs AND fresh continuation points (so a
        // round 2 is needed). Round 2 is rejected with BadTooManyOperations when more
        // than one continuation point is sent, forcing a split-and-retry. This exercises
        // the multi-round path of ProcessContinuationPointsAsync together with the
        // recursive split inside BrowseNextBatchAsync. It pins the aliased-buffer
        // contract: the same `pending` list is read from and appended to across rounds
        // and across recursive splits without corrupting in-flight indices.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var attr1aId = new NodeId(3001, 2);
        var attr2aId = new NodeId(3002, 2);
        var attr1bId = new NodeId(3003, 2);
        var attr2bId = new NodeId(3004, 2);

        var cp1Round1 = new byte[] { 0x10 };
        var cp2Round1 = new byte[] { 0x20 };
        var cp1Round2 = new byte[] { 0x11 };
        var cp2Round2 = new byte[] { 0x21 };

        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    if (desc.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References =
                            [
                                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
                            ]
                        });
                    }
                    else if (desc.NodeId == var1Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = [],
                            ContinuationPoint = cp1Round1
                        });
                    }
                    else if (desc.NodeId == var2Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = [],
                            ContinuationPoint = cp2Round1
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                // Round 2 (the cpXRound2 batch) is rejected when more than one CP is sent.
                var isRound2 = continuationPoints.Any(cp => cp.SequenceEqual(cp1Round2) || cp.SequenceEqual(cp2Round2));
                if (isRound2 && continuationPoints.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new BrowseResultCollection();
                foreach (var cp in continuationPoints)
                {
                    if (cp.SequenceEqual(cp1Round1))
                    {
                        // Round 1 result for Var1: emit one ref AND a fresh CP for round 2.
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr1a", new ExpandedNodeId(attr1aId))],
                            ContinuationPoint = cp1Round2
                        });
                    }
                    else if (cp.SequenceEqual(cp2Round1))
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr2a", new ExpandedNodeId(attr2aId))],
                            ContinuationPoint = cp2Round2
                        });
                    }
                    else if (cp.SequenceEqual(cp1Round2))
                    {
                        // Round 2 result for Var1 (single-item batch after split): one ref, no further CP.
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr1b", new ExpandedNodeId(attr1bId))]
                        });
                    }
                    else if (cp.SequenceEqual(cp2Round2))
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr2b", new ExpandedNodeId(attr2bId))]
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseNextResponse { Results = results, DiagnosticInfos = [] };
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [attr1aId] = (DataTypeIds.Int32, -1),
            [attr2aId] = (DataTypeIds.Int32, -1),
            [attr1bId] = (DataTypeIds.Int32, -1),
            [attr2bId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _, subject) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: every variable and every attribute across both BrowseNext rounds reached
        // the loader despite the round-2 split-and-retry.
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(attr1aId, monitoredNodeIds);
        Assert.Contains(attr2aId, monitoredNodeIds);
        Assert.Contains(attr1bId, monitoredNodeIds);
        Assert.Contains(attr2bId, monitoredNodeIds);
        Assert.Equal(6, monitoredItems.Count);
    }
}
