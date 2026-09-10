using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Transactions;

/// <summary>
/// Handler for writing transaction changes to external sources.
/// Implement this interface and register it with <see cref="IInterceptorSubjectContext"/>
/// to enable external source writes during transaction commit.
/// </summary>
/// <remarks>
/// The transaction invokes both callbacks while its live ambient transaction is committing. Subject
/// property reads and writes on a flow carrying that transaction throw <see cref="InvalidOperationException"/>.
/// Commit-owned synchronous local replay remains allowed, including property hooks, change handlers,
/// and derived cascades initiated by that replay. This authorization does not extend into these callbacks.
/// Disposed and terminal transactions are inactive and use normal property access semantics.
/// </remarks>
public interface ITransactionWriter : ISingletonContextService<ITransactionWriter>
{
    /// <summary>
    /// Writes every source-bound change to its source (best-effort per source) and reports the
    /// per-change outcome. Does not apply anything to the local model. Local (no-source) changes are
    /// neither written nor returned; the transaction applies them. Also enforces the SingleWrite
    /// requirement, since only the writer knows the source mappings.
    /// </summary>
    /// <remarks>
    /// Do not read or write subject properties from this callback, and do not suppress ambient
    /// execution-context flow to bypass the committing access boundary. This includes getters: sibling
    /// or landed-model state is outside the frozen snapshot and can make a source payload inconsistent.
    /// Construct source operations from <paramref name="changes"/>, <paramref name="requirement"/>, and
    /// writer-owned configuration, and any required subject state captured before
    /// <see cref="SubjectTransaction.CommitAsync"/>.
    ///
    /// Report failures via <see cref="SourceWriteResult"/>, never throw: a throw returns neither the
    /// written set nor the revert state, so the transaction fails terminally with nothing reverted.
    /// After (and only after) a source accepts a change, replace that slot in <paramref name="changes"/>
    /// with the same change marked by the accepting source (<see cref="SubjectPropertyChange.WithOrigin"/>),
    /// so the commit's local apply and revert notifications are recognized as echoes by that source's
    /// outbound queue. Change only the slot's <see cref="SubjectPropertyChange.Origin"/>, never move a
    /// change to a different slot, and leave failed slots untouched. Not marking at all is allowed, but
    /// the queue then re-pushes each committed value.
    /// <paramref name="changes"/> is a pooled buffer owned by the commit: do not retain or mutate it after
    /// the returned task completes. Parallel per-source writers must touch only their own source's slots.
    /// Enable <see cref="SubjectTransaction.ValidateWriterContract"/> while developing an implementation.
    /// </remarks>
    /// <param name="changes">The commit snapshot to classify, write, and mark in place.</param>
    /// <param name="requirement">The transaction requirement for validation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The result reporting which source-bound changes reached their source (<see cref="SourceWriteResult.Written"/>),
    /// which failed (<see cref="SourceWriteResult.Failed"/>), and the corresponding errors.
    /// </returns>
    ValueTask<SourceWriteResult> WriteToSourcesAsync(
        Memory<SubjectPropertyChange> changes,
        TransactionRequirement requirement,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reverts previously-written changes at their sources (for rollback) by writing the inverse
    /// values back to each source.
    /// </summary>
    /// <remarks>
    /// Do not read or write subject properties from this callback, and do not suppress ambient
    /// execution-context flow to bypass the committing access boundary. Construct inverse source
    /// operations from <paramref name="written"/>, <paramref name="revertState"/>, writer-owned
    /// configuration, and any required subject state captured before <see cref="SubjectTransaction.CommitAsync"/>.
    ///
    /// Implementations must report revert failures via <see cref="SourceRevertResult"/> rather than
    /// throwing. A throw is treated as if every requested revert failed and the commit fails terminally.
    /// </remarks>
    /// <param name="written">The previously-written changes to revert, as returned in <see cref="SourceWriteResult.Written"/>.</param>
    /// <param name="revertState">The opaque writer-owned state from <see cref="SourceWriteResult.RevertState"/> identifying the original target sources.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result reporting any revert failures and their errors.</returns>
    ValueTask<SourceRevertResult> RevertSourceWritesAsync(
        IReadOnlyList<SubjectPropertyChange> written,
        object? revertState,
        CancellationToken cancellationToken);
}
