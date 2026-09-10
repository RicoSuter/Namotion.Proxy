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

/// <summary>
/// Tests for matching a configured RootPath segment against browse results, which run on
/// raw references that are not filtered through DistinctByResolvedNodeId. A server may return
/// a reference with a missing BrowseName, so the match must tolerate null BrowseNames.
/// </summary>
public class OpcUaRootPathResolutionTests
{
    [Fact]
    public void WhenAReferenceHasNullBrowseName_ThenItIsSkippedAndTheMatchingChildIsFound()
    {
        // Arrange
        var references = new ReferenceDescriptionCollection
        {
            new ReferenceDescription { NodeId = new ExpandedNodeId(new NodeId(1, 0)) }, // null BrowseName
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Machines"),
                NodeId = new ExpandedNodeId(new NodeId(2, 0))
            }
        };

        // Act
        var match = OpcUaSubjectClientSource.FindChildByBrowseName(references, "Machines");

        // Assert
        Assert.NotNull(match);
        Assert.Equal("Machines", match.BrowseName.Name);
    }

    [Fact]
    public void WhenNoReferenceMatches_ThenReturnsNull()
    {
        // Arrange
        var references = new ReferenceDescriptionCollection
        {
            new ReferenceDescription { NodeId = new ExpandedNodeId(new NodeId(1, 0)) }, // null BrowseName
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Other"),
                NodeId = new ExpandedNodeId(new NodeId(2, 0))
            }
        };

        // Act
        var match = OpcUaSubjectClientSource.FindChildByBrowseName(references, "Machines");

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public async Task WhenIntermediateRootPathSegmentHasUnregisteredNamespace_ThenReturnsNullInsteadOfThrowing()
    {
        // Arrange: a two-segment RootPath. Browsing the Objects folder returns a "Machines"
        // reference whose ExpandedNodeId carries a namespace URI that is NOT registered in the
        // session's NamespaceTable, so ExpandedNodeId.ToNodeId(...) resolves to null. The resolver
        // must return null (so the load logs "could not find root node" and retries cleanly)
        // instead of browsing a null NodeId, which throws ArgumentNullException deep in the
        // browse primitive. This is the symmetric companion to the null-BrowseName tolerance above.
        var source = CreateSource(rootPath: ["Machines", "Pumps"]);

        var mockSession = new Mock<ISession>();
        var namespaceTable = new NamespaceTable();
        namespaceTable.Append("urn:test"); // "urn:vendor:unregistered" intentionally NOT registered
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
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
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription
                            {
                                BrowseName = new QualifiedName("Machines"),
                                NodeId = new ExpandedNodeId("MachinesFolder", "urn:vendor:unregistered")
                            }
                        ]
                    }
                ],
                DiagnosticInfos = []
            });

        // Act
        var result = await source.TryGetRootNodeAsync(mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    private static OpcUaSubjectClientSource CreateSource(string[] rootPath)
    {
        var configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            RootPath = rootPath,
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };

        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var subject = new DynamicSubject(context);
        return new OpcUaSubjectClientSource(subject, configuration, NullLogger<OpcUaSubjectClientSource>.Instance);
    }
}
