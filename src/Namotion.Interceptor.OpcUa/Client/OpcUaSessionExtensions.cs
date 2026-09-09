using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal static class OpcUaSessionExtensions
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    // Soft cap when a server reports 0/null (or an int-overflowing value) for its
    // per-call operation limit. The upper clamp keeps a hostile or buggy server from
    // producing a negative batch size, which would corrupt the batching loop math.
    private const int DefaultBatchLimit = 256;

    private static int ToBatchLimit(uint? limit) => limit is > 0 and <= int.MaxValue ? (int)limit : DefaultBatchLimit;

    private static int GetMaxNodesPerBrowse(ISession session) => ToBatchLimit(session.OperationLimits?.MaxNodesPerBrowse);

    private static int GetMaxNodesPerRead(ISession session) => ToBatchLimit(session.OperationLimits?.MaxNodesPerRead);

    /// <summary>
    /// Batch size for browse calls: the per-call operation limit, capped by the server's
    /// continuation-point quota when <paramref name="maxReferencesPerNode"/> is non-zero. At zero the
    /// server returns every reference in the first response and issues no continuation point, so the
    /// quota cannot bind.
    /// </summary>
    /// <remarks>
    /// A batch that opens more continuation points than the quota fails with
    /// <c>BadNoContinuationPoints</c> or has its oldest points evicted, and a same-size retry fails
    /// identically. A server that under-reports or shrinks its quota is still caught by the
    /// <c>BadNoContinuationPoints</c> split in <see cref="BrowseBatchAsync"/>.
    /// </remarks>
    private static int GetBrowseBatchSize(ISession session, uint maxReferencesPerNode)
    {
        var operationLimit = GetMaxNodesPerBrowse(session);
        if (maxReferencesPerNode == 0)
        {
            return operationLimit;
        }

        // Declared non-nullable by ISession, but a partially initialized or mocked session can
        // still hand back null, and a server that does not expose the node reports 0.
        int continuationPointLimit = session.ServerCapabilities?.MaxBrowseContinuationPoints ?? 0;
        return continuationPointLimit > 0 && continuationPointLimit < operationLimit
            ? continuationPointLimit
            : operationLimit;
    }

    /// <summary>
    /// Browses multiple nodes in batched calls, collecting all references including
    /// continuation-point pages. Deduplicates input NodeIds. A NodeId present in the result was
    /// browsed to completion (possibly to an empty reference list); one that failed this round,
    /// whether the first page returned a permanent bad status or pagination stopped part-way, is
    /// omitted rather than reported with a truncated child list.
    /// </summary>
    public static async Task<Dictionary<NodeId, ReferenceDescriptionCollection>> BrowseNodesAsync(
        this ISession session,
        IReadOnlyCollection<NodeId> nodeIds,
        uint maxReferencesPerNode,
        int maxContinuationRounds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, ReferenceDescriptionCollection>(nodeIds.Count);
        if (nodeIds.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<NodeId>(nodeIds.Count);
        var uniqueNodeIds = new List<NodeId>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            if (seen.Add(nodeId))
            {
                uniqueNodeIds.Add(nodeId);
            }
        }

        var batchSize = GetBrowseBatchSize(session, maxReferencesPerNode);

        for (var offset = 0; offset < uniqueNodeIds.Count; offset += batchSize)
        {
            var end = Math.Min(offset + batchSize, uniqueNodeIds.Count);
            await BrowseBatchAsync(session, uniqueNodeIds, offset, end, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Reads multiple node attributes in batched calls with split-and-retry on
    /// <c>BadTooManyOperations</c>. Returns a positionally aligned
    /// <see cref="DataValueCollection"/> where <c>allResults[i]</c> corresponds to
    /// <paramref name="nodesToRead"/>[i]. Short server responses are padded with
    /// <c>BadUnexpectedError</c> to maintain alignment. Best-effort: bad statuses are
    /// never thrown here, so each caller decides how to handle them.
    /// </summary>
    public static async Task<DataValueCollection> ReadNodesAsync(
        this ISession session,
        ReadValueIdCollection nodesToRead,
        TimestampsToReturn timestampsToReturn,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var allResults = new DataValueCollection(nodesToRead.Count);
        if (nodesToRead.Count == 0)
        {
            return allResults;
        }

        var maxBatchSize = GetMaxNodesPerRead(session);
        for (var batchStart = 0; batchStart < nodesToRead.Count; batchStart += maxBatchSize)
        {
            var batchEnd = Math.Min(batchStart + maxBatchSize, nodesToRead.Count);
            await ReadSingleBatchAsync(session, nodesToRead, batchStart, batchEnd, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
        }

        return allResults;
    }

    /// <summary>
    /// Deduplicates browse references by resolving each <see cref="ExpandedNodeId"/>
    /// to a canonical <see cref="NodeId"/> via the session's namespace table.
    /// References with unresolvable namespace URIs, missing BrowseName, or a duplicate
    /// resolved NodeId are skipped (each skip is logged) so downstream consumers can
    /// safely access <c>Reference.BrowseName.Name</c>. Because skips shorten the result,
    /// consumers that align it positionally (e.g. collection child reuse) can shift.
    /// </summary>
    public static List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        this ISession session,
        IReadOnlyCollection<ReferenceDescription> references,
        ILogger logger)
    {
        var seen = new HashSet<NodeId>(references.Count);
        var result = new List<(ReferenceDescription, NodeId)>(references.Count);
        foreach (var reference in references)
        {
            if (string.IsNullOrEmpty(reference.BrowseName?.Name))
            {
                logger.LogWarning(
                    "Skipping browse reference with missing BrowseName (NodeId '{NodeId}').",
                    reference.NodeId);
                continue;
            }
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                logger.LogWarning(
                    "Skipping browse reference '{BrowseName}' with unresolvable NodeId '{NodeId}': namespace URI is not registered in the session's NamespaceTable.",
                    reference.BrowseName.Name, reference.NodeId);
                continue;
            }
            if (!seen.Add(nodeId))
            {
                // Debug, not Warning: a node reachable through more than one hierarchical
                // reference is normal in OPC UA address spaces, so this is expected traffic.
                logger.LogDebug(
                    "Skipping duplicate browse reference '{BrowseName}' resolving to already-seen NodeId '{NodeId}'.",
                    reference.BrowseName.Name, nodeId);
                continue;
            }
            result.Add((reference, nodeId));
        }
        return result;
    }

    private static async Task BrowseBatchAsync(
        ISession session,
        IReadOnlyList<NodeId> nodeIds,
        int offset,
        int end,
        uint maxReferencesPerNode,
        int maxContinuationRounds,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var count = end - offset;
        var browseDescriptions = new BrowseDescriptionCollection(count);
        for (var i = offset; i < end; i++)
        {
            browseDescriptions.Add(new BrowseDescription
            {
                NodeId = nodeIds[i],
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = NodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            });
        }

        BrowseResponse response;
        try
        {
            response = await session.BrowseAsync(
                null, null,
                maxReferencesPerNode,
                browseDescriptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && OpcUaStatusCodeClassifier.IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "BrowseAsync rejected batch of {Count} nodes ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            var midpoint = offset + count / 2;
            await BrowseBatchAsync(session, nodeIds, offset, midpoint, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            await BrowseBatchAsync(session, nodeIds, midpoint, end, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Checked before any result is merged: GetOrCreateBucket appends, so retrying a partially
        // merged batch would duplicate references. BadNoContinuationPoints means the batch asked for
        // more continuation points than the server's quota allows, which a same-size retry would
        // repeat forever, so shrinking the batch is what makes it converge. Reachable whenever the
        // quota cap is skipped (maxReferencesPerNode 0) against a server that pages anyway, or when
        // a server under-reports or dynamically shrinks its quota.
        if (count > 1 && HasNoContinuationPointsStatus(response.Results, count))
        {
            logger.LogWarning(
                "BrowseAsync exhausted the server's continuation points for a batch of {Count} nodes ({StatusCode}). Splitting into smaller batches.",
                count, (StatusCode)StatusCodes.BadNoContinuationPoints);

            // The points the server did issue must go back before retrying, otherwise this attempt
            // keeps holding quota that the smaller batches then fail to obtain.
            var issuedPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
            await CollectContinuationPointsAsync(session, response.Results, count, i => nodeIds[offset + i], issuedPoints, logger).ConfigureAwait(false);
            await ReleaseContinuationPointsAsync(session, issuedPoints, logger).ConfigureAwait(false);

            var splitPoint = offset + count / 2;
            await BrowseBatchAsync(session, nodeIds, offset, splitPoint, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            await BrowseBatchAsync(session, nodeIds, splitPoint, end, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var continuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
        try
        {
            await MergeBrowseResultsAsync(session, "Browse", response.Results, count, i => nodeIds[offset + i], result, continuationPoints, logger).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseContinuationPointsAsync(session, continuationPoints, logger).ConfigureAwait(false);
            throw;
        }

        await ProcessContinuationPointsAsync(session, continuationPoints, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True iff any in-range result carries <c>BadNoContinuationPoints</c>. Extras beyond
    /// <paramref name="count"/> are ignored because they were never requested and their
    /// continuation points are already released as orphans.
    /// </summary>
    private static bool HasNoContinuationPointsStatus(BrowseResultCollection results, int count)
    {
        var inRange = Math.Min(results.Count, count);
        for (var index = 0; index < inRange; index++)
        {
            if (results[index].StatusCode.Code == StatusCodes.BadNoContinuationPoints)
            {
                return true;
            }
        }
        return false;
    }

    private static ReferenceDescriptionCollection GetOrCreateBucket(
        Dictionary<NodeId, ReferenceDescriptionCollection> result, NodeId nodeId)
    {
        if (!result.TryGetValue(nodeId, out var bucket))
        {
            bucket = new ReferenceDescriptionCollection();
            result[nodeId] = bucket;
        }
        return bucket;
    }

    private static async Task ProcessContinuationPointsAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> initialPoints,
        uint maxReferencesPerNode,
        int maxContinuationRounds,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var current = initialPoints;
        var next = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
        var round = 0;
        var batchSize = GetBrowseBatchSize(session, maxReferencesPerNode);
        try
        {
            while (current.Count > 0)
            {
                // One round drains every currently-pending continuation point, possibly
                // in multiple BrowseNextAsync calls (the inner loop batches by
                // GetBrowseBatchSize). The cap therefore bounds pagination *depth*,
                // i.e. how many times the server can keep handing out fresh continuation
                // points before we give up. It does not bound BrowseNext call count,
                // which legitimately scales with the number of in-flight continuation
                // points per round.
                if (++round > maxContinuationRounds)
                {
                    logger.LogWarning(
                        "Aborting BrowseNext after {MaxRounds} rounds with {Remaining} continuation points still pending. Possible server bug.",
                        maxContinuationRounds, current.Count);

                    // Those nodes are still mid-pagination, so what was collected is a prefix of
                    // their children. Drop it: the contract is that a NodeId present in the result
                    // was browsed completely, and a truncated child list would make positional
                    // consumers replace a collection with a shortened one.
                    foreach (var (nodeId, _) in current)
                    {
                        result.Remove(nodeId);
                    }
                    break;
                }

                for (var offset = 0; offset < current.Count; offset += batchSize)
                {
                    var end = Math.Min(offset + batchSize, current.Count);
                    await BrowseNextBatchAsync(session, current, offset, end, result, next, logger, cancellationToken).ConfigureAwait(false);
                }

                (current, next) = (next, current);
                next.Clear();
            }
        }
        catch
        {
            await ReleaseContinuationPointsAsync(session, current, logger).ConfigureAwait(false);
            await ReleaseContinuationPointsAsync(session, next, logger).ConfigureAwait(false);
            throw;
        }

        // Only non-empty when the round cap broke out of the loop early; no-op otherwise.
        await ReleaseContinuationPointsAsync(session, current, logger).ConfigureAwait(false);
    }

    private static async Task ReleaseContinuationPointsAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> continuationPoints,
        ILogger logger)
    {
        if (continuationPoints.Count == 0)
        {
            return;
        }

        // Releasing frees continuation points rather than consuming them, so the quota does not
        // apply and the plain operation limit is the right batch size here.
        var batchSize = GetMaxNodesPerBrowse(session);
        for (var offset = 0; offset < continuationPoints.Count; offset += batchSize)
        {
            try
            {
                // Per batch, not shared: one shared budget would let a slow first batch consume
                // the whole timeout and cancel every remaining release without ever calling.
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var end = Math.Min(offset + batchSize, continuationPoints.Count);
                var collection = new ByteStringCollection(end - offset);
                for (var i = offset; i < end; i++)
                {
                    collection.Add(continuationPoints[i].ContinuationPoint);
                }
                await session.BrowseNextAsync(null, true, collection, releaseTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Best-effort release of continuation points failed at offset {Offset}.", offset);
            }
        }
    }

    /// <summary>
    /// Routes continuation points from a browse or browse-next response: good in-range results go to
    /// <paramref name="goodPoints"/> (the caller releases these if it later aborts), while bad-status
    /// results and any extras the server returned beyond <paramref name="count"/> are released
    /// immediately so unfollowed pages do not leak server-side.
    /// </summary>
    private static async Task CollectContinuationPointsAsync(
        ISession session,
        BrowseResultCollection results,
        int count,
        Func<int, NodeId> nodeIdAt,
        List<(NodeId NodeId, byte[] ContinuationPoint)> goodPoints,
        ILogger logger)
    {
        List<(NodeId NodeId, byte[] ContinuationPoint)>? orphanedPoints = null;
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].ContinuationPoint is { Length: > 0 } continuationPoint)
            {
                if (i < count && StatusCode.IsGood(results[i].StatusCode))
                {
                    goodPoints.Add((nodeIdAt(i), continuationPoint));
                }
                else
                {
                    (orphanedPoints ??= []).Add((i < count ? nodeIdAt(i) : NodeId.Null, continuationPoint));
                }
            }
        }

        if (orphanedPoints is { Count: > 0 })
        {
            await ReleaseContinuationPointsAsync(session, orphanedPoints, logger).ConfigureAwait(false);
        }
    }

    private static async Task BrowseNextBatchAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> current,
        int offset,
        int end,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        List<(NodeId NodeId, byte[] ContinuationPoint)> next,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var count = end - offset;
        var continuationPointCollection = new ByteStringCollection(count);
        for (var i = offset; i < end; i++)
        {
            continuationPointCollection.Add(current[i].ContinuationPoint);
        }

        BrowseNextResponse nextResponse;
        try
        {
            nextResponse = await session.BrowseNextAsync(
                null, false, continuationPointCollection, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && OpcUaStatusCodeClassifier.IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "BrowseNextAsync rejected batch of {Count} continuation points ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            var midpoint = offset + count / 2;
            await BrowseNextBatchAsync(session, current, offset, midpoint, result, next, logger, cancellationToken).ConfigureAwait(false);
            await BrowseNextBatchAsync(session, current, midpoint, end, result, next, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Fresh continuation points go to `next`, which the caller releases on abort together with
        // the point still pending in `current` for a slot whose result is missing.
        await MergeBrowseResultsAsync(session, "BrowseNext", nextResponse.Results, count, i => current[offset + i].NodeId, result, next, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges one Browse or BrowseNext response into <paramref name="result"/>: good references are
    /// appended to their node's bucket, a permanent bad status omits the node, and a transient bad
    /// status or a missing result slot throws <see cref="OpcUaTransientServiceException"/>. The
    /// continuation points of good in-range results are collected into
    /// <paramref name="continuationPoints"/> before any result is merged, so the caller can release
    /// them when the merge throws; those of bad-status and extra results are released here.
    /// </summary>
    private static async Task MergeBrowseResultsAsync(
        ISession session,
        string operation,
        BrowseResultCollection results,
        int count,
        Func<int, NodeId> nodeIdAt,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        List<(NodeId NodeId, byte[] ContinuationPoint)> continuationPoints,
        ILogger logger)
    {
        var actual = results.Count;
        if (actual != count)
        {
            logger.LogWarning(
                "{Operation} returned {Actual} results but {Expected} were requested.",
                operation, actual, count);
        }

        await CollectContinuationPointsAsync(session, results, count, nodeIdAt, continuationPoints, logger).ConfigureAwait(false);

        for (var i = 0; i < count; i++)
        {
            var nodeId = nodeIdAt(i);
            if (i >= actual)
            {
                // Abort so the load retries instead of loading the node with a truncated child list.
                throw new OpcUaTransientServiceException(operation, nodeId, (StatusCode)StatusCodes.BadUnexpectedError);
            }

            var browseResult = results[i];
            if (!StatusCode.IsGood(browseResult.StatusCode))
            {
                OpcUaStatusCodeClassifier.ThrowIfLoadMustRetry(browseResult.StatusCode, operation, nodeId);
                logger.LogWarning(
                    "{Operation} returned permanent bad status for {NodeId} ({StatusCode}); omitting the node from this load.",
                    operation, nodeId, browseResult.StatusCode);

                // Drops the pages collected so far, which would leave the child list truncated. A
                // no-op on a first page: the result dictionary is per BrowseNodesAsync call and its
                // inputs are deduplicated, so the node has no bucket yet.
                result.Remove(nodeId);
                continue;
            }

            var bucket = GetOrCreateBucket(result, nodeId);
            if (browseResult.References is { Count: > 0 })
            {
                bucket.AddRange(browseResult.References);
            }
        }
    }

    private static async Task ReadSingleBatchAsync(
        ISession session,
        ReadValueIdCollection nodesToRead,
        int batchStart,
        int batchEnd,
        TimestampsToReturn timestampsToReturn,
        DataValueCollection allResults,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var count = batchEnd - batchStart;

        ReadValueIdCollection chunk;
        if (count == nodesToRead.Count)
        {
            // Everything fits in one call, which is the common case: send the caller's collection
            // as-is rather than copying it. ReadAsync does not mutate the request.
            chunk = nodesToRead;
        }
        else
        {
            chunk = new ReadValueIdCollection(count);
            for (var i = batchStart; i < batchEnd; i++)
            {
                chunk.Add(nodesToRead[i]);
            }
        }

        ReadResponse response;
        try
        {
            response = await session.ReadAsync(null, 0, timestampsToReturn, chunk, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && OpcUaStatusCodeClassifier.IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "ReadAsync rejected batch of {Count} items ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            // Halving may split a caller's logical pair (e.g. DataType+ValueRank) across
            // two batches. That's safe: each sub-batch is independently padded to its
            // requested length, so the flat allResults stays aligned with nodesToRead.
            var midpoint = batchStart + count / 2;
            await ReadSingleBatchAsync(session, nodesToRead, batchStart, midpoint, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
            await ReadSingleBatchAsync(session, nodesToRead, midpoint, batchEnd, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var actual = response.Results.Count;
        if (actual == count)
        {
            allResults.AddRange(response.Results);
        }
        else
        {
            logger.LogWarning(
                "ReadAsync returned {Actual} results but {Expected} were requested. Padding to preserve positional alignment.",
                actual, count);

            var take = Math.Min(actual, count);
            for (var i = 0; i < take; i++)
            {
                allResults.Add(response.Results[i]);
            }
            // Pad missing trailing slots so allResults stays aligned with nodesToRead.
            // This primitive never throws; callers classify the bad status themselves.
            for (var i = take; i < count; i++)
            {
                allResults.Add(new DataValue { StatusCode = StatusCodes.BadUnexpectedError });
            }
        }
    }
}
