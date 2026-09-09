using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Classifies OPC UA <see cref="StatusCode"/>s for retry decisions. Two questions are asked at
/// different callsites, and the access-scoped codes answer them oppositely, so there are two
/// predicates rather than one shared list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsRecoverableWithinSession"/> answers <em>can this status recover without a new
/// session?</em> It backs the subscribe and write paths. Access-scoped codes
/// (<c>BadUserAccessDenied</c>, <c>BadNotReadable</c>, <c>BadNotImplemented</c>) are recoverable
/// here: role permissions and the <c>AccessLevel</c> attribute are mutable server-side, so a
/// monitored item can start succeeding mid-session and must be kept for retry rather than dropped.
/// <c>BadSecurityModeInsufficient</c> is permanent because it is bound to the SecureChannel's
/// <c>MessageSecurityMode</c>, which can only change by opening a new channel, and reconnect
/// re-attempts everything anyway.
/// </para>
/// <para>
/// <see cref="ThrowIfLoadMustRetry"/> answers <em>must the load abort and retry on this status?</em>
/// It backs the browse and read paths, where a status that a fresh load could clear aborts the load
/// so reconnect retries it, and one that would repeat is logged and the node skipped. Here the
/// access-scoped codes repeat: the session's identity and permissions have not changed, so throwing
/// would crash-loop the whole load instead of skipping the one unreachable node. That makes the
/// load-skip set a superset of the session-permanent set by exactly those three codes.
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
