using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the bad-status handling in <see cref="OpcUaSessionExtensions"/>.
/// Browse aborts on a transient per-NodeId status by throwing
/// <see cref="OpcUaTransientServiceException"/> so the structural graph is never
/// loaded incomplete; permanent statuses are logged and skipped. The read path is
/// a best-effort primitive: it never classifies or throws, returning every result
/// positionally so each caller applies its own policy (value loads keep the good
/// values, type resolution aborts on transient itself).
/// </summary>
public class OpcUaSessionExtensionsTests
{
    [Fact]
    public async Task WhenBrowseReturnsTransientBadStatus_ThenThrowsTransientServiceException()
    {
        // Arrange
        var nodeId = new NodeId(2001, 2);
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
                    new BrowseResult
                    {
                        StatusCode = StatusCodes.BadCommunicationError,
                        References = []
                    }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [nodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadCommunicationError, exception.StatusCode);
    }

    [Fact]
    public async Task WhenBrowseReturnsPermanentBadStatus_ThenSkipsNodeAndContinues()
    {
        // Arrange: NodeId 1 returns BadNodeIdUnknown (permanent), NodeId 2 returns good results.
        // The browse must skip the unknown NodeId and continue with the rest of the batch.
        var unknownNodeId = new NodeId(1001, 2);
        var goodNodeId = new NodeId(1002, 2);
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == unknownNodeId)
                    {
                        results.Add(new BrowseResult
                        {
                            StatusCode = StatusCodes.BadNodeIdUnknown,
                            References = []
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult
                        {
                            References =
                            [
                                new ReferenceDescription { BrowseName = new QualifiedName("Child"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                            ]
                        });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [unknownNodeId, goodNodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert: the permanent-bad NodeId is omitted; the good NodeId is present.
        // Omission (not "present with empty refs") is the contract that lets the cache
        // re-attempt the bad NodeId on the next load.
        Assert.False(result.ContainsKey(unknownNodeId));
        Assert.True(result.ContainsKey(goodNodeId));
        Assert.Single(result[goodNodeId]);
    }

    [Fact]
    public async Task WhenBrowseNextReturnsTransientBadStatus_ThenThrowsTransientServiceException()
    {
        // Arrange: initial browse returns a continuation point; BrowseNext returns a transient bad status.
        var nodeId = new NodeId(1, 0);
        var continuationToken = new byte[] { 0xCA, 0xFE };
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
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("X"), NodeId = new ExpandedNodeId(new NodeId(2001, 2)) }
                        ],
                        ContinuationPoint = continuationToken
                    }
                ],
                DiagnosticInfos = []
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
                        StatusCode = StatusCodes.BadTimeout,
                        References = []
                    }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [nodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("BrowseNext", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadTimeout, exception.StatusCode);
    }

