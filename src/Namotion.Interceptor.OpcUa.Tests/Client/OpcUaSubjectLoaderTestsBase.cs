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
        int? maxAttributeTraversals = null)
    {
        var subject = new DynamicSubject(CreateSubjectContext());
        var (loader, ownership, _) = CreateLoaderFor(
            subject,
            shouldAddDynamicProperties,
            shouldAddDynamicAttributes,
            typeResolver,
            maxAttributeTraversals);

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
        int? maxAttributeTraversals = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = BaseConfiguration.ServerUrl,
            TypeResolver = typeResolver ?? BaseConfiguration.TypeResolver,
            ValueConverter = BaseConfiguration.ValueConverter,
            SubjectFactory = BaseConfiguration.SubjectFactory,
            ShouldAddDynamicProperty = shouldAddDynamicProperties ?? BaseConfiguration.ShouldAddDynamicProperty,
            ShouldAddDynamicAttribute = shouldAddDynamicAttributes,
            DefaultNamespaceUri = BaseConfiguration.DefaultNamespaceUri,
            MaxAttributeTraversals = maxAttributeTraversals ?? BaseConfiguration.MaxAttributeTraversals
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
    /// Sets up ReadAsync on a mock session to return DataType + ValueRank for given node-to-DataTypeId mappings.
    /// Handles both single-node and batch ReadAsync calls.
    /// </summary>
    private protected static void SetupReadAsync(Mock<ISession> mockSession, Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)> dataTypes)
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

    /// <summary>
    /// Sets up BrowseAsync on a mock session to dispatch per NodeId, handling both single-node
    /// and multi-node BrowseDescriptionCollections (as used by BrowseManyNodesAsync).
    /// </summary>
    private protected static void SetupBrowseAsync(Mock<ISession> mockSession, Dictionary<NodeId, ReferenceDescription[]> browseTree)
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
                foreach (var desc in browseDescriptions)
                {
                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        children.AddRange(refs);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
    }

    /// <summary>
    /// Like <see cref="SetupBrowseAsync"/> but also records every browsed NodeId, so tests
    /// can assert that specific subtrees were (not) visited.
    /// </summary>
    private protected static HashSet<NodeId> SetupBrowseAsyncWithTracking(Mock<ISession> mockSession, Dictionary<NodeId, ReferenceDescription[]> browseTree)
    {
        var browsedNodeIds = new HashSet<NodeId>();
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
                    browsedNodeIds.Add(desc.NodeId);
                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        children.AddRange(refs);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
        return browsedNodeIds;
    }

    private protected static Mock<ISession> CreateMockSessionWithNoChildren()
    {
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult { References = [] }
                ],
                DiagnosticInfos = []
            });

        return mockSession;
    }

    private protected static Mock<ISession> CreateMockSessionWithChildren(ReferenceDescription[] children)
    {
        var mockSession = CreateMockSession();
        var childCollection = new ReferenceDescriptionCollection();
        childCollection.AddRange(children);

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult { References = childCollection }
                ],
                DiagnosticInfos = []
            });

        return mockSession;
    }
}
