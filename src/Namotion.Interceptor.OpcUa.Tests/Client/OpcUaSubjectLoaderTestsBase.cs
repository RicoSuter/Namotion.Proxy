using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderTestsBase
{
    private protected readonly OpcUaClientConfiguration BaseConfiguration;

    private protected OpcUaSubjectLoaderTestsBase()
    {
        BaseConfiguration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false)
        };
    }

    /// <summary>
    /// Creates the subject the test loads together with the source and loader rooted on it, which
    /// is the shape production uses: <c>OpcUaSubjectClientSource</c> builds its loader over its own
    /// root subject. That matters beyond tidiness. <c>SourceOwnershipManager</c> subscribes to the
    /// <c>LifecycleInterceptor</c> reachable from the source's root subject, so a fixture that
    /// loaded a different subject on a different context would wire the detach callback to a
    /// lifecycle interceptor the loaded graph never touches, and every subject-detach path would go
    /// untested.
    /// </summary>
    private protected (OpcUaSubjectLoader Loader, SourceOwnershipManager Ownership, IInterceptorSubject Subject) CreateLoader(
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicProperties = null,
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicAttributes = null,
        OpcUaTypeResolver? typeResolver = null,
        int? maxAttributeTraversalDepth = null)
    {
        var subject = new DynamicSubject(CreateSubjectContext());
        var (loader, ownership, _) = CreateLoaderFor(
            subject,
            shouldAddDynamicProperties,
            shouldAddDynamicAttributes,
            typeResolver,
            maxAttributeTraversalDepth);

        return (loader, ownership, subject);
    }

    /// <summary>
    /// Builds the source and its loader over an already created subject, for tests that need a
    /// statically modelled root instead of a <see cref="DynamicSubject"/>. The subject must live on
    /// a context created by <see cref="CreateSubjectContext"/> so the source sees the same
    /// lifecycle interceptor as the loaded graph.
    /// </summary>
    private protected (OpcUaSubjectLoader Loader, SourceOwnershipManager Ownership, OpcUaSubjectClientSource Source) CreateLoaderFor(
        IInterceptorSubject subject,
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicProperties = null,
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicAttributes = null,
        OpcUaTypeResolver? typeResolver = null,
        int? maxAttributeTraversalDepth = null,
        OpcUaSubjectFactory? subjectFactory = null,
        int? maxBrowseContinuationRounds = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = BaseConfiguration.ServerUrl,
            TypeResolver = typeResolver ?? BaseConfiguration.TypeResolver,
            ValueConverter = BaseConfiguration.ValueConverter,
            SubjectFactory = subjectFactory ?? BaseConfiguration.SubjectFactory,
            ShouldAddDynamicProperty = shouldAddDynamicProperties ?? BaseConfiguration.ShouldAddDynamicProperty,
            ShouldAddDynamicAttribute = shouldAddDynamicAttributes,
            DefaultNamespaceUri = BaseConfiguration.DefaultNamespaceUri,
            MaxAttributeTraversalDepth = maxAttributeTraversalDepth ?? BaseConfiguration.MaxAttributeTraversalDepth,
            MaxBrowseContinuationRounds = maxBrowseContinuationRounds ?? BaseConfiguration.MaxBrowseContinuationRounds
        };

        var source = new OpcUaSubjectClientSource(subject, config, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(
            subject,
            config,
            source.Ownership,
            source,
            NullLogger<OpcUaSubjectClientSource>.Instance);
        return (loader, source.Ownership, source);
    }

    /// <summary>
    /// Creates the context that loaded subjects live on. <c>WithLifecycle</c> attaches the subject
    /// through the context's own lifecycle interceptor, which is the one the source subscribes to.
    /// </summary>
    private protected static IInterceptorSubjectContext CreateSubjectContext()
    {
        return InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
    }

    private protected static ReferenceDescription CreateTestReferenceDescription(string name, NodeId nodeId)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = new ExpandedNodeId(nodeId),
            NodeClass = NodeClass.Variable
        };
    }

    private protected static ReferenceDescription CreateTestReferenceDescription(string name, ExpandedNodeId nodeId)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = nodeId,
            NodeClass = NodeClass.Variable
        };
    }

    private protected static ReferenceDescription CreateObjectReferenceDescription(string name, NodeId nodeId)
    {
        return CreateObjectReferenceDescription(name, new ExpandedNodeId(nodeId));
    }

    private protected static ReferenceDescription CreateObjectReferenceDescription(string name, ExpandedNodeId nodeId)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = nodeId,
            NodeClass = NodeClass.Object
        };
    }

    private protected static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        var namespaceTable = new NamespaceTable();
        // Register the test namespace so that ExpandedNodeId("...", "urn:test") resolves
        // through the session's NamespaceUris. Production servers register their
        // namespaces with the client session at handshake time; an empty NamespaceTable
        // would cause every ExpandedNodeId carrying a NamespaceUri to resolve to null.
        namespaceTable.Append("urn:test");
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        mockSession.SetupGet(s => s.TypeTree).Returns(new Mock<ITypeTable>().Object);
        return mockSession;
    }

    /// <summary>
    /// Sets up ReadAsync to answer the DataType and ValueRank pair of every node in
    /// <paramref name="dataTypes"/>, whatever the batch size, and <c>BadNodeIdUnknown</c> for any
    /// other node. A node for which <paramref name="failWhile"/> returns true at call time is
    /// answered with <paramref name="failStatusCode"/> instead.
    /// </summary>
    private protected static void SetupReadAsync(
        Mock<ISession> mockSession,
        Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)> dataTypes,
        Func<NodeId, bool>? failWhile = null,
        uint failStatusCode = StatusCodes.BadServerHalted)
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
                // ReadValueIds come in pairs: DataType + ValueRank per node
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    var nodeId = nodesToRead[i].NodeId;
                    if (failWhile?.Invoke(nodeId) == true)
                    {
                        results.Add(new DataValue { StatusCode = failStatusCode });
                        results.Add(new DataValue { StatusCode = failStatusCode });
                    }
                    else if (dataTypes.TryGetValue(nodeId, out var dataType))
                    {
                        results.Add(new DataValue { Value = dataType.DataTypeId, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = dataType.ValueRank, StatusCode = StatusCodes.Good });
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

    /// <summary>
    /// Sets up BrowseAsync to answer every requested NodeId with <paramref name="browseNode"/>,
    /// whatever the batch size.
    /// </summary>
    private protected static void SetupBrowseAsync(Mock<ISession> mockSession, Func<NodeId, BrowseResult> browseNode)
    {
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
                foreach (var description in browseDescriptions)
                {
                    results.Add(browseNode(description.NodeId));
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
    }

    /// <summary>
    /// Sets up BrowseAsync to serve <paramref name="browseTree"/>; a NodeId absent from it has no
    /// children. A NodeId for which <paramref name="failWhile"/> returns true at call time is
    /// answered with <paramref name="failStatusCode"/> instead.
    /// </summary>
    private protected static void SetupBrowseAsync(
        Mock<ISession> mockSession,
        Dictionary<NodeId, ReferenceDescription[]> browseTree,
        Func<NodeId, bool>? failWhile = null,
        uint failStatusCode = StatusCodes.BadServerHalted)
    {
        SetupBrowseAsync(mockSession, nodeId => failWhile?.Invoke(nodeId) == true
            ? new BrowseResult { StatusCode = failStatusCode, References = [] }
            : CreateBrowseResult(browseTree, nodeId));
    }

    /// <summary>
    /// Like <see cref="SetupBrowseAsync(Mock{ISession}, Dictionary{NodeId, ReferenceDescription[]}, Func{NodeId, bool}?, uint)"/>,
    /// but also records every browsed NodeId, so tests can assert that specific subtrees were (not) visited.
    /// </summary>
    private protected static HashSet<NodeId> SetupBrowseAsyncWithTracking(Mock<ISession> mockSession, Dictionary<NodeId, ReferenceDescription[]> browseTree)
    {
        var browsedNodeIds = new HashSet<NodeId>();
        SetupBrowseAsync(mockSession, nodeId =>
        {
            browsedNodeIds.Add(nodeId);
            return CreateBrowseResult(browseTree, nodeId);
        });
        return browsedNodeIds;
    }

    /// <summary>
    /// Like <see cref="SetupBrowseAsync(Mock{ISession}, Dictionary{NodeId, ReferenceDescription[]}, Func{NodeId, bool}?, uint)"/>,
    /// except that <paramref name="pagingNodeId"/> never finishes paging: its first page carries a
    /// continuation point and every BrowseNext hands out a fresh one, so the node is omitted from
    /// the load once <c>MaxBrowseContinuationRounds</c> is spent.
    /// </summary>
    private protected static void SetupBrowseAsyncPagingForever(
        Mock<ISession> mockSession,
        Dictionary<NodeId, ReferenceDescription[]> browseTree,
        NodeId pagingNodeId)
    {
        SetupBrowseAsync(mockSession, nodeId =>
        {
            var result = CreateBrowseResult(browseTree, nodeId);
            if (nodeId == pagingNodeId)
            {
                result.ContinuationPoint = [0xFF];
            }
            return result;
        });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool releaseContinuationPoints, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var _ in continuationPoints)
                {
                    results.Add(new BrowseResult
                    {
                        References = [],
                        ContinuationPoint = releaseContinuationPoints ? null : new byte[] { 0xFF }
                    });
                }
                return new BrowseNextResponse { Results = results, DiagnosticInfos = [] };
            });
    }

    private protected static BrowseResult CreateBrowseResult(Dictionary<NodeId, ReferenceDescription[]> browseTree, NodeId nodeId)
    {
        var children = new ReferenceDescriptionCollection();
        if (browseTree.TryGetValue(nodeId, out var references))
        {
            children.AddRange(references);
        }
        return new BrowseResult { References = children };
    }

    /// <summary>
    /// Asserts that <paramref name="monitoredItems"/> covers exactly <paramref name="expectedNodeIds"/>,
    /// that the source claims exactly as many properties, and that every claimed property carries
    /// one of those NodeIds.
    /// </summary>
    private protected static void AssertMonitoredAndClaimed(
        OpcUaSubjectClientSource source,
        IReadOnlyList<MonitoredItem> monitoredItems,
        params NodeId[] expectedNodeIds)
    {
        var expected = expectedNodeIds.ToHashSet();
        Assert.Equal(expectedNodeIds.Length, monitoredItems.Count);
        Assert.Equal(expected, monitoredItems.Select(item => item.StartNodeId).ToHashSet());

        var claimedProperties = source.Ownership.Properties;
        var claimedNodeIds = new HashSet<NodeId>();
        foreach (var property in claimedProperties)
        {
            Assert.True(source.TryGetNodeId(property, out var nodeId), $"{property.Name} is claimed but carries no NodeId.");
            claimedNodeIds.Add(nodeId!);
        }
        Assert.Equal(expectedNodeIds.Length, claimedProperties.Count);
        Assert.Equal(expected, claimedNodeIds);
    }
}