    [Theory]
    [InlineData(StatusCodes.BadServerNotConnected)]
    [InlineData(StatusCodes.BadUserAccessDenied)]
    public async Task WhenReadReturnsBadStatus_ThenPassesResultThroughToCaller(uint statusCode)
    {
        // Arrange: the read path is best-effort and never classifies, so a transient status
        // (BadServerNotConnected) and a permanent one (BadUserAccessDenied) both pass through for
        // the caller to handle per property.
        var nodeId = new NodeId(5001, 2);
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
                Results = [new DataValue { StatusCode = statusCode }],
                DiagnosticInfos = []
            });

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.Value }
        };

        // Act
        var results = await mockSession.Object.ReadNodesAsync(
            nodesToRead,
            TimestampsToReturn.Neither,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Single(results);
        Assert.Equal(statusCode, results[0].StatusCode);
    }

    [Fact]
    public async Task WhenReadMixesGoodAndNotReadyValues_ThenReturnsAllWithoutThrowing()
    {
        // Arrange: regression for one not-ready node cancelling the whole load.
        // BadWaitingForInitialData (a startup status) must not throw or drop the good value read with it.
        var goodNodeId = new NodeId(8001, 2);
        var notReadyNodeId = new NodeId(8002, 2);
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
                    new DataValue { Value = 42, StatusCode = StatusCodes.Good },
                    new DataValue { StatusCode = StatusCodes.BadWaitingForInitialData }
                ],
                DiagnosticInfos = []
            });

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = goodNodeId, AttributeId = Opc.Ua.Attributes.Value },
            new ReadValueId { NodeId = notReadyNodeId, AttributeId = Opc.Ua.Attributes.Value }
        };

        // Act
        var results = await mockSession.Object.ReadNodesAsync(
            nodesToRead,
            TimestampsToReturn.Source,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert: both slots returned and aligned; the good value survives the not-ready one.
        Assert.Equal(2, results.Count);
        Assert.True(StatusCode.IsGood(results[0].StatusCode));
        Assert.Equal(42, results[0].Value);
        Assert.Equal(StatusCodes.BadWaitingForInitialData, results[1].StatusCode);
    }

    [Fact]
    public async Task WhenBrowseReturnsFewerResultsThanRequested_ThenThrowsTransientServiceException()
    {
        // Arrange: two nodes requested but the server returns only one BrowseResult. The missing
        // node must surface as a transient failure so the load retries, rather than silently
        // loading that subject with zero children.
        var returnedNodeId = new NodeId(1001, 2);
        var missingNodeId = new NodeId(1002, 2);
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
                Results = [new BrowseResult { References = [] }],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [returnedNodeId, missingNodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(missingNodeId, exception.NodeId);
    }

    [Fact]
    public async Task WhenBrowseNextReturnsFewerResultsThanRequested_ThenThrowsTransientServiceException()
    {
        // Arrange: the initial browse returns two nodes, each with a continuation point. The
        // follow-up BrowseNext is sent both continuation points but the server returns only one
        // result. The missing node must surface as a transient failure (mirroring the initial
        // Browse path) so the load retries, rather than silently truncating that node's children
        // and leaking its continuation point.
        var returnedNodeId = new NodeId(1001, 2);
        var missingNodeId = new NodeId(1002, 2);
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
                    new BrowseResult { References = [], ContinuationPoint = [0xAA] },
                    new BrowseResult { References = [], ContinuationPoint = [0xBB] }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results = [new BrowseResult { References = [] }],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [returnedNodeId, missingNodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("BrowseNext", exception.Operation);
        Assert.Equal(missingNodeId, exception.NodeId);
    }

    [Fact]
    public async Task WhenServerReportsIntOverflowingOperationLimit_ThenBrowseUsesDefaultBatchLimit()
    {
        // Arrange: a buggy or hostile server can report MaxNodesPerBrowse above int.MaxValue;
        // an unclamped uint-to-int cast would produce a negative batch size and corrupt the
        // batching loop math.
        var firstNodeId = new NodeId(1001, 2);
        var secondNodeId = new NodeId(1002, 2);
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerBrowse = uint.MaxValue });

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var _ in descriptions)
                {
                    results.Add(new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("Child"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ]
                    });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [firstNodeId, secondNodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.True(result.ContainsKey(firstNodeId));
        Assert.True(result.ContainsKey(secondNodeId));
    }

    [Fact]
    public async Task WhenServerLimitsBrowseContinuationPoints_ThenBrowseBatchesToThatQuota()
    {
        // Arrange: the continuation-point quota (2) is far below the operation limit (100).
        // Batching by the operation limit would open more continuation points than the server
        // allows, which fails permanently and identically on every reconnect retry.
        var nodeIds = Enumerable.Range(1, 5).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerBrowse = 100 });
        mockSession.SetupGet(s => s.ServerCapabilities).Returns(new ServerCapabilities { MaxBrowseContinuationPoints = 2 });

        var batchSizes = new List<int>();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                batchSizes.Add(descriptions.Count);
                var results = new BrowseResultCollection();
                foreach (var _ in descriptions)
                {
                    results.Add(new BrowseResult { References = [] });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal([2, 2, 1], batchSizes);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task WhenBrowseRejectsBatchAsTooLarge_ThenSplitsAndRetriesUntilAccepted()
    {
        // Arrange: the server rejects any batch above one node with BadRequestTooLarge. Halving
        // must recurse until every node is browsed rather than failing the whole load.
        var nodeIds = Enumerable.Range(1, 4).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                if (descriptions.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadRequestTooLarge);
                }
                return new BrowseResponse
                {
                    Results = [new BrowseResult { References = [] }],
                    DiagnosticInfos = []
                };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.All(nodeIds, nodeId => Assert.True(result.ContainsKey(nodeId)));
    }

    [Fact]
    public async Task WhenBrowseAbortsAfterCollectingContinuationPoints_ThenReleasesThem()
    {
        // Arrange: the first node pages (continuation point handed out), the second returns a
        // transient bad status that aborts the load. The first node's continuation point must be
        // released, otherwise every aborted load burns one of the server's scarce slots.
        var pagingNodeId = new NodeId(1001, 2);
        var failingNodeId = new NodeId(1002, 2);
        var continuationToken = new byte[] { 0xAA };
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
                    new BrowseResult { References = [], ContinuationPoint = continuationToken },
                    new BrowseResult { StatusCode = StatusCodes.BadCommunicationError, References = [] }
                ],
                DiagnosticInfos = []
            });

        var releasedTokens = new List<byte[]>();
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                true,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection points, CancellationToken _) =>
            {
                releasedTokens.AddRange(points);
                return new BrowseNextResponse { Results = [], DiagnosticInfos = [] };
            });

        // Act & Assert
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [pagingNodeId, failingNodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal([continuationToken], releasedTokens);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(100)]
    public async Task WhenBrowseNextReturnsFreshContinuationPointForever_ThenStopsAfterMaxContinuationRoundsAndOmitsNode(int maxContinuationRounds)
    {
        // Arrange: the server never stops handing out continuation points. The round cap must stop
        // the loop, omit the node rather than report the prefix collected so far as its children,
        // and release the trailing continuation point.
        var nodeId = new NodeId(1001, 2);
        var browseNextCallCount = 0;
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
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("First"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ],
                        ContinuationPoint = [0xAA]
                    }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                false,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection _, CancellationToken _) =>
            {
                var round = ++browseNextCallCount;
                return new BrowseNextResponse
                {
                    Results =
                    [
                        new BrowseResult
                        {
                            References =
                            [
                                new ReferenceDescription { BrowseName = new QualifiedName($"Page{round}"), NodeId = new ExpandedNodeId(new NodeId((uint)(3000 + round), 2)) }
                            ],
                            ContinuationPoint = [(byte)round]
                        }
                    ],
                    DiagnosticInfos = []
                };
            });

        var releasedTokens = new List<byte[]>();
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                true,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection points, CancellationToken _) =>
            {
                releasedTokens.AddRange(points);
                return new BrowseNextResponse { Results = [], DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [nodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Empty(result);
        Assert.Equal(maxContinuationRounds, browseNextCallCount);
        Assert.Single(releasedTokens);
    }

    [Fact]
    public async Task WhenBrowseNextReturnsPermanentBadStatus_ThenPartiallyPagedNodeIsOmitted()
    {
        // Arrange: the first page arrives, the second fails permanently. The node has a truncated
        // child list either way, so it is omitted rather than reported as fully browsed.
        var nodeId = new NodeId(1001, 2);
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
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("First"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ],
                        ContinuationPoint = [0xAA]
                    }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                false,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results = [new BrowseResult { StatusCode = StatusCodes.BadNodeIdUnknown, References = [] }],
                DiagnosticInfos = []
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [nodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task WhenMaxReferencesPerNodeIsZero_ThenBrowseBatchIsNotCappedByContinuationPoints()
    {
        // Arrange: at maxReferencesPerNode 0 the server returns every reference in the first
        // response and issues no continuation point, so the quota (100) cannot bind and the far
        // larger operation limit (4000) governs. Capping here would turn one round-trip into ten.
        var nodeIds = Enumerable.Range(1, 1000).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerBrowse = 4000 });
        mockSession.SetupGet(s => s.ServerCapabilities).Returns(new ServerCapabilities { MaxBrowseContinuationPoints = 100 });

        var batchSizes = new List<int>();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                batchSizes.Add(descriptions.Count);
                var results = new BrowseResultCollection();
                foreach (var _ in descriptions)
                {
                    results.Add(new BrowseResult { References = [] });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 0,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal([1000], batchSizes);
        Assert.Equal(1000, result.Count);
    }

    [Fact]
    public async Task WhenMaxReferencesPerNodeIsNonZero_ThenBrowseBatchIsCappedByContinuationPoints()
    {
        // Arrange: same server as above, but a non-zero maxReferencesPerNode means each operation
        // can leave a continuation point open, so the quota (100) binds even though the operation
        // limit (4000) would allow the whole load in one call.
        var nodeIds = Enumerable.Range(1, 1000).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerBrowse = 4000 });
        mockSession.SetupGet(s => s.ServerCapabilities).Returns(new ServerCapabilities { MaxBrowseContinuationPoints = 100 });

        var batchSizes = new List<int>();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                batchSizes.Add(descriptions.Count);
                var results = new BrowseResultCollection();
                foreach (var _ in descriptions)
                {
                    results.Add(new BrowseResult { References = [] });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 50,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal(10, batchSizes.Count);
        Assert.All(batchSizes, batchSize => Assert.Equal(100, batchSize));
        Assert.Equal(1000, result.Count);
    }

    [Fact]
    public async Task WhenServerReturnsBadNoContinuationPoints_ThenBatchSizeHalvesAndRetries()
    {
        // Arrange: the server issues at most two continuation points per browse call and answers
        // every operation past that with BadNoContinuationPoints. Without splitting, reconnect
        // would retry the identical batch size and fail identically forever.
        var nodeIds = Enumerable.Range(1, 5).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();

        var batchSizes = new List<int>();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                batchSizes.Add(descriptions.Count);
                var results = new BrowseResultCollection();
                for (var index = 0; index < descriptions.Count; index++)
                {
                    if (index >= 2)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadNoContinuationPoints, References = [] });
                        continue;
                    }

                    // Good results carry both a reference and a continuation point. Processing them
                    // before the split is detected would put this reference into the result bucket
                    // and the retry would append it a second time.
                    var identifier = (uint)descriptions[index].NodeId.Identifier;
                    results.Add(new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription
                            {
                                BrowseName = new QualifiedName("Child"),
                                NodeId = new ExpandedNodeId(new NodeId(identifier + 10000, 2))
                            }
                        ],
                        ContinuationPoint = [(byte)(identifier - 1000)]
                    });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                false,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                // Final page: no further references and no continuation point, so pagination ends.
                var results = new BrowseResultCollection();
                foreach (var _ in continuationPoints)
                {
                    results.Add(new BrowseResult { References = [] });
                }
                return new BrowseNextResponse { Results = results, DiagnosticInfos = [] };
            });

        var releasedTokens = new List<byte[]>();
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                true,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                releasedTokens.AddRange(continuationPoints);
                return new BrowseNextResponse { Results = [], DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 1,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert: the batch halves until every operation fits inside the quota, so it converges
        // instead of repeating the rejected size.
        Assert.Equal([5, 2, 3, 1, 2], batchSizes);

        // Every node is browsed, and each appears exactly once with its own single child. A
        // duplicate would mean the split ran after the result buckets were already filled.
        Assert.Equal(5, result.Count);
        Assert.All(nodeIds, nodeId => Assert.True(result.ContainsKey(nodeId)));
        Assert.All(result.Values, references =>
            Assert.Equal(references.Count, references.Select(reference => reference.NodeId).Distinct().Count()));
        Assert.All(result.Values, references => Assert.Single(references));

        // The four continuation points the server did issue for the two rejected batches are handed
        // back before recursing, otherwise the smaller batches compete with quota this attempt holds.
        Assert.Equal(4, releasedTokens.Count);
    }

    [Fact]
    public async Task WhenBatchOfOneReturnsBadNoContinuationPoints_ThenThrowsTransientServiceException()
    {
        // Arrange: a single node cannot be split any further, so the quota status is classified
        // like any other bad status and aborts the load instead of looping on the split.
        var nodeId = new NodeId(1001, 2);
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
                Results = [new BrowseResult { StatusCode = StatusCodes.BadNoContinuationPoints, References = [] }],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [nodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadNoContinuationPoints, exception.StatusCode);
    }

    [Fact]
    public async Task WhenBrowseReturnsMoreResultsThanRequested_ThenExtrasAreReleasedAndTheLoadContinues()
    {
        // Arrange: one node requested, two results returned. The extra result was never asked for,
        // so its continuation point goes back to the server and the requested node still loads.
        var nodeId = new NodeId(1001, 2);
        var extraToken = new byte[] { 0xEE };
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
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("Child"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ]
                    },
                    new BrowseResult { References = [], ContinuationPoint = extraToken }
                ],
                DiagnosticInfos = []
            });

        var releasedTokens = new List<byte[]>();
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                true,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection points, CancellationToken _) =>
            {
                releasedTokens.AddRange(points);
                return new BrowseNextResponse { Results = [], DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [nodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Single(result[nodeId]);
        Assert.Equal([extraToken], releasedTokens);
    }

    [Fact]
    public async Task WhenReadSpansMultipleBatches_ThenResultsStayAlignedWithRequests()
    {
        // Arrange: MaxNodesPerRead 3 splits four ReadValueIds into two calls. Each request is
        // answered by its own (NodeId, AttributeId) pair, so a batch boundary that shifted or
        // reordered results would surface as a value under the wrong request.
        var firstNodeId = new NodeId(5001, 2);
        var secondNodeId = new NodeId(5002, 2);
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerRead = 3 });

        var valuesByRequest = new Dictionary<(NodeId NodeId, uint AttributeId), int>
        {
            [(firstNodeId, Opc.Ua.Attributes.Value)] = 11,
            [(firstNodeId, Opc.Ua.Attributes.DataType)] = 12,
            [(secondNodeId, Opc.Ua.Attributes.Value)] = 21,
            [(secondNodeId, Opc.Ua.Attributes.DataType)] = 22
        };

        var batchSizes = new List<int>();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection batch, CancellationToken _) =>
            {
                batchSizes.Add(batch.Count);
                var results = new DataValueCollection();
                foreach (var readValueId in batch)
                {
                    results.Add(new DataValue { Value = valuesByRequest[(readValueId.NodeId, readValueId.AttributeId)], StatusCode = StatusCodes.Good });
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = firstNodeId, AttributeId = Opc.Ua.Attributes.Value },
            new ReadValueId { NodeId = firstNodeId, AttributeId = Opc.Ua.Attributes.DataType },
            new ReadValueId { NodeId = secondNodeId, AttributeId = Opc.Ua.Attributes.Value },
            new ReadValueId { NodeId = secondNodeId, AttributeId = Opc.Ua.Attributes.DataType }
        };

        // Act
        var results = await mockSession.Object.ReadNodesAsync(
            nodesToRead,
            TimestampsToReturn.Neither,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal([3, 1], batchSizes);
        Assert.Equal(nodesToRead.Count, results.Count);
        for (var i = 0; i < nodesToRead.Count; i++)
        {
            Assert.Equal(valuesByRequest[(nodesToRead[i].NodeId, nodesToRead[i].AttributeId)], results[i].Value);
        }
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        var namespaceTable = new NamespaceTable();
        namespaceTable.Append("urn:test");
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        return mockSession;
    }
}
