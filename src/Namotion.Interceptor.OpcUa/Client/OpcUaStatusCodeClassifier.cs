using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Classifies OPC UA <see cref="StatusCode"/>s for retry decisions. The two predicates cannot share
/// one list: the access-scoped codes are recoverable for the first and a skip for the second.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsRecoverableWithinSession"/> answers whether a status can recover without a new
/// session and backs the subscribe and write paths. <see cref="ThrowIfLoadMustRetry"/> answers
/// whether the load must abort and retry and backs the browse and read paths; its load-skip set is
/// the session-permanent set plus <c>BadUserAccessDenied</c>, <c>BadNotReadable</c> and
/// <c>BadNotImplemented</c>. The reasoning is in docs/design/opcua-client-loader.md.
/// </para>
/// <para>
/// The write path uses <see cref="IsRecoverableWithinSession"/> for diagnostics only:
/// <c>WriteResult.FailedChanges</c> must stay complete for the retry queue and the transaction
/// writer, so permanently failed writes are still requeued.
/// </para>
/// </remarks>
internal static class OpcUaStatusCodeClassifier
{
    /// <summary>
    /// Bad statuses that cannot recover without a new session. Used by the subscribe and write paths.
    /// </summary>
    private static readonly HashSet<uint> SessionPermanentCodes =
    [
        StatusCodes.BadNodeIdUnknown,
        StatusCodes.BadNodeIdInvalid,
        StatusCodes.BadAttributeIdInvalid,
        StatusCodes.BadIndexRangeInvalid,
        StatusCodes.BadTypeMismatch,
        StatusCodes.BadSecurityModeInsufficient,
        StatusCodes.BadNotWritable,
        StatusCodes.BadWriteNotSupported
    ];

    /// <summary>
    /// Bad statuses where reloading the structure now cannot help, so the browse/read caller skips
    /// the node instead of aborting the load. Superset of <see cref="SessionPermanentCodes"/> by the
    /// access-scoped codes, which are deterministic for the current session even though they may
    /// recover over a longer horizon.
    /// </summary>
    private static readonly HashSet<uint> LoadSkipCodes =
    [
        .. SessionPermanentCodes,
        StatusCodes.BadUserAccessDenied,
        StatusCodes.BadNotReadable,
        StatusCodes.BadNotImplemented
    ];

    /// <summary>
    /// True iff <paramref name="statusCode"/> is a bad status that could recover without a new
    /// session (e.g. transport glitch, server-side resource exhaustion, a later permission grant).
    /// Returns false for good and uncertain statuses. Used by the subscribe and write paths.
    /// </summary>
    public static bool IsRecoverableWithinSession(StatusCode statusCode)
    {
        return StatusCode.IsBad(statusCode) && !SessionPermanentCodes.Contains(statusCode.Code);
    }

    /// <summary>
    /// Throws <see cref="OpcUaTransientServiceException"/> if <paramref name="statusCode"/> is a bad
    /// status that a fresh load could clear, so the browse/read caller aborts and lets reconnect
    /// retry. Statuses that would repeat immediately on reload (permanent design-time and
    /// access-scoped codes) and non-bad statuses are ignored, so the caller logs and skips the node.
    /// </summary>
    public static void ThrowIfLoadMustRetry(StatusCode statusCode, string operation, NodeId? nodeId)
    {
        if (StatusCode.IsBad(statusCode) && !LoadSkipCodes.Contains(statusCode.Code))
        {
            throw new OpcUaTransientServiceException(operation, nodeId, statusCode);
        }
    }

    /// <summary>
    /// True iff the <see cref="ServiceResultException"/> indicates the server rejected
    /// the batch size rather than the operation itself.
    /// </summary>
    public static bool IsBatchTooLarge(ServiceResultException exception) =>
        exception.StatusCode is StatusCodes.BadTooManyOperations
            or StatusCodes.BadEncodingLimitsExceeded
            or StatusCodes.BadRequestTooLarge
            or StatusCodes.BadResponseTooLarge;
}
