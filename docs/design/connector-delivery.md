# Connector delivery: why the rules are what they are

Maintainer notes for the outbound delivery path. Consumer-facing behaviour is in
[connectors.md](../connectors.md); this covers the reasoning behind it, which is not recoverable from
the code and has been rediscovered more than once.

## The invariant

> A change may be dropped only if a later commit will carry the settled value in its place.

Everything else follows from that sentence, including the parts that look arbitrary.

## Why commit order and not value comparison

The obvious implementation asks "does this change still carry the property's current value?" and drops
it when the answer is no. It was implemented, shipped in a branch, and replaced. Three reasons:

1. **It cannot judge derived or runtime-registered properties.** The comparison is only meaningful when
   the getter returns what the write stored. A derived getter recomputes and can return a fresh instance
   that never compares equal; a runtime-registered property carries a caller-supplied getter that need
   not read stored state at all. Both were therefore exempted and delivered unconditionally, which is
   every property the OPC UA client loader creates.
2. **It boxes.** Comparing `object?` against `object?` allocated 48 bytes per delivered change on
   value-typed properties, on a path built to allocate nothing.
3. **It cannot see the difference that matters.** A value equal to the current one and a value that a
   later commit will re-deliver are indistinguishable by value, but only the second is safe to drop.

## Why source-originated commits do not advance the marker

This is the single most load-bearing line in the design, and it looks like a special case.

A commit that came from the source is skipped as an echo when that source's queue is drained. If it
counted as superseding, a change could be dropped against a commit that is then never delivered, which
breaks the invariant directly. The failure needs no concurrency: write A, it reaches the source, write
B, then the source's notification for A arrives late and commits locally at a higher revision than B.
B is dropped, the echo is skipped, nothing is sent, and both ends settle on A. The user's write is gone
with no error.

Issue #373 covers the general form: an echo's revision is stamped when we apply it, not when the source
produced it, so it cannot be ranked against local writes at all.

## Why a server ranks against a different marker

The argument above rests on the source having produced its value before it saw our write. That holds for
a connector talking to something remote. It does not hold for a server, where a client's write is the
thing being applied, so a commit that predates it is genuinely older.

Ranking against the non-source marker there fails the same invariant from the other side. A local commit
that predates the client's write and reaches the write loop late is not superseded, so it is written out
after the client's value, leaving the clients on our older value while the subject holds theirs.

So which commits may supersede is a property of the sink, not of the change: `ChangeDeliveryRule`,
chosen at four construction sites: the three servers, and once in `SubjectSourceBase` for every client
source. The OPC UA server repeats the decision inside the node
manager lock, because a client write takes that same lock and can land between the batch being accepted
and written.

**Check the precondition, not the metaphor.** `SourceValuesAreSettled` is sound only if every commit the processor
skips as its own echo has already reached the destination when it is applied. The three servers satisfy
that differently, and the difference is load-bearing:

- **OPC UA** applies with `SetValueFromSource(this, ...)`, so the apply *is* echo-skipped. It is sound
  because the SDK wrote the node before `StateChanged` fired, so the value is already there. Without
  `SourceValuesAreSettled` the two stores diverge permanently, which is the failure this rule was added for.
- **MQTT and WebSocket** apply under a foreign source, `_mqttClientSource` and the originating connection,
  so nothing is echo-skipped and the precondition holds vacuously. Their failure without `SourceValuesAreSettled` is
  milder and different: within one flush the merger already picks by revision, so it only bites when the
  client's value and the straggler land in different flushes.

Unifying those conventions on `SetValueFromSource(this, ...)` would look like a tidy-up and would make
`SourceValuesAreSettled` unsound for MQTT immediately, because the broker does not distribute a client's
message itself: it handles the message and sets `ProcessPublish` to false, so the value reaches the other
clients only when the server relays it in order. Same for WebSocket, which has no store at all.

The OPC UA case was invisible until #425. Before it, the server applied its own node writes back to the
subject, which converged the two stores by accident while corrupting them in other ways.

## Why the batch survivor spans the batch

