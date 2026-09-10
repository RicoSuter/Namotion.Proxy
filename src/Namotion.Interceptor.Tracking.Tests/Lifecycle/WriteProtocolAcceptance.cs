namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Shared deadlines for the write protocol acceptance repros, and the register of what each repro
/// pins and what would quietly stop it pinning anything.
/// </summary>
/// <remarks>
/// CrossContextGateDeadlockTests, attach-callback half. Two lifecycle callbacks writing into each
/// other's contexts acquire two topology gates in opposite order, because the gate is entered before
/// the write chain resolves and the callback guard lives inside the chain. Depends on attach
/// callbacks running under the gate. It asserts the rejection and not only that both threads
/// finished, so a world where callbacks stop holding the gate fails on the missing rejection.
///
/// CrossContextGateDeadlockTests, downstream-interceptor half. The same acquisition order with no
/// callback involved, so no reentrancy guard is consulted at all. Depends on the gate being entered
/// around the whole write chain rather than inside the lifecycle. If gate entry moved inside the
/// lifecycle, an interceptor outside it would hold nothing and both writes would simply complete;
/// the asserted rejection is what catches that, and chain position alone does not.
///
/// TerminalStoreContractTests, foreign-subject half. Fixed, and now the record of a known contract
/// boundary rather than a repro. Only the proposed value was claimed before the terminal ran, so a
/// foreign-context subject stored by a normalizing terminal was rejected after the baseline had been
/// committed. Claiming the stored value moves the rejection ahead of every graph mutation, and the
/// test asserts that per kind of state rather than through one probe. The backing field is the
/// boundary and is asserted as such, deliberately in the negative: it still holds what the terminal
/// stored, because the only store the framework has is the terminal the subject handed it and this
/// one ignores the value it is given, so no replay restores anything. A change that starts restoring
/// the field fails that assertion, which is the point: the contract would have moved and that should
/// be seen rather than absorbed. The boundary is stated for consumers in the WriteProperty remarks
/// and in docs/design/tracking-lifecycle.md, so this entry is the test-side reminder, not the
/// argument.
///
/// TerminalStoreContractTests, reordering half. Passes today and is a parity guard, not a repro: a
/// normalizing setter that stores a reordered subset of the proposed subjects must stay legal. It is
/// the only case that runs the stored-value claim on a value that has to pass, so it is what stops
/// an over-aggressive fix. Its stored list is a different instance from the proposed one, so a
/// reference-equality short circuit cannot hide the rewrite, and it is also what pins that the
/// stored-value claim is skipped when the terminal stored exactly what it was given.
///
/// NormalizingSetterDerivedRaceTests. A derived recalculation on another thread convicted a subject
/// that a normalizing setter had stored before the reconcile attached it. Parks in the authoritative
/// getter the lifecycle rereads between its own next and its reconcile, which the lifecycle invokes
/// itself, so no interceptor ordering can move it. Parking in the stored setter was measured and
/// rejected: that delegate runs under the terminal lock the reading thread also takes, so the reader
/// blocks instead of racing. The guard asserts the backing field held the substituted subject when
/// the park ran, so a park landing outside the window fails the test rather than passing it. Its
/// non-intercepted twin is the one that pins the mechanism: reading through a plain accessor records
/// no dependency, so no recalculation cascade can reach the probe and the value comes back only
/// through the booking the withholding recalculation made with the lifecycle. Both assert that the
/// re-evaluation happened rather than only that the final read looks right, because reading a
/// derived property re-invokes its getter and would answer correctly either way.
/// ConcurrentPublicationVerdictTests holds the other side, that a deferral is not an acquittal.
///
/// AttachResidueTests, root half. A rejected explicit attach left residue behind. Asserts each kind
/// of state the attach would have written separately, so a partial rollback reports which part
/// leaked. Depends on discovery invoking user code before the root is claimed, and on seeding
/// re-reading the property getter rather than reusing the discovery snapshot; the guard asserts the
/// root is still unattached when the scan parks.
///
/// AttachResidueTests, sibling half. The residue a claim-only rollback cannot reach: a subject the
/// seed published before it threw was never in the claimed set, because the concurrent write
/// installed it after the scan. Both children arrive in one property value, so the seed attaches
/// them in that list's order rather than in whatever order the subject enumerates its properties,
/// which is what makes the published-then-rejected sequence deterministic.
/// </remarks>
internal static class WriteProtocolAcceptance
{
    /// <summary>How long a thread waits to meet another thread at a handoff before giving up.</summary>
    public static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a bounded join waits before a stuck thread is reported as a failure.</summary>
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);
}
