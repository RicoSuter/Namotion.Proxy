using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaTypeResolverTests
{
    private static readonly ReferenceDescription ObjectNode = new()
    {
        BrowseName = new QualifiedName("Parent"),
        NodeClass = NodeClass.Object
    };

    private readonly OpcUaTypeResolver _resolver;

    public OpcUaTypeResolverTests()
    {
        _resolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance);
    }

    [Fact]
    public void WhenObjectChildrenHaveBracketIntNames_ThenClassifiesAsCollection()
    {
        // Arrange
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Item[0]"),
                NodeClass = NodeClass.Object
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(ObjectNode, children);

        // Assert
        Assert.Equal(typeof(DynamicSubject[]), result);
    }

    [Fact]
    public void WhenObjectChildrenHaveBracketStringNames_ThenClassifiesAsDictionary()
    {
        // Arrange
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Device[SensorA]"),
                NodeClass = NodeClass.Object
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(ObjectNode, children);

        // Assert
        Assert.Equal(typeof(IReadOnlyDictionary<string, DynamicSubject>), result);
    }

    [Fact]
    public void WhenObjectChildrenHaveRegularNames_ThenClassifiesAsSubject()
    {
        // Arrange
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Temperature"),
                NodeClass = NodeClass.Variable
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(ObjectNode, children);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public void WhenObjectHasNoChildren_ThenClassifiesAsSubject()
    {
        // Act
        var result = _resolver.ResolveObjectNodeType(ObjectNode, new ReferenceDescriptionCollection());

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public void WhenObjectChildHasEmptyBrackets_ThenClassifiesAsSubject()
    {
        // Arrange: a `Name[]` browse name carries no key or index information; without
        // this branch the empty content would fall through to the dictionary classification.
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Item[]"),
                NodeClass = NodeClass.Object
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(ObjectNode, children);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public async Task WhenResolvingMultipleVariables_ThenBatchReadsAndMapsTypes()
    {
        // Arrange
        var node1Id = new NodeId(5001, 2);
        var node2Id = new NodeId(5002, 2);
        var node3Id = new NodeId(5003, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Name"), NodeId = new ExpandedNodeId(node3Id), NodeClass = NodeClass.Variable },
        };

        var mockSession = CreateMockSession();
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [node1Id] = (DataTypeIds.Float, -1),
            [node2Id] = (DataTypeIds.Int32, -1),
            [node3Id] = (DataTypeIds.String, -1)
        });

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal(typeof(float), result[node1Id]);
        Assert.Equal(typeof(int), result[node2Id]);
        Assert.Equal(typeof(string), result[node3Id]);
    }

    [Fact]
    public async Task WhenVariableHasCustomDataTypeSubtype_ThenWalksTypeTreeToBuiltInType()
    {
        // Arrange: a Variable whose DataType is a custom NodeId outside the built-in range.
        // The session's TypeTree walks the custom DataType up to the well-known Double DataType.
        var customDataTypeId = new NodeId(9001, 2);
        var variableNodeId = new NodeId(2001, 2);

        var mockTypeTable = new Mock<ITypeTable>();
        mockTypeTable
            .Setup(t => t.FindSuperTypeAsync(customDataTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataTypeIds.Double);

        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.TypeTree).Returns(mockTypeTable.Object);

        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results =
                [
                    new DataValue { Value = customDataTypeId, StatusCode = StatusCodes.Good },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                ],
                DiagnosticInfos = []
            });

        var variables = new List<ReferenceDescription>
        {
            new()
            {
                BrowseName = new QualifiedName("Temperature"),
                NodeId = new ExpandedNodeId(variableNodeId),
                NodeClass = NodeClass.Variable
            }
        };

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(double), result[variableNodeId]);
        mockTypeTable.Verify(
            t => t.FindSuperTypeAsync(customDataTypeId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task WhenSessionExtensionPadsShortRead_ThenResolverPreservesAlignment()
    {
        // Arrange: two variables (= 4 ReadValueIds), but the server returns only the
        // first 2 slots. ReadNodesAsync pads the missing slots with BadUnexpectedError;
        // the resolver classifies that transient status and throws an
        // OpcUaTransientServiceException attributed to node2 (proving alignment held).
        var node1Id = new NodeId(7001, 2);
        var node2Id = new NodeId(7002, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
        };

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results =
                [
                    new DataValue { Value = DataTypeIds.Float, StatusCode = StatusCodes.Good },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                    // server omits the 2 trailing slots for node2
                ],
                DiagnosticInfos = []
            });

        // Act & Assert: alignment is verified by the exception attributing the
        // padded transient slot to node2's NodeId (not node1's, not a phantom).
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None));

        Assert.Equal("Read", exception.Operation);
        Assert.Equal(node2Id, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadUnexpectedError, exception.StatusCode);
    }

    [Fact]
    public async Task WhenDataTypeReadReturnsTransientBadStatus_ThenAbortsByThrowing()
    {
        // Arrange: a transient DataType read would drop the property from the model, so
        // the resolver must abort (throw) rather than build a partial model.
        var variableNodeId = new NodeId(7101, 2);
        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(variableNodeId), NodeClass = NodeClass.Variable }
        };

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results =
                [
                    new DataValue { StatusCode = StatusCodes.BadServerNotConnected },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None));

        Assert.Equal("Read", exception.Operation);
        Assert.Equal(variableNodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadServerNotConnected, exception.StatusCode);
    }

    [Fact]
    public async Task WhenDataTypeReadReturnsPermanentBadStatus_ThenSkipsPropertyWithoutThrowing()
    {
        // Arrange: a permanent DataType read cannot improve on retry, so the resolver must
        // not abort; it leaves the type unresolved (null) for the loader to skip that property.
        var variableNodeId = new NodeId(7201, 2);
        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(variableNodeId), NodeClass = NodeClass.Variable }
        };

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results =
                [
                    new DataValue { StatusCode = StatusCodes.BadUserAccessDenied },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                ],
                DiagnosticInfos = []
            });

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert: no throw; the node is present with an unresolved (null) type.
        Assert.True(result.ContainsKey(variableNodeId));
        Assert.Null(result[variableNodeId]);
    }

    [Fact]
    public async Task WhenServerRejectsReadBatch_ThenRetriesWithSmallerBatches()
    {
        // Arrange: server rejects any ReadAsync call with more than 2 ReadValueIds
        var node1Id = new NodeId(6001, 2);
        var node2Id = new NodeId(6002, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
        };

        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [node1Id] = (DataTypeIds.Float, -1),
            [node2Id] = (DataTypeIds.Int32, -1)
        };

        var mockSession = CreateMockSession();
        var readCallCount = 0;

        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                Interlocked.Increment(ref readCallCount);
                // Reject batches larger than 2 ReadValueIds (= 1 variable with DataType + ValueRank)
                if (nodesToRead.Count > 2)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new DataValueCollection();
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    var nodeId = nodesToRead[i].NodeId;
                    if (dataTypes.TryGetValue(nodeId, out var dt))
                    {
                        results.Add(new DataValue { Value = dt.DataTypeId, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = dt.ValueRank, StatusCode = StatusCodes.Good });
                    }
                    else
                    {
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                    }
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert: both variables resolved despite batch rejection
        Assert.Equal(2, result.Count);
        Assert.Equal(typeof(float), result[node1Id]);
        Assert.Equal(typeof(int), result[node2Id]);

        // Assert: the rejected call of four ReadValueIds, then one accepted call per half. A
        // regression that swallowed the rejection and returned partial data could still satisfy
        // the Count == 2 check without retrying; the exact call count closes that gap.
        Assert.Equal(3, readCallCount);
    }

    [Fact]
    public async Task WhenOnlyThePerNodeHookIsOverridden_ThenBatchingAndBaseMappingStillApply()
    {
        // Arrange: three variables the server types identically, one of which the subclass
        // re-types by browse name. Overriding the per-node hook must not cost the batching.
        var node1Id = new NodeId(7001, 2);
        var node2Id = new NodeId(7002, 2);
        var node3Id = new NodeId(7003, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Timestamp"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node3Id), NodeClass = NodeClass.Variable },
        };

        var readCallCount = 0;
        var submittedNodeCounts = new List<int>();
        var mockSession = CreateMockSession();
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [node1Id] = (DataTypeIds.Float, -1),
            [node2Id] = (DataTypeIds.Int64, -1),
            [node3Id] = (DataTypeIds.Int32, -1)
        });
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .Callback((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                readCallCount++;
                submittedNodeCounts.Add(nodesToRead.Count);
            })
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                var results = new DataValueCollection();
                foreach (var read in nodesToRead)
                {
                    var isDataType = read.AttributeId == Opc.Ua.Attributes.DataType;
                    var dataTypeId = read.NodeId == node1Id ? DataTypeIds.Float
                        : read.NodeId == node2Id ? DataTypeIds.Int64
                        : DataTypeIds.Int32;
                    results.Add(isDataType
                        ? new DataValue { Value = dataTypeId, StatusCode = StatusCodes.Good }
                        : new DataValue { Value = -1, StatusCode = StatusCodes.Good });
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });

        var resolver = new BrowseNameTypeResolver();

        // Act
        var result = await resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert: the override decides its own node and the base mapping decides the rest.
        Assert.Equal(typeof(DateTimeOffset), result[node2Id]);
        Assert.Equal(typeof(float), result[node1Id]);
        Assert.Equal(typeof(int), result[node3Id]);

        // Assert: still one read of all six attributes, so the override did not fall back to
        // per-node reads. The hook saw every node, not only the one it re-typed.
        Assert.Equal(1, readCallCount);
        Assert.Equal([6], submittedNodeCounts);
        Assert.Equal(["Temp", "Timestamp", "Count"], resolver.SeenBrowseNames);
    }

    private sealed class BrowseNameTypeResolver() : OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance)
    {
        private readonly List<string> _seenBrowseNames = [];

        public IReadOnlyList<string> SeenBrowseNames => _seenBrowseNames;

        protected override Task<Type?> ResolveVariableTypeAsync(
            OpcUaVariableTypeContext node,
            CancellationToken cancellationToken)
        {
            _seenBrowseNames.Add(node.Reference.BrowseName.Name);
            return node.Reference.BrowseName.Name == "Timestamp"
                ? Task.FromResult<Type?>(typeof(DateTimeOffset))
                : base.ResolveVariableTypeAsync(node, cancellationToken);
        }
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        mockSession.SetupGet(s => s.NamespaceUris).Returns(new NamespaceTable());
        mockSession.SetupGet(s => s.TypeTree).Returns(new Mock<ITypeTable>().Object);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        return mockSession;
    }

    private static void SetupReadAsync(Mock<ISession> mockSession, Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)> dataTypes)
    {
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                var results = new DataValueCollection();
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    var nodeId = nodesToRead[i].NodeId;
                    if (dataTypes.TryGetValue(nodeId, out var dt))
                    {
                        results.Add(new DataValue { Value = dt.DataTypeId, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = dt.ValueRank, StatusCode = StatusCodes.Good });
                    }
                    else
                    {
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                    }
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });
    }
}