The survivor's old value comes from the lowest revision in the batch and its new value from the highest, rather than from whichever change happened to arrive first and last. Enqueuing happens after the commit and outside the subject lock, so a writer preempted between the two can present an older commit after a newer one. Under concurrent writers that inversion is real rather than theoretical, and taking the first and last arrivals would produce a survivor whose old value postdates its new one.

Everything else on the survivor, its `Revision`, `Origin` and both timestamps, comes from the highest-revision change, so a handler keying off `Origin.Source` sees the newest commit's origin rather than a mixture. Under the arrival-position fallback below the origin and timestamps come from the last arrival instead, and the survivor carries no revision at all.

## Why revision 0 is delivered rather than dropped

A change carrying revision 0 orders against nothing, so staleness is unprovable and it is delivered; a property with one in its batch collapses by arrival position instead, which is what a source saw before revisions existed. A redundant write costs one message, a wrong drop is permanent, so the guard errs toward delivering.

The survivor of such a batch is emitted carrying no revision too. It was chosen by arrival rather than by revision, so ranking it against the property marker could drop it while the higher-revision change whose value it carries has already been merged away in the same batch, leaving nothing to re-deliver.

No committed change arrives carrying revision 0: every published change comes from a write terminal and carries a revision, including derived recomputations. The batch collapses manufacture it deliberately, though, through `WithoutRevision`, which is the case the paragraph above describes, so this is a live path rather than a guard against something that cannot happen.

## Why the written-out mark is sticky and not per source

The mark that says a connector has written a property out never clears, and lives in the subject's property data rather than per source.

It cannot clear on an inbound event: nothing observable on this side proves that an earlier write of ours did not land on the source after a transaction's direct write, so clearing would be a bet against an ordering the client cannot see, and losing it silently strands a committed transaction value.

It is not per source because it decides only whether a confirmation is written back, and a confirmation carries the current value. The worst a foreign connector's mark can cost is one redundant write of the value the source is owed anyway. That is what lets it be a bare flag with no source reference to release. A property written only through transactions never sets it.

## Why the marker is read before FinalizeOrigin

`FinalizeOrigin` demotes a stamped origin to `Local` when the stored value differs from the sent value,
because the local model computed that value. Correct for publishing, wrong for the marker: the write
still originated at the source. Reading after the demotion made a property carrying a clamp or normalize
hook behave differently from one without, so whether a user's write survived depended on whether that
property happened to have a hook.

## Why Confirmed commits do advance it

They are echo-skipped like any other own-source change, so by the rule above they should not. They are
safe for a different reason: `SourceTransactionWriter` stamps `Confirmed` only after the source write
succeeded, so the source genuinely holds that value and needs nothing sent.

That reasoning lives in another assembly, which makes it fragile in both directions. Excluding
`Confirmed` would let an older local write overwrite a confirmed value; extending `Confirmed` stamping
to a path that has not written the source would silently lose writes. Transaction rollback is exactly
such a path and is tracked separately.

## Why the connect window reverses source-wins

At connect, a parked local write and the initial-state load cannot be ordered against each other, for
the same reason an echo cannot: the load carries the source's state as of the connect and says nothing
about whether it precedes or follows a write made moments earlier. The earlier rule resolved that
ambiguity by discarding the write, silently. It now resolves it the other way, and the source converges
to the local value.

Both directions keep the two ends in sync. What differs is whether a committed write can vanish without
an error.

## What actually guarantees convergence

Not the conflict rule. Two properties of the delivery path:

- The newest local commit is never dropped, since nothing supersedes it, so the source always receives
  the model's settled value.
- The source's notifications carry its own value back, so the model converges to what the source holds.

A source that neither reports values back nor answers reads is outside this, and no local mechanism can
close it. That is #373's territory: it needs a read-after-write fence or an in-band echo fence.

## The property index hysteresis

`ChangeMerger` trims its property index after a burst, but only once the narrow condition has held for
several consecutive flushes. Trimming on the first narrow batch measured as +17% allocation on the
delivery benchmark, because flush widths vary constantly under load and each narrow batch trimmed an
index the next wide one immediately regrew. A single batch cannot distinguish a working set from a burst
artifact; observing over time can.
