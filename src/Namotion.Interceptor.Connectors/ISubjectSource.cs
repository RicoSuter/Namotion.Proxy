using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents a source that synchronizes data FROM an external system to a subject.
/// The external system is the source of truth; the C# object is a replica.
/// Sources must claim ownership of properties by calling <c>SetSource(this)</c> during initialization.
/// </summary>
/// <remarks>
/// <see cref="ISubjectConnector.RootSubject"/>, <see cref="State"/>, <see cref="StateChangeTime"/> and
/// <see cref="LastSynchronizedAt"/> must be lock-free. <c>SourceMonitor</c> reads them while holding
/// its own lock, and <see cref="StateChanged"/> is raised from inside the source's transition lock,
/// so a getter that took that lock would close an ABBA cycle. <see cref="SubjectSourceBase"/>
/// satisfies this.
/// <para>
/// Implementing this directly instead of deriving from <see cref="SubjectSourceBase"/> means
/// registering with every monitor from <c>subject.Context.GetServices&lt;SourceMonitor&gt;()</c> on
/// start and unregistering on stop. Skipping that is silent rather than fatal: the source never
/// appears in the stream, and branch-scoped waits covering it complete vacuously.
/// </para>
/// </remarks>
public interface ISubjectSource : ISubjectConnector
{
    /// <summary>
    /// Gets the maximum number of property changes that can be applied in a single batch (0 = no limit).
    /// </summary>
    public int WriteBatchSize { get; }

    /// <summary>
    /// Applies a set of property changes to the source.
    /// Returns a <see cref="WriteResult"/> indicating which changes succeeded.
    /// On partial failure, returns the subset of changes that were successfully written.
    /// </summary>
    /// <remarks>
    /// Thread-safety is handled automatically by <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/>,
    /// which should be used by all callers instead of this method directly.
    /// Implement <see cref="ISupportsConcurrentWrites"/> to opt-out of automatic synchronization.
    /// Do not retain <paramref name="changes"/> after the returned task completes: the caller may
    /// reuse or mutate the underlying buffer.
    /// When reporting an error, enumerate the failed changes; see <see cref="WriteResult.FailedChanges"/>.
    /// <para>
    /// Build the payload from <paramref name="changes"/> alone, never by reading subject properties.
    /// Under transactions the built-in writer calls this on the committing flow, where property reads
    /// and writes throw <see cref="InvalidOperationException"/>: sibling and landed-model state is
    /// outside the frozen snapshot and can make the payload inconsistent with it. Capture any other
    /// subject state the write needs before <see cref="Tracking.Transactions.SubjectTransaction.CommitAsync"/>,
    /// and see <see cref="Tracking.Transactions.ITransactionWriter"/> for the full committing access boundary.
    /// </para>
    /// </remarks>
    /// <param name="changes">The collection of subject property changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="WriteResult"/> containing successful changes and any error.</returns>
    ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the initial state from the external authoritative system and returns a delegate that applies it to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the initial state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the source's synchronization state. Describes the inbound direction only: the model
    /// mirroring the external system. Outbound backlog is reported by the connector's diagnostics.
    /// </summary>
    SourceState State { get; }

    /// <summary>
    /// Gets when <see cref="State"/> last changed. Stamped at construction, so it is always meaningful.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="LastSynchronizedAt"/>, which records whether a good period ever began
    /// rather than when the current one did.
    /// </remarks>
    DateTimeOffset StateChangeTime { get; }

    /// <summary>
    /// Gets when the most recent initial synchronization completed, or <c>null</c> if it never has.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not only diagnostic: branch waits use it to tell a source that stopped having
    /// delivered from one that never did. An implementation reaching
    /// <see cref="SourceState.Synchronized"/> must stamp it, never clear it, and make the stamp
    /// visible before <see cref="State"/> becomes <see cref="SourceState.Stopped"/>, or every branch
    /// it participates in reports <see cref="SourceSynchronizationResult.Incomplete"/> once it stops.
    /// Only a stopped source's value is read, so <c>null</c> while synchronized costs nothing.
    /// Stamped only on the transition into <see cref="SourceState.Synchronized"/>, so it cannot say
    /// when synchronization was lost; <see cref="StateChangeTime"/> answers that.
    /// </remarks>
    DateTimeOffset? LastSynchronizedAt { get; }

    /// <summary>
    /// Gets what this source reports about its transport and its buffers.
    /// </summary>
    new SourceDiagnostics Diagnostics { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    /// <remarks>
    /// Raised synchronously on the transitioning thread, inside the source's transition lock, so
    /// handlers must be observe-only: no blocking, and no causing a transition of any source. The
    /// lock is reentrant, so a nested transition would publish out of order. Mutating consumers
    /// belong on the SourceMonitor stream, where delivery is queued outside all locks.
    /// </remarks>
    event EventHandler<SourceEvent>? StateChanged;
}
