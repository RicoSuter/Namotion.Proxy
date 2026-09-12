# Hosted Service Ownership: Internal Design

This document describes the internal concurrency model of `Namotion.Interceptor.Hosting`: how `HostedServiceHandler`, `HostedServiceTarget` and `HostedServiceGate` decide when a subject bound hosted service starts, stops and is disposed. For user-facing documentation, see the [Hosting](../hosting.md) documentation.

## Overview

Everything below exists to keep [the rule](../hosting.md#the-rule) true under concurrency: lifecycle events fire from inside `LifecycleInterceptor`'s `_attachedSubjects` lock, callers attach and detach from arbitrary threads, and the host starts and drains underneath both.

The unit of management is a **target**: either a subject that implements `IHostedService`, or one factory attachment. Each target owns a serialized **transition chain**, so start, stop and dispose for that one target never interleave, while transitions for unrelated targets run concurrently.

Several decisions below look like they could be simplified. Each was measured, and the obvious simplification was measured to be wrong. The reason is recorded with the decision, because the reason is the only thing that stops it being simplified again. Where the code already carries the whole argument next to the mechanism, this document names the mechanism and points at it rather than repeating it.

## Data Structures

### `HostedServiceTarget`

One per managed thing, stored in the subject's `Data` bag:

```
Factory               Func<IHostedService>?    the factory for an attachment, null for a subject target
Subject               IHostedService?          the subject for a subject target, null for an attachment
_current              IHostedService?          the running instance, or null
_fault                Exception?               the exception from the last failed transition
_owner                HostedServiceHandler?    the handler that claimed this target
_lastFactoryInstance  IHostedService?          the instance the previous factory call returned, kept for the life of the attachment
_detached             bool                     set by an explicit detach, refuses every later start
_tail                 Task                     the transition chain
_sync                 object                   guards _tail and _detached
TransitionGate        Func<Task>?              test seam awaited at the top of every body, null in production
ChainLockGate         Action?                  test seam invoked inside _sync between the take and the append, null in production
```

The fields are synchronized differently, and each difference is deliberate:

- `Current` and `Fault` use `Volatile.Read` and `Volatile.Write`. Awaiting a transition already gives its awaiter a happens before edge, but a diagnostics poll reads `Current` from an unrelated thread with no such edge, so this is required rather than decorative.
- `Owner` uses `Volatile.Read`, and it is never written with `Volatile.Write`. The only two writers are `TryTakeOwnership` and `ReleaseOwnership`, and both go through `Interlocked.CompareExchange`, which carries the fence itself. A plain write would lose the compare and swap that decides which of two racing handlers claims the target.
- `_lastFactoryInstance` is neither volatile nor locked, and it is never cleared. Both are deliberate, and both reasons are on `HostedServiceTarget.TryRecordFactoryInstance`.
- `_detached` is written and read under `_sync`, which is what pairs it with the append (see [Refusing a start for an attachment a detach already removed](#refusing-a-start-for-an-attachment-a-detach-already-removed)).

`IsHandlerOwnedInstance` is simply `Factory is not null`, and it is the whole disposal policy: the handler created the instance if and only if it invoked a factory to get it, so it disposes attachment instances and never disposes a subject.

### `HostedServiceHandler`

```
_logger          ILogger?                set once by the service provider factory, volatile
_gate            HostedServiceGate       NotStarted, Running, Draining, Drained
_owned           ConcurrentDictionary    target -> subject, for targets whose ownership this handler installed
_liveSubjects    ConcurrentDictionary    subject -> unused, for subjects in the graph that host something
_inFlight        int                     transitions this handler appended that have not finished
DrainGate          Func<Task>?           test seam, null in production
OwnershipTakenGate Action?               test seam, null in production
LivenessWriteGate  Action?               test seam, null in production
LivenessReadGate   Action?               test seam, null in production
DrainAppendGate    Func<Task>?           test seam, null in production
DrainReleaseGate   Func<Task>?           test seam, null in production
```

`_owned` and `_inFlight` are the shutdown state, and they are two facts rather than one because they have different lifetimes:

- **What to stop and release** is `_owned`, and it lives as long as an ownership.
- **What to wait for** is `_inFlight`, and it lives as long as a transition.

An earlier design conflated them into a running set plus a set of in flight stops, where a target left the running set when its stop was appended. Correctness then rested on the order of those writes against the order of the drain's two unlocked reads, and four separate defects of that one shape were found before it was replaced. `_owned` maps a target to its subject rather than being a set, because the drain has to group the targets it stops per subject to reproduce the ordering a context detach gives.

The seams are documented where they are declared; each one holds open a window between two statements that are adjacent in production.

`_liveSubjects` holds an entry only for subjects that host something, not for every subject that attaches. Every reader of liveness holds a `HostedServiceTarget` when it reads, so a subject with no target has no reader, and recording one for the whole graph cost a concurrent dictionary write per subject on attach and a removal per subject on detach: measured on a 20,000 subject graph that hosts nothing at 95 bytes per subject into a fresh context and 40 bytes into a warm one, the difference being that a `ConcurrentDictionary` never shrinks its table, so only the first attach grows it. The removal on detach allocates nothing, which is why the attach half of that cost is the half `HostingLifecycleBenchmark` can see.

Two facts keep that safe, and both are load bearing:

- **A subject gaining its first target after it entered the graph records its own liveness**, in `MarkLiveIfAttached`, because that is the one moment the answer cannot be taken from a target. The membership question goes to `LifecycleInterceptor.TryRunWhileAttached`, and the write happens *inside* its callback. Reading membership and then writing releases the graph lock in between, and a graph move landing in that gap makes the write land on the opposite answer: an attach that recorded liveness has it removed again so its service never starts, or a detach that cleared it has it re-armed so a service starts for a subject that has left the graph. Pinned by `HostedServiceHandlerTests.WhenAnAttachmentRecordsLiveness_ThenAGraphMoveCannotLandBetweenTheCheckAndTheWrite`, which drives it through a seam because the window is nanoseconds wide.
- **A context detach clears liveness for every subject that has *ever* hosted something**, not merely for one that hosts something at that moment. `RemoveAttachment` stores null rather than removing its data key, so the key outlives the attachments and `TryGetHostedServiceAttachments` reports "has ever hosted" at the cost of the same single lookup. Keying the fast path off the current attachments instead leaves an entry behind that outlives the subject's membership, and a start already queued against the detached attachment then re-reads liveness in its body, finds it, and creates and starts an instance for a subject that has left the graph. Pinned by `WhenAnAttachmentIsDetachedAndTheSubjectLeavesBeforeItsQueuedStartRuns_ThenNothingIsCreated`, which is deterministic because the host is never started.

Retained memory is therefore linear in the number of hosted services rather than in the size of the graph, and a graph with no hosted services anywhere pays nothing. `_owned` is bounded the same way, because a target only enters it through a take.

One limit is accepted rather than guarded. `MarkLiveIfAttached` asks every `LifecycleInterceptor` reachable from the subject and records if any reports the subject attached, because a subject in two hosting enabled contexts is live for both handlers and one interceptor not holding it says nothing about the other. A handler can therefore be marked live on the strength of a graph it does not itself serve, and the consequence goes one step further than that phrasing suggests: the handler then takes ownership and starts the service, and no context detach on the other interceptor's side ever reaches it, so nothing stops that instance before host shutdown drains what the handler owns.

The reachable shape is one hosting enabled context plus one lifecycle only context, not two hosting contexts, because `TryGetService<HostedServiceHandler>()` throws when two hosting contexts are reachable. Reaching it needs a manual `AddFallbackContext` to put a subject in a second lifecycle enabled graph: `ContextInheritanceHandler` fires only at reference count one, so a subject already in one graph never gains a second context through ordinary assignment. Recorded here rather than defended against, because the guard would have to distinguish which interceptor's graph a handler serves, which the handler does not know.

The logger is resolved through a callback rather than injected because `WithHostedServices` constructs the handler while the context is being configured, which is before any service provider exists; the registration it adds to the `IServiceCollection` assigns the logger when the provider builds it.

### Where records live

Records live on subjects rather than in the handler, so nothing in the handler roots a detached subject and a factory survives a detach. That survival is what lets a subject that leaves the graph and re-enters it get working services again: the next context attach invokes the surviving factory, so no restart contract is needed from the service.

Attachments live under one data key as an `ImmutableArray`, and the subject target under another. The correctness and allocation constraints on both paths are on `InterceptorHostingExtensions.AddAttachment` and `InterceptorHostingExtensions.GetOrAddSubjectTarget`; the allocation half is pinned by `HostedServiceHandlerTests.WhenASubjectTargetAlreadyExists_ThenReadingItAllocatesNothing`.

The handler carries `[RunsAfter(typeof(ContextInheritanceHandler))]`, because it resolves startup completion deferrers from `subject.Context` and for a subject entering as a child it is `ContextInheritanceHandler` that installs the parent context as a fallback. Ahead of the descent the child's context would still be its private executor and would resolve nothing. See [Handler Order Around the Descent](tracking-lifecycle.md#handler-order-around-the-descent).

## Why Per Target Chains Rather Than One Queue

An earlier implementation posted every start and stop to a single `BufferBlock` drained by one consumer loop. One queue for a whole handler couples services that have nothing to do with each other, and the coupling was not theoretical:

- **Self deadlock across the whole handler.** The loop awaited each action to completion before taking the next, so any action that awaited another action, which the awaitable attach and detach paths do, wedged the loop for every service in the context. Per target chains do not remove that cycle, they contain it to one chain (see [Residual Hazards](#residual-hazards)).
- **Shutdown dropped queued work.** Shutdown cancelled the loop's token first, so anything still queued never ran and any caller awaiting it waited forever. A host disposed without ever having started returned early and left every queued action and every awaiter hanging.
- **Ordering dependence on dependency injection registration order.** The loop only began draining when the handler's own `StartAsync` ran, and hosted services start in registration order, so a hosted service registered ahead of `WithHostedServices` that awaited an attach hung host startup. `HostedServiceGate` and `EnsureStarted` replace that, pinned by `AddSubjectTests.WhenAddSubjectIsRegisteredBeforeWithHostedServices_ThenStartupDoesNotHang`.
- **Cost proportional to the number of subjects.** Every start paid the delay the start body then carried in series, so N subjects cost N times 50 ms of host startup.

Per target serialization keeps the only ordering property that was ever needed, that one target's own transitions never overlap, and buys the one cross target ordering genuinely required with a completion signal rather than with a global executor. That starts for subjects entering the graph together overlap rather than running in series is pinned by `HostedServiceStartupShapeTests.WhenManySubjectsEnterTheGraphTogether_ThenTheirStartsOverlap`, without measuring time.

One guarantee is deliberately given up. `LifecycleInterceptor.DetachFromProperty` invokes a parent's handlers before recursing into children, so under one queue a parent hosted subject stopped before hosted descendants; under per target chains they stop concurrently. Hosted services at different depths are independent by construction and nothing here depends on the old order. If a consumer ever needs it, the fix is another completion signal, not a global executor.

## Appending a Transition

All three append paths, `AppendAsync`, `AppendIfOwnedAsync` and `TryTakeOwnershipAndAppendAsync`, end in `HostedServiceTarget.AppendCore`, under the target's `_sync` lock, where the comment records why the lock and `TaskScheduler.Default` are each required. What the second and third add ahead of that call is a decision taken inside the same lock acquisition: ownership for [the drain's appends](#why-the-ownership-decision-is-inside-the-chain-lock), liveness and ownership together for [a take](#the-read-inside-the-chain-lock). Two appenders race there in ordinary use: a lifecycle event appends while holding `_attachedSubjects`, and a user driven detach appends from a pool thread. Pinned by `HostedServiceTargetTests.WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap`.

`RunAsync` catches everything, so the chain is never faulted. Bodies record failures into `Fault` and log them instead.

### Appending never blocks and never runs the body

A default `ContinueWith` never executes inline on the appending thread, so a lifecycle handler may append while the lifecycle interceptor holds `_attachedSubjects`, and no transition **body** ever runs under that lock. That is structural rather than a rule somebody has to remember, and it is pinned by `HostedServiceTargetTests.WhenATransitionIsAppended_ThenItDoesNotRunOnTheAppendingThread`.

Third party code does run under that lock, though, and it is not the body: taking and releasing the startup completion holds calls into `IStartupCompletionDeferrer` synchronously from the lifecycle event. That is [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own).

## Appending at Event Time

Every append happens when the lifecycle event fires, never deferred into another transition. This is the rule that makes moving a subject through the graph work, and the obvious alternative was measured to break it.

### Why a composite transition is wrong

The requirement is that a subject's own stop completes before the attachments it uses are disposed. `BackgroundService.StopAsync` awaits its execute task, so a subject's stop is slow, and an attachment disposed underneath it is observed as already disposed by code still unwinding inside `ExecuteAsync`. The tempting fix is one composite transition on the subject's chain that stops the subject and then stops its attachments. A detach immediately followed by an attach shows why it is wrong:

1. The subject leaves the graph. The composite transition is appended to the **subject's** chain. It has not run yet.
2. The subject re-enters the graph. The create and start for the attachment is appended to the **attachment's** chain, which is empty, so it runs. Instance B is now running.
3. The composite transition finally runs. It stops the subject, then reads `Current` on the attachment and finds instance B, which it stops and disposes.

Instance A, the one that was running before the detach, is never disposed. Instance B, the one the graph expects to be running, is stopped and disposed. The subject is in the graph with nothing running and no error anywhere.

### What a context detach appends

So `DetachSubject` appends immediately, under `lock (_attachedSubjects)`, to every affected chain:

- a stop on the subject's chain if the subject is an `IHostedService` this handler owns, which sets a `subjectStopped` completion in a `finally`, so cancellation and failure release it too;
- for each attachment this handler owns, a stop on that attachment's own chain that first awaits `subjectStopped`, then stops, disposes and clears `Current`.

`subjectStopped` is allocated only when the handler is appending the subject's own stop **and** the subject has at least one attachment to order behind it; the reasons are at that allocation and at the null wait below it. It returns before allocating anything at all for a subject that hosts neither, which is essentially every subject in a detaching graph. Pinned by `HostedServiceHandlerTests.WhenASubjectWithoutHostedServicesIsDetached_ThenNothingIsAllocated`.

Ordering holds because both appends happen under the lifecycle lock, so any later re-attach queues behind them on the same chains. The wait is acyclic: an attachment chain waits on the subject's signal and the subject's chain waits on nothing, **provided the subject's stop does not itself wait on an attachment chain**. That proviso is [residual hazard 3](#3-a-subject-that-detaches-its-own-attachment-while-unwinding).

Pinned by `HostedServiceHandlerRaceTests.WhenAReAttachLandsWhileTheSubjectStopIsHeld_ThenAFreshInstanceRunsAndTheOldOneIsDisposed`, which holds the subject's stop on a seam so the re-attach provably lands mid stop, and by `HostedServiceHandlerRaceTests.WhenASubjectLeavesTheGraph_ThenItStopsBeforeItsAttachmentIsDisposed` for the ordering itself. Shutdown builds the same shape from its own code in `StopAsync` rather than calling this path, so it needs its own test and has one, `HostedServiceHandlerRaceTests.WhenTheHostDrains_ThenASubjectStopsBeforeItsAttachmentIsDisposed`. Dropping the wait from one path fails that path's test and leaves the other green.

### What `subjectStopped` actually means

It means "the subject's stop returned", which equals "`ExecuteAsync` unwound" only when the stop is not cancelled: since .NET 8, `BackgroundService.StopAsync` awaits its execute task with `ConfigureAwaitOptions.SuppressThrowing`, so on a cancelled token it returns normally while `ExecuteAsync` is still running. Graph driven detaches pass `CancellationToken.None` and get the strong reading; host shutdown passes the stopping token and gets the weak one. That is documented rather than fixed, because forcing the strong reading at shutdown would mean ignoring `ShutdownTimeout`.

Context attach is the mirror image, also under the lock: a start on the subject's chain if it is an `IHostedService`, and a create and start on each attachment's chain.

### A subject created from inside a lifecycle handler

Giving a container a default child from the container's own context attach is a legitimate pattern, and it is the one place where a subject enters the graph while another subject's attach event is still being dispatched. `ChildCreatingLifecycleHandler` in the test project is the shape, and the remarks on `NestedAttachTests` record why the child's attach is an ordinary one rather than a re-entrant call. Both handler orders reach the same state, pinned by `NestedAttachTests.WhenAnAttachHandlerCreatesTheContainersChild_ThenBothStartOnceAndEachOwnsItsOwnTarget`.

The way back out is the same shape. The child is reached by the detach cascade through the container's property rather than by anything an explicit `AttachHostedService` left behind, and both ownerships are released, which is what lets a re-attach start the same two subjects again rather than finding targets no handler can claim. Pinned by `NestedAttachTests.WhenAContainerWhoseChildAnAttachHandlerCreatedLeavesTheGraph_ThenBothStopAndTheGraphCanRunThemAgain`.

The one caller that really does re-enter `AttachSubject` is an `IStartupCompletionDeferrer`, because `TakeStartupHolds` calls it synchronously from inside `TryTakeOwnershipAndStart`, which is inside the outer `AttachSubject`. A deferrer that assigns a subject typed property therefore runs the whole inner attach before the outer call has taken its own target. Nothing is shared between the two: liveness is per subject, ownership is per target, and the counted holds let the inner attach take and release its own while the outer one is still outstanding. Pinned by `NestedAttachTests.WhenADeferrerCreatesTheChildWhileTheContainersOwnAttachIsStillRunning_ThenBothStartOnceAndEveryHoldIsReleased`, which reads the container's owner from inside the deferrer to prove the inner attach really did run first. This is [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own) territory rather than a recommendation: what it costs a deferrer is the constraint stated there, not re-entrancy.

## Ownership

A target's `Owner` is taken with `Interlocked.CompareExchange`. Finding this handler already installed counts as success; only losing to a different handler means do nothing. `HostedServiceTarget.TryTakeOwnership` reports which of the two successes the caller got, because a caller that has to undo its own take must leave an earlier one alone.

The take also records the target in the handler's `_owned`, and **only on the install**, never on the repeat. `ReleaseOwnership` retires that record, and only when its compare and exchange matched. Writing only on the install is what makes membership something no call can gain without also owning the undo for it: every install is followed by the gate re-read, which releases when the handler is draining.

**The exchange and the record are taken under `_ownershipSync`, and that lock is not decorative.** They are one fact and they were not atomic against each other: a release landing between an install and its record nulls the owner, finds no record to retire, and leaves a record that no later release can ever match, because the owner it would have matched on is already gone. Nothing then retires it, including the take's own liveness undo. Measured at tens of leaks per two million races before the lock, and pinned by `HostedServiceTargetTests.WhenATakeRacesARelease_ThenTheOwnerAndTheRecordNeverDisagree`, which sweeps the releasing thread across the window and fails 8 of 8 runs without the lock.

That test asserts both directions, because the obvious alternative trades a leak for something worse. Retiring the record unconditionally rather than on a matched exchange leaves the mirror state instead, a target the handler owns and is running that no drain snapshot contains, and it is both more common and never stopped: measured at 55,640 against 26 per two million.

The lock is its own rather than the chain lock. A context detach releases every attachment target it enumerates, including ones whose chain lock a concurrent attach is holding, so releasing under the chain lock deadlocks that pair, which `HostedServiceHandlerRaceTests.WhenADetachReleasesOwnershipBeforeAnAttachTakesIt_ThenTheAttachUndoesItsOwnTake` builds directly. Nothing held under `_ownershipSync` takes another lock, and the take enters it while already holding the chain lock, so the single order is chain lock then ownership lock.

### Ownership is read on context detach, at append time

`DetachSubject` appends a stop only for the targets it owns, and the comment at that read records both what a handler stopping a stranger's instance costs and why the read cannot move into the transition body. Reading it at append time, under the lifecycle lock, is what pairs it with the release a few statements below. Pinned by `HostedServiceHandlerTests.WhenADrainedHandlerSeesAContextDetach_ThenTheLiveHandlersInstancesKeepRunning` and `HostedServiceHandlerTests.WhenANonOwningHandlerSeesAContextDetach_ThenTheOwnersInstanceKeepsRunning`; deleting both guards fails exactly those two, and makes the project take about 40 seconds rather than about 7. The extra time is one test: with the guards gone a context detach appends a stop for a target whose chain lock `HostedServiceHandlerRaceTests.WhenADetachReleasesOwnershipBeforeAnAttachTakesIt_ThenTheAttachUndoesItsOwnTake` is holding at its seam, which is the deadlock that test's own remarks name, and it gets out only because every wait a seam makes is bounded. Before those bounds were uniform this mutation wedged the run rather than reporting the two failures.

### Ownership is released on context detach and on drain

Always after the stops are appended, and never from inside a transition body. Both halves were measured. Releasing from the body clobbers an ownership a re-attach has already retaken, and the consequence is a subject in the graph with nothing running and no error anywhere. Releasing before appending lets a second handler's start land ahead of the first handler's stop on a shared chain.

Release on detach is what lets a subject moved between contexts be picked up by the next handler. Release on drain is what lets a second host run over the same subject instances: without it every target the drained handler owned stays owned by it, and no later handler can ever win the compare and exchange for that target. Deleting the release loop in `StopAsync` fails `HostedServiceHandlerTests.WhenADrainedHandlerSeesAContextDetach_ThenTheLiveHandlersInstancesKeepRunning` and `HostedServiceHandlerTests.WhenADrainedHandlerIsAskedToWaitForAStart_ThenItClaimsNothingAndReportsNothingStarted`. A target whose start faulted is released the same way, because the record follows the take rather than the instance: pinned by `HostedServiceHandlerRaceTests.WhenAStartFaultedAndTheSubjectStayedInTheGraph_ThenTheDrainStillReleasesTheTarget`.

### An explicit detach retires the record without releasing ownership

`DetachHostedService` and `DetachHostedServiceAsync` are the exception to everything above: they stop the target and retire its `_owned` record, and they deliberately leave `Owner` installed.

Adding a release there fails `HostedServiceHandlerTests.WhenAnAwaitedDetachRunsOnAHostThatWasNeverStarted_ThenItStillReturns`, because the start queued ahead of the detach then reads `Owner` as null, refuses, and the stop is left with no instance to dispose. That ordering is the guard `MarkDetached` exists for, and it is why the release cannot simply move.

Retiring the record is not optional, though: without it every attach and detach cycle keeps the target and its subject on the handler for the handler's whole life. Each overload retires its own, so each needs its own case, and both halves of each are pinned: the two cases of `HostedServiceHandlerTests.WhenAnAttachmentIsDetached_ThenTheHandlerStopsRetainingItsTarget` fail when the retirement on the overload that case drives is deleted, and fail on their `Owner` assertion when a release is added. Any future site that stops a target without releasing it inherits the same rule.

The awaiting overload retires before it awaits the stop, so a cancelled wait cannot skip it.

### A faulted awaited attach releases instead

`AttachHostedServiceAsync` is transactional: a start that faults has its attachment removed before the exception propagates. That removal is what puts the target out of reach, since nothing enumerates an attachment that is gone from the subject's data, so the ownership that call took has to be undone there or a host that retries failed attaches leaks a subject per failure.

It **releases** rather than only retiring, which is the opposite of the explicit detach above, because the reason for that asymmetry is absent: the awaited start has already run and faulted, so there is no queued start to refuse itself on a null owner and no stop left holding an instance. It marks the target detached first, so a context attach that snapshotted the attachment before the removal cannot take the target in the gap and start something no later detach could reach. Pinned by `HostedServiceHandlerTests.WhenAnAwaitedAttachFaults_ThenTheHandlerStopsRetainingItsTarget` for the release; the mark has no test, for the reason the other two marks do not.

### Ownership is not what makes two contexts over one subject benign

Reading it that way was measured wrong: a subject reachable from two hosting enabled contexts raises one context attach per context and the **owning** handler sees both, so it appends two starts to the same chain and loses no exchange at any point. What closes it is a one instance guard inside the start body, where the chain serializes the two starts against each other. At append time both would still see an empty target. Pinned by `WithHostedServicesTests.WhenOneSubjectIsReachableFromTwoHostingContexts_ThenItIsStartedOnce`.

## Liveness

**Liveness is a per subject flag, not per target ownership.** It is set when a subject that hosts something enters the graph and cleared when a subject that has ever hosted something leaves it, both under `lock (_attachedSubjects)`, and a start consults it before doing anything. A subject that gains its first target while already in the graph sets its own flag through `MarkLiveIfAttached`, which writes inside `LifecycleInterceptor.TryRunWhileAttached`'s callback so that the write cannot land on a membership answer a graph move has already invalidated.

Chain order covers lifecycle driven appends, because every lifecycle event fires under the lifecycle lock and the handler appends inside it. A user driven `AttachHostedService` appends under the target's own lock only and is unordered against them, so a start needs a second check. Target ownership cannot be that check, and the failure was measured: the attaching path takes ownership itself, so an attach racing a detach passes its own check and leaves the attachment running on a detached subject.

The flag is read on both of the paths a start passes through. On the append path it is read twice inside the chain lock, on entry and again after the take, and what the suite discriminates is the pair rather than either half. Deleting the read on entry alone leaves every test green, because the read after the take absorbs it: the take is installed and then undone instead of never being made. Deleting the read after the take alone fails `HostedServiceHandlerRaceTests.WhenADetachReleasesOwnershipBeforeAnAttachTakesIt_ThenTheAttachUndoesItsOwnTake`, which is the interleaving only that read covers. Deleting both additionally fails `HostedServiceHandlerRaceTests.WhenASubjectLeavesTheGraphBeforeItsAttachTakesTheTarget_ThenTheNextHandlerStillClaimsIt`, whose detach lands before the take rather than inside it, so either read alone refuses it. Inside the start body the flag is still masked by the ownership read beside it: deleting the flag read alone leaves every test green, and deleting the whole body guard fails three.

### The read inside the chain lock

`TryTakeOwnershipAndAppendAsync` performs the liveness read, the ownership take and the append under one acquisition of the target's lock, and its remarks record what splitting them opens. The damage a test can read is the explicit detach order, where the stop runs first, finds nothing to stop, and leaves the start behind it to create an instance the detach has already made unreachable.

That order is what `HostedServiceHandlerRaceTests.WhenADetachRacesTheAppendInsideTheChainLock_ThenTheStartIsOrderedAheadOfTheStop` reads. It holds the section open on `ChainLockGate` while a real `DetachHostedService` runs on its own thread, and releases the seam only once that thread has provably blocked on the chain lock or run to completion, so both builds decide the same way every time rather than by whichever thread wakes first. Splitting the section fails that test and no other; deleting the liveness read or the detached read inside it leaves it green.

### The read inside the start body

The body re-reads liveness **and** ownership before creating anything, covering a detach that lands after the append and before the body runs. The pair is pinned by `HostedServiceHandlerRaceTests.WhenAQueuedStartRunsAfterTheSubjectDetached_ThenNothingIsStarted`, which fails when both reads are deleted.

Neither read alone is discriminated by the suite, and that is a coverage limit rather than redundancy. The two cover different windows, which the comment on `RunStartAsync` sets out. Forcing a body into the window between the liveness clear and the ownership release would mean holding the lifecycle lock open, which blocks every graph write a test needs to make progress, so no seam can drive it.

The consequence if the liveness read were removed is bounded rather than a leak: the same detach has already appended a stop for that target, chains are first in first out, so the instance the start creates is stopped and disposed by that stop. The cost is a needless create and teardown against a subject that has left the graph, which for a connector means a session opened and closed. That is also exactly the damage a context detach skipping its liveness clear reintroduces, which is why the detach fast path turns on "has ever hosted" rather than on "hosts now". Removing the window instead, by releasing ownership before appending the stops, reopens a defect that was measured, so the guard stays and the limit is recorded here.

The subject level flag is what makes an attach onto an already detached subject fail closed: `HostedServiceHandlerRaceTests.WhenAnAttachmentIsAddedAfterTheSubjectDetached_ThenNothingIsStarted` fails when the flag is not consulted on either path.

The documented behaviour that attaching to a subject outside a hosting enabled graph stores the factory and runs nothing has two halves, and the flag is one of them. A subject whose context resolves no handler at all never reaches this code. A subject whose context still resolves the handler but which has left the graph reaches it and is refused here. The flag is also why `WaitForStartAsync` on a handler whose drain has cleared liveness answers immediately instead of queueing behind that drain's stop.

### Refusing a start for an attachment a detach already removed

An explicit `DetachHostedService` clears no liveness, so the flag cannot see it. Both detach overloads therefore call `MarkDetached()` on the target, under the chain lock, after `RemoveAttachment` succeeds and **before** they append their stop. `TryTakeOwnershipAndAppendAsync` reads that mark inside the same lock acquisition that reads liveness. That leaves two orders and no third:

1. The attach wins the lock. The start is appended, the detach's stop lands behind it on that chain, and the stop stops and disposes whatever the start created.
2. The detach's stop wins the lock. The mark is already visible, so nothing is appended, no ownership is taken and no record is written.

Without the mark that start runs after the attachment has already been removed, and the instance it creates is reachable from nothing; the remarks on `TryTakeOwnershipAndAppendAsync` carry that argument.

The two marks are independent, so each needs a test that drives its own overload: deleting the mark from one of them leaves every test that reaches the window through the other green. Pinned by the three cases of `HostedServiceHandlerRaceTests.WhenAnAttachmentIsDetachedBeforeItsStartIsAppended_ThenNothingIsStarted`: two detach through the synchronous overload, the second of those against the awaiting attach, and the third drives the awaiting detach.

## Resolving the Handler on the Public Paths

### The lookup precedes the mutation, on all four entry points

`AttachHostedService`, `AttachHostedServiceAsync`, `DetachHostedService` and `DetachHostedServiceAsync` all resolve the handler before they touch the subject, and in all four that order is load bearing rather than tidy. `TryGetService<HostedServiceHandler>` throws when the subject is reachable from two hosting contexts, so a lookup made after the mutation hands the caller an exception with the subject already changed. An attach is then left holding a stored attachment that the next context attach starts and the caller has no handle to; a detach is left with the instance running, the attachment already gone, and therefore no later graph event that enumerates it and no stop appended anywhere. Pinned by the four `WithHostedServicesTests` tests whose names contain `CannotResolveOneHandler`, one per entry point.

### An attach and a context entry are the same two facts in opposite orders

An attach reads the handler and then stores the attachment. A subject entering a graph publishes the context and then reads the subject's attachments. Those are a load and a store of the same two facts in opposite orders, so neither single order is safe, and the two fail differently:

- **Lookup, then add.** A context published between the two is missed by both sides: the attach decided there was no handler, and the graph event read an empty attachment list. The subject sits inside a hosting graph holding a factory nothing will ever invoke, with no fault and nothing logged.
- **Add, then lookup.** The lookup can throw after the subject has already been mutated, which is the half changed call above.

The attach therefore does both, and the second read counts rather than throwing. `TryResolveHandlerAfterPublish` runs only when the first lookup found none, and it counts the reachable handlers instead of going through `TryGetService`, so two of them are declined rather than thrown on: a throw there would land after the add and be exactly the state the first lookup exists to rule out. Two reachable handlers is the one shape it declines to act on, and it is the shape every other path on that subject throws on anyway.

Each attach overload adds its attachment from its own code, so each needs its own test. Pinned by `WithHostedServicesTests.WhenASubjectEntersAHostingGraphBetweenAnAttachsLookupAndItsAdd_ThenTheAttachStartsTheService` and `WithHostedServicesTests.WhenASubjectEntersAHostingGraphBetweenAnAwaitedAttachsLookupAndItsAdd_ThenTheAttachStartsTheService`, which run a whole context entry inside the window by gating the subject's first `Data` read through `Models/DataGatedSubject`, that read being the one the add itself makes. Nothing on the attach path ahead of the add may read `subject.Data`, or the gate fires early and the window moves somewhere else.

## The Gate

`HostedServiceGate` has four states and moves forward only: `NotStarted`, `Running`, `Draining`, `Drained`. `EnsureStarted` advances `NotStarted` to `Running` and does nothing in any other state. Written as a plain assignment it would let a detach arriving during shutdown flip `Draining` back to `Running` and reopen the race the fourth state exists to close.

### Where the gate is read

The gate state is read at append time as well as inside transition bodies, and the two reads answer different questions. The property that matters is narrower than "never at append time":

**Stops are never refused at append time, and a start's gating decision is re-read in the body.**

`AppendStop` reads no gate state at all. A stop short circuited at append time would have no body, therefore no `finally`, therefore would never set its `subjectStopped` completion, so the paired attachment stop would park on that signal forever and wedge that chain against every later append.

Starts are refused at append time, by `AttachSubject` and by `TryTakeOwnershipAndStart`, and those refusals are about bookkeeping rather than about work: a draining or drained handler must not install itself as owner of a target it can never start, nor record a subject as live, because a target left owned by a dead handler makes every future handler lose the compare and exchange. The decision about whether the start's **work** runs is taken again in the body, because a start already queued when shutdown begins only becomes a no-op if it re-reads the state when it runs.

A gated out transition therefore still runs its signalling and its bookkeeping, and skips only the user visible work.

| Gate state when the body runs | start | stop |
|---|---|---|
| `NotStarted` | parks until the gate leaves `NotStarted`, then re-reads | parks until the gate leaves `NotStarted` |
| `Running` | runs | runs |
| `Draining` | skips the work, releases its startup holds | runs, signals |
| `Drained` | skips the work, releases its startup holds | runs, signals |

### Why a stop runs at every state, `Drained` included

That row is not a rounding error. Shutdown waits for every transition it counted, but a stop appended after its second wait returned, by a graph move racing the drain, was counted by nobody still watching and reaches `Drained` still holding a running instance. A stop that no-oped there would leave that instance never stopped and never disposed, and nothing is lost by letting it run, because the null `Current` check, not the gate state, is what makes a stop idempotent. `HostedServiceHandlerRaceTests.WhenAnExplicitDetachRacesTheHostDrain_ThenTheInstanceIsDisposedOnce` reaches `Draining` with a second stop behind the drain's own and pins that the two together dispose once; nothing reaches `Drained`, which needs an append the barrier has already stopped watching for.

`BeginDraining` sets the opened signal even though it never opens the gate for work, which releases anything parked on a gate that was never opened. Without it, a host that aborts startup or is disposed without starting leaves transitions and their awaiters hanging forever. Pinned by `HostedServiceGateTests.WhenDrainingStartsFromNotStarted_ThenParkedWaitersAreReleased`, which fails when the signal is removed from `BeginDraining`.

### Who may open the gate

This is a real decision, because "nothing runs before host start" and "a caller that started before the handler must not hang" pull in opposite directions. `SubjectActivation<T>` and the awaitable attach and detach overloads call `EnsureStarted`, since awaiting is an explicit request for the service to be running. The synchronous overloads and every lifecycle driven append only wait for the gate, which preserves the invariant for `new Car(context)` at configuration time. Pinned by `HostedServiceHandlerTests.WhenAnAwaitedAttachRunsOnAHostThatWasNeverStarted_ThenItStillReturns` and `HostedServiceHandlerTests.WhenAnAwaitedDetachRunsOnAHostThatWasNeverStarted_ThenItStillReturns`.

## Startup Completion Holds

The user facing contract is in [Deferred Starts and Startup Completion](../hosting.md#deferred-starts-and-startup-completion), and the constraint an implementer meets is on [`IStartupCompletionDeferrer`](../../src/Namotion.Interceptor.Tracking/IStartupCompletionDeferrer.cs). What matters here is where the hold is taken and released.

The hold is taken in `TryTakeOwnershipAndStart`, synchronously and **before** the append, so there is no window between the attach and the hold in which completion can fire. Pinned by `HostedServiceHandlerRaceTests.WhenASubjectEntersTheGraph_ThenItsStartupHoldIsTakenBeforeTheGraphWriteReturns`, which asserts the hold is outstanding by the time `parent.Child = child` has returned.

It is released in the start body's `finally`, which is what covers every way out of the body. One test per path the `finally` protects:

- gated out by a drain: `HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheDrain_ThenItsStartupHoldIsReleased`;
- the subject is no longer live: `HostedServiceHandlerRaceTests.WhenAQueuedStartFindsItsSubjectDetached_ThenItsStartupHoldIsReleased`;
- skipped by the one instance guard: `HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheOneInstanceGuard_ThenItsStartupHoldIsReleased`.

When the append is refused there is no body, so that path releases the holds itself. A leaked hold blocks every synchronization wait on that tree forever, which is a hang rather than a wrong answer and worse than never having taken the hold.

A deferrer that throws is logged and ignored on both paths, for the reason at the `catch` in `TakeStartupHolds` when taking and at the one in `ReleaseStartupHolds` when releasing, where the reason is that one deferrer must not strand the others. A deferrer that blocks is a different matter, and it is a constraint on the implementation rather than an exposure every consumer carries: see [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own).

## Faults and Failed Starts

`Fault` holds the exception from the last failed transition on that target. Only a start body ever clears it, and it clears it **after its guards, not on entry**: a start that is gated out or skipped must not drop a fault a caller has not read yet (pinned by `HostedServiceHandlerRaceTests.WhenAQueuedStartIsSkippedByTheDrain_ThenAnEarlierFaultSurvives`). Clearing it at all matters because a graph driven start that faulted is deliberately kept so the next context attach can retry; without the clear, the next successful attach would throw a stale exception through `AttachHostedServiceAsync` or `WaitForStartAsync`. Pinned by `HostedServiceHandlerTests.WhenATransitionFaultedEarlier_ThenTheNextSuccessfulOneClearsTheFault`.

A stop records a fault when it fails and never clears one, so a start that faulted followed by a clean stop ends with `Fault` set and nothing running. That is the reading the two members are meant to give together: `Current` null with `Fault` set is "this should be running and is not".

A start that faults after creating the instance disposes it when the handler created it, and leaves `Current` null. Leaving a half started connector undisposed is the ownership gap by another route: a connector can hold a semaphore, a session manager and a lifecycle subscription that only its own dispose releases.

That cleanup dispose runs inside a `catch` that rethrows the start's own exception afterwards, so the `catch` inside `DisposeInstanceAsync` is load bearing there and not only a log: an escape from it skips the rethrow, and the caller waiting on `AttachHostedServiceAsync` is handed the cleanup failure instead of the reason the start failed. Pinned by `HostedServiceHandlerTests.WhenAFailedStartsCleanupDisposeAlsoThrows_ThenTheCallerStillGetsTheStartException`.

A start whose factory returns the instance it returned last time is refused before `StartAsync` and before `SetCurrent`, with an `InvalidOperationException` recorded on `Fault` and `Current` left null. The rule and what it does not catch are stated for consumers in [The factory must construct](../hosting.md#the-factory-must-construct).

Failing closed was measured both ways. The guard gates on `IsHandlerOwnedInstance`, which is wider than the harm it names: `DisposeInstanceAsync` acts on `IDisposable` and `IAsyncDisposable` only, so a hosted service implementing neither is stopped and never disposed, and handing it back would in fact start it cleanly. With the guard such a service starts once and the fault is set; with the guard disabled it starts twice and works. The rule stated is therefore "a factory attachment constructs on every call", not "the handler would otherwise hand back a disposed instance"; why the wider rule is the better one is argued in the consumer facing section linked above.

The check is one reference comparison against `_lastFactoryInstance`, and it sits ahead of the dispose-on-failed-start path, so nothing is disposed twice. Pinned by `HostedServiceHandlerTests.WhenTheFactoryReturnsTheInstanceItAlreadyProduced_ThenTheStartFaultsInsteadOfUsingItAfterDispose`.

A cancelled stop is caught and **not** recorded as a fault, and the dispose after it still runs; the comment at that `catch` records why, and `HostedServiceHandlerTests.WhenAStopIsCancelled_ThenTheInstanceIsStillDisposed` pins both halves.

Both places that rethrow a recorded fault to a caller, `HostedServiceHandler.WaitForStartAsync` and `InterceptorHostingExtensions.AttachHostedServiceAsync`, use `ExceptionDispatchInfo.Capture(fault).Throw()` rather than `throw fault`, for the reason recorded at the second of them. Pinned by `HostedServiceHandlerTests.WhenAFailedStartIsRethrownToTheAttachingCaller_ThenTheOriginalStackSurvives` and `HostedServiceHandlerTests.WhenAStartFaultedForAWaitingCaller_ThenTheFaultIsRethrown`.

## Shutdown

### The barrier

`StopAsync` is built from the pieces above rather than around them:

1. `BeginDraining`, which stops new targets being taken and releases parked waiters.
2. Clear `_liveSubjects`, for the reason recorded there, and because it is also what stops `WaitForStartAsync` appending an empty transition behind the drain's own stop, which is what `HostedServiceHandlerTests.WhenAHandlerIsAskedToWaitWhileItsOwnDrainIsStopping_ThenItAnswersWithoutQueueingBehindTheStop` pins: deleting the clear is the one change that fails it.
3. Snapshot `_owned`.
4. Append stops for that snapshot in the same per subject shape a context detach uses: a stop carrying a `subjectStopped` signal for every subject target, then a stop for every attachment target that awaits its own subject's signal when that subject was in the snapshot. Each append is refused unless this handler still owns the target, decided inside the chain lock.
5. Wait for `_inFlight` to reach zero, bounded by the host's stopping token.
6. Release ownership of every target in the snapshot.
7. Wait for `_inFlight` to reach zero again, then `CompleteDraining`.

An expired token in step 5 does not throw and does not skip step 6: rethrowing would abandon it, so every target this handler owns would stay owned by a dead handler and a second host over the same subjects would start nothing. The count it gave up at is logged. Why nothing below step 5 observes the token is at the wait itself. Pinned by `HostedServiceHandlerTests.WhenAServiceStopNeverReturns_ThenShutdownDoesNotOutlastTheTimeout` and `HostedServiceHandlerTests.WhenTheShutdownTokenIsAlreadyCancelled_ThenTheInstanceIsStillDisposed`.

Step 7 is skipped when step 5 gave up, because the deadline has already passed and a second wait would return immediately with the same answer.

**`_owned` is never cleared at the end.** After step 6 the only entries left are installs whose own gate re-read releases them, and clearing is what would make the set and the `_owner` field disagree for anything still in flight.

### What the drain waits for

`_inFlight` is incremented in `HostedServiceTarget.AppendCore`, attributed to the appending handler, and decremented in `RunAsync`'s `finally`. Three details are load bearing:

- **The increment is before the `ContinueWith`, not after.** On an already completed tail the continuation runs and decrements before the next statement on the appending thread executes, which takes the count negative, and a later increment then brings it back to zero while a transition is still running. Nothing between the increment and the append may throw, because a leaked increment never comes back. Pinned by `HostedServiceTargetTests.WhenTransitionsAreAppendedOntoACompletedTail_ThenTheInFlightCountIsNeverNegative`, which samples the live count from a second thread rather than through a seam, because the two statements are adjacent and no seam fits between them. It fails 5 of 5 runs when the increment moves.
- **The decrement is in the `finally`, not at the end of the `try`**, so the transition gate above the body is inside the same count.
- **An unattributed append is allowed and is not counted.** Only tests make one; every production append carries the handler that made it.

The count is per handler and not per target, so the drain waits for every transition this handler appended, including those on chains its own snapshot never covered. That is deliberate and it is the cost the change accepts: see [Cost](#cost).

### Why the count is re-read rather than signalled

A completion source was tried and rejected. It has to cope with the count already being zero when the drain starts, with a transient zero before the drain's own stops land, and with a store-load reordering on both sides that `Volatile.Write` does not close. Two of those were demonstrated, one of them hanging shutdown outright. Re-reading has none of the cases because it re-reads. The cost is one poll interval per round, once per process, on a path that already spends 50 ms inside every stop it is waiting for.

**The second wait is why the barrier holds.** The count is read, not held, so an append landing after the count first reached zero would otherwise be outside the barrier despite having gone through the same increment. Past step 6 a context detach appends nothing for this handler, because it reads `Owner` and finds a stranger, so one more round is the last that path can need. `DetachHostedService`, which appends without an ownership check, is not covered by it, and that residual is recorded below.

The two waits are pinned separately. Deleting the second fails `WhenAStopIsAppendedAfterTheCountFirstReachedZero_ThenTheDrainStillWaitsForIt` and nothing else; it holds the drain at `DrainReleaseGate`, lets a context detach append there, and arms the target's transition seam from inside the seam so the count provably reached zero first. Deleting the first fails three. `WhenAStopIsInFlightWhenTheHostDrains_ThenTheDrainWaitsForIt` and `WhenAHandlerIsAskedToWaitWhileItsOwnDrainIsStopping_ThenItAnswersWithoutQueueingBehindTheStop` fail through the ownership release moving ahead of the stops, and `WhenAStopIsAppendedAfterTheCountFirstReachedZero_ThenTheDrainStillWaitsForIt` fails on the premise it asserts, because its seam sits between the two waits and it reads the count as zero there. Deleting both additionally fails `WhenNothingIsInFlightAsTheDrainBegins_ThenItStillWaitsForTheStopsItAppends`, which is what pins that a drain starting from a settled graph waits at all.

### Why the ownership decision is inside the chain lock

The drain snapshots `_owned` holding nothing and appends afterwards, so ownership can move in between. `HostedServiceTarget.AppendIfOwnedAsync` therefore reads `Owner` and appends under one acquisition of the chain lock. Without it, a subject that leaves host 1's graph and joins host 2's before host 1's drain reaches its append has the instance host 2 started stopped and disposed by host 1, and host 2 is left with a live graph, nothing running and no error anywhere. Pinned by `WhenASubjectJoinsASecondHostWhileTheFirstIsDraining_ThenTheSecondHostsInstanceSurvives`, which holds the drain at `DrainAppendGate` and moves the whole subject across in that window.

Because the append can be refused, **`subjectStopped` is recorded only for an accepted one**. A refused append has no body and therefore no `finally`, so an attachment stop handed that signal would park on it, stay counted, and burn the whole shutdown deadline.

The same lock is what orders the drain against a take in flight. A drain that snapshots while a take holds the chain lock queues behind that take for its own append, and by then it reads either the ownership the take installed, so its stop lands behind the start, or the release the take's own liveness undo made, so it appends nothing for a take that was undone. `WhenAStartIsQueuedAndUnrunAsTheDrainRuns_ThenTheDrainStillWaitsForIt` drives that interleaving through `ChainLockGate`, but what it asserts is carried by the count rather than by the lock: the start was counted when it was appended, so the drain waits for it whether or not its target was in the snapshot. What the ordering itself protects is the ownership, and that is read by `WhenARepeatTakeFindsLivenessCleared_ThenItLeavesTheEarlierAttachAlone`, which holds a repeat take on `LivenessReadGate` while the drain clears liveness and snapshots underneath it: undoing an ownership that take did not install pulls the target out from under the drain's own append, which then reads a stranger and refuses, and the running instance survives shutdown. That test is also the one the `ownershipTaken` guard on the liveness undo needed and did not have.

**The gate re-read's undo appends a stop rather than only retiring the record.** The re-read fires after the start has been appended, and it cannot assume that start has not run: the body reads the gate at its own top, so it can have read `Running` a moment before `BeginDraining` and be past every guard it has, committed to creating an instance. Retiring the record alone hides that instance from a snapshot taken afterwards and nothing ever stops or disposes it, with ownership released so no later detach can reach it either. Appending a stop orders the cleanup behind the committed start on the same chain, and the count carries it. Pinned by `WhenTheGateReReadUndoesATakeWhoseStartIsAlreadyCommitted_ThenThatStartIsStillStopped`, which parks the start inside its factory, past every guard, and reports a started, never stopped, never disposed instance when the append is removed.

Several tests named above assert that `StopAsync` did **not** return within a bounded window, which is the suite's only kind of timed observation: "did not happen" has no event to wait on. They cannot false fail, because on an intact build the drain is parked on something a test thread releases later, so no length of wait lets it return. On a reverted build each returns in tens of milliseconds.

### Cost

A subject that hosts nothing never gets a target and never reaches `AppendCore`, so it pays nothing at all. On the detach path the change is cheaper: one increment and one decrement replace a per stop continuation and a dictionary entry. The unit suite's hosting project runs in the same 10 seconds it did before, and both shutdown deadline tests are unchanged.

Two shapes get slower, and the second is a correctness gain being paid for:

- **A wedged chain the drain's snapshot never covered now holds the count non zero**, so shutdown takes the full timeout where it used to return as soon as its own snapshot completed. What it used to do there was return while an unrelated chain was about to touch a service provider the host is disposing.
- **A hosted service whose own stop mutates the graph** appends stops the snapshot never saw. Those used to be missed, and are now waited for.

### The two gate reads in `TryTakeOwnershipAndStart`

`TryTakeOwnershipAndStart` reads the gate twice, once on entry and once after the take has installed the owner and its record, for the reasons recorded at both reads. `AttachSubject` re-reads the gate after writing `_liveSubjects` for the same reason.

`HostedServiceHandlerRaceTests.WhenAnAttachmentIsAddedDuringTheDrain_ThenTheDrainingHandlerTakesNoOwnership` pins that a draining handler ends up owning nothing, and it fails only when **both** reads are deleted: its attach arrives after `BeginDraining`, so the read on entry already refuses it. Each read also has a test the other does not satisfy, and deleting either one alone fails it:

- The re-read: `HostedServiceHandlerRaceTests.WhenAnAttachLandsItsTakeAfterTheDrainBegan_ThenTheTakeIsUndone`, and with it `NestedAttachTests.WhenTheDrainBeginsWhileANestedAttachHoldsTheOuterOne_ThenNeitherSubjectStaysLiveOnTheDrainingHandler`, which reads the owner of both its subjects as well as their liveness and so is not separated from the `AttachSubject` re-read below. Its attach passes the read on entry while the gate is still `Running` and lands both writes after `BeginDraining`. The seam between the two is `TakeStartupHolds`, which is third party code on that path: the deferrer starts the drain and waits for it to reach `DrainGate` before the attach goes on. The ownership is read while the drain is still held, because letting it go releases every target the drain's snapshot covered and hides the difference.
- The read on entry: `HostedServiceHandlerRaceTests.WhenADrainingHandlerSeesAnAttach_ThenItInstallsNoOwnerForALiveHandlerToLoseTo`. It holds the take open on `OwnershipTakenGate` and has a second handler try the compare and exchange from there. The re-read undoes a take, but only after installing it, and a live handler that reaches the target inside that window loses the exchange for good, because nothing retries it. The seam is reached only when the read on entry is gone, so on an intact build the attach simply returns and the second handler wins.

### The two gate reads in `AttachSubject`

What these two protect is the liveness entry rather than the owner: whichever of them is missing, the take itself is still refused by `TryTakeOwnershipAndStart`'s own reads, and the damage that survives is a subject rooted on a dead handler.

- The re-read: `NestedAttachTests.WhenTheDrainBeginsWhileANestedAttachHoldsTheOuterOne_ThenNeitherSubjectStaysLiveOnTheDrainingHandler`. A deferrer creates a child from inside the container's attach, and the hold that nested attach takes starts the drain, so both calls wrote their liveness entries while the gate was still `Running` and both have to notice on the way out. The set is read while the drain is held at `DrainGate`, which is ahead of the liveness clear, because letting the drain go clears both entries for an unrelated reason and hides the difference. Deleting the re-read alone fails it.
- The pair: `NestedAttachTests.WhenAnAttachHandlerCreatesAChildAfterTheDrainClearedLiveness_ThenNeitherSubjectIsLeftLive`, which attaches a container, and the child its attach handler creates, into a drain parked inside a stop body, past the liveness clear. Parking there rather than on `DrainGate` is what makes the damage permanent: an entry written while the drain is held at `DrainGate` is swept up by the clear that follows it. Deleting either read alone leaves it green, because the write and the removal cancel out, so it fails only when both are gone.

The read on entry has no test that fails for it alone, and that is a coverage limit rather than redundancy. The window it covers is a drain beginning between it and the liveness write beside it, and those two statements are adjacent, so no seam can drive anything into the gap.

### Undoing a take the detach could not release

The detach clears liveness, then reads `Owner`, then releases, and it does the last two outside the chain lock while a take does its own read and compare and exchange inside it. So a take whose liveness read passed before the clear, and whose exchange landed after the release, is one the detach never saw and never releases. No instance is created while liveness stays cleared, because the start body re-reads it, but the target stays owned and stays in `_owned`, which roots the detached subject on this handler until shutdown and makes the next handler over that subject lose the compare and exchange for good.

`TryTakeOwnershipAndAppendAsync` therefore reads liveness a second time, after its own take and still inside the chain lock, and releases an ownership it installed rather than appending anything. The clear happens before the release, so a take that missed the release cannot also have missed the clear.

**Inside the lock, not after it, and that placement is the whole point.** `ReleaseOwnership` matches on the handler rather than on the take, so an undo running outside the lock can destroy an ownership a concurrent re-attach installed in between: the subject is then in the graph and live with nothing running and no error anywhere, or a started instance sits in no drain's snapshot and shutdown never stops it. Inside the lock no install can interleave, because every install takes it. The gate equivalent of this undo does sit outside the lock, and is safe there only because a draining handler installs nothing at all.

Pinned by `HostedServiceHandlerRaceTests.WhenADetachReleasesOwnershipBeforeAnAttachTakesIt_ThenTheAttachUndoesItsOwnTake`, which holds the take on `LivenessReadGate` between its first liveness read and its compare and exchange, because those two statements are adjacent in production.

### The two gate reads in `MarkLiveIfAttached`

The same pair, on the path a subject takes when it gains its first target while already in the graph, and they protect the same thing: an entry written after the drain cleared the set is one nothing removes, so the subject stays live on a dead handler for the rest of that handler's life.

- The re-read: `HostedServiceHandlerTests.WhenAnAttachmentIsAddedAfterTheDrainClearedLiveness_ThenTheSubjectIsNotLeftLive`. The drain is parked inside a stop body, past the liveness clear, and the write is held on `LivenessWriteGate` so it lands after that clear. Deleting the re-read alone fails it. Parking on `DrainGate` instead cannot reach this, for the reason above: the clear still follows and sweeps the entry up.
- The read on entry has no test that fails for it alone, for the same reason as in `AttachSubject`: it is a narrowing, and the re-read beside it absorbs its absence, so no observable outcome changes.

## Activation and Waiting for a Start

`SubjectActivation<T>` exists because a singleton nobody resolves is never constructed, never attached to its context and never started, and `IHostedService` is the only hook the generic host offers for forcing that construction. Resolving the subject attaches it, which makes the handler append the start. When the resolved context has no `HostedServiceHandler` the activation starts the subject itself and stops exactly that instance, for the reason at the field it records it in. Pinned by `AddSubjectTests.WhenThereIsNoHostingHandler_ThenTheActivationStartsTheSubjectItself`.

`WaitForStartAsync` appends an empty transition to the same chain and awaits it. Appending never runs a body, so that transition completes only once the start ahead of it has run. It then rethrows the recorded fault, which preserves the `AddHostedService` guarantee that a failing subject aborts host startup and that `ApplicationStarted` implies the subject is running.

It reads the target and never creates one, and never takes ownership; the comment there records why a claim taken from a wait would never be released. Its false result is not licence for the caller to start the subject itself, and `SubjectActivation<T>` records that decision at the call it makes. Pinned by `HostedServiceHandlerTests.WhenADrainedHandlerIsAskedToWaitForAStart_ThenItClaimsNothingAndReportsNothingStarted` and `HostedServiceHandlerTests.WhenANonOwningHandlerIsAskedToWaitForAStart_ThenItReportsNothingStarted`.

## The 50 ms Delay

The stop body delays 50 ms before touching the instance. The start body used to as well, and no longer does: it waits for a startup scope instead, which is the same hazard answered rather than guessed at.

The hazard is caller side. The generated context constructor attaches the subject last, so `new Car(context) { Name = "x" }`, deserialization, and `AddSubject`'s `configure` on that constructor path all assign after the attach has fired and after the start has been appended. A scope is the constructing flow saying when it has finished, so a start captured in one waits for the answer. `AddSubject` and HomeBlaze's `ConfigurableSubjectSerializer` and `RootManager` all open one; a consumer writing `new Car(context) { Name = "x" }` by hand gets no protection unless they open one too, which [`docs/hosting.md`](../hosting.md#configuration-before-startup) states.

What remains on the stop side is a mitigation with no mechanism behind it, kept because removing it is a behaviour change of its own rather than part of the scope's arrival. It passes `CancellationToken.None`, so shutdown waits it out per target.

## Startup Scopes

`HostedServiceStartupScope` is ambient per context, held in an `AsyncLocal` on the handler and handed out by `IInterceptorSubjectContext.DeferHostedServiceStartup()`, which returns null when the context has no handler. The contract:

- **Capture is per execution flow, at append time.** `TryTakeOwnershipAndStart` reads the ambient scope in the appending flow, beside the startup holds and for the same reason: the body runs later, and on the fire and forget paths in a flow that has already moved on.
- **A captured start waits for its own scope and every scope enclosing it**, which `HostedServiceStartupScope.WaitAsync` walks. Disposal releases; there is nothing to call on success and no way to fail through the scope.
- **The wait sits after every guard in the start body and before the fault is cleared**, and each guard is re-read after it. A scope holds a start for as long as its flow stays open, so the subject can leave the graph, ownership can move, and a competing start can install an instance in the meantime. A start that finds any of that declines, which leaves the outcome the same as the old handler's cancel on detach, reached later: nothing is created.
- **The drain releases the wait too**, through `HostedServiceGate.WaitForDrainingAsync`. A start parked on a scope is already counted in flight, so a scope nobody disposes would otherwise hold the drain's barrier for the whole shutdown deadline. Pinned by `WhenTheDrainBeginsWhileAScopeIsOpen_ThenItReleasesTheParkedStartInsteadOfWaitingForIt`, which fails by timing out when the draining term is removed.
- **Disposal order is the caller's discipline, not enforced.** Reverse creation order in the creating flow is what nested `using` blocks do. Repeated disposal, disposal from another flow and out of order disposal none of them throw, and none of them strand a start: what is undefined afterwards is only which later attaches the scope still covers. Pinned by `WhenAScopeIsDisposedIrregularly_ThenCapturedAndLaterServicesCanStart`.
- **Nesting reaches into dependency injection, deliberately.** `AddSubject<T>`'s factory opens its own scope and, like any scope, it takes the ambient one as its parent, so a subject built while another scope is open waits for both. HomeBlaze depends on that: `RootManager` holds one scope across the whole root load and `ConfigurableSubjectSerializer` opens a nested one per subject it creates, and the nesting is what stops a device starting before the rest of the root exists. The cost is that a scope held across `host.StartAsync()` reaches every such factory, which is [residual hazard 5](#5-awaiting-a-captured-start-or-its-detach-inside-its-own-startup-scope) with the await hidden inside the host.
- **The chain is shared between handlers and the scope is not.** A target lives on the subject, so two handlers over one subject append to one chain, while each handler has its own ambient scope. A start parked on one host's scope therefore delays the other host's start of a subject the first host no longer owns, silently, since the second start carries no scope of its own and records no fault. Reachable only by moving a subject between two hosting enabled hosts in one process while one of them holds a scope open. Documented rather than fixed: separating the chains is a larger change than the hazard is worth.
- **A parked start holds its target's chain.** Anything ordered behind it waits for the scope, including the empty transition `WaitForStartAsync` appends, so a subject whose start was captured by a scope another flow still holds does not finish activating until that scope closes. Bounded rather than deadlocked, unless the waiting flow is the one holding the scope, which is [residual hazard 5](#5-awaiting-a-captured-start-or-its-detach-inside-its-own-startup-scope). Under one shared queue a deferred start sat in a pending list and blocked nothing.
- **Every attributed transition body drops the ambient scope first**, through `HostedServiceHandler.ClearAmbientStartupScope`. A body inherits the execution context of the flow that appended it, and a stop body does not wait for that flow's scope, so without this an attach made from a stop path is captured by a scope belonging to a caller it has nothing to do with. It parks, and where the stop path awaits it, the stop holds its chain for as long as that caller keeps the scope open. Pinned by `WhenAStopPathAttachesAService_ThenAnUnrelatedOpenScopeDoesNotCaptureIt` and `WhenAStopPathAwaitsItsOwnAttach_ThenAnUnrelatedOpenScopeDoesNotWedgeTheChain`. A start body would be safe either way, because it either cannot run until its own scope is released, or was released by the drain and then declines before creating anything.

### What arrived with the scope and what it replaced

The scope and its `DeferHostedServiceStartup` entry point predate this design and are unchanged by it. What this design replaced is where the waiting happens: a shared consumer loop scanned queued actions for readiness and tracked a cancellation source per deferred start so a detach could cancel one. Per target chains need none of that. The start body is already serialized against its own target's stops, so parking in it is enough, and the detach that used to cancel now lands as a stop behind the parked start on the same chain.

One consequence is worth stating plainly: a detach no longer completes a deferred start's cancellation immediately. `DetachHostedServiceAsync` awaited while the scope is still open therefore waits for the scope, where it used to return as soon as the start was cancelled. Not awaiting inside an open scope is already the rule for attaches.

## Residual Hazards

Per target chains contain the self deadlock that one shared queue spread across every service. They do not remove cycles, and none of the five shapes below is detected.

### 1. An attachment whose own `StopAsync` detaches itself

The detach appends to the chain the running stop occupies.

### 2. Two subjects whose services detach each other's attachments from their own stop paths

The same cycle across two chains rather than one.

### 3. A subject that detaches its own attachment while unwinding

The subject's stop transition waits on the unwind, the unwind waits on the attachment chain, and the attachment chain's head waits on `subjectStopped`, which only the blocked subject transition can set:

1. The subject leaves the graph. `DetachSubject` appends the subject's stop, carrying `subjectStopped`, and appends the attachment's stop, which first awaits `subjectStopped`.
2. The subject's stop runs. `BackgroundService.StopAsync` awaits the execute task, and `ExecuteAsync` unwinds into a helper that awaits `DetachHostedServiceAsync` for the attachment.
3. That call appends its own stop to the attachment's chain, behind the stop from step 1, and awaits it.
4. The attachment's chain head is still awaiting `subjectStopped`, which is set in the `finally` of the subject's stop, which cannot finish because it is still inside step 2.

Shape 3 is the one that occurs in practice, because it is what a `BackgroundService` reaches when `ExecuteAsync` unwinds into a stop helper that detaches. It is the shape both HomeBlaze OPC UA wrappers would have if their unwind detached, which is why each one's unwind only resets its reported state. The rule is stated in [the user documentation](../hosting.md#do-not-detach-from-your-own-stop-path) rather than guarded, and `HostedServiceHandlerTests.WhenASubjectOwningAnAttachmentIsStoppedByTheHost_ThenShutdownCompletesWellInsideTheTimeout` is the regression guard. A wedged chain is unbounded in damage but bounded in blast radius: shutdown gives up on it at `ShutdownTimeout` and every other chain drains normally.

### 4. A deferrer that takes a lock of its own

`TakeStartupHolds` calls `IStartupCompletionDeferrer.DeferCompletion()` synchronously from `HandleLifecycleChange`, and the refused-append path disposes those holds from the same place. Both run under `_attachedSubjects`. A deferrer that takes a lock of its own therefore joins that lock's order, and the resulting cycle has three parties:

1. Thread A takes the deferrer's own lock `L`, then awaits a hosted service transition `T`, for example through `AttachHostedServiceAsync`.
2. `T`'s body writes a subject typed property, so it needs `_attachedSubjects`.
3. Thread B holds `_attachedSubjects` for an unrelated graph write, reaches `HandleLifecycleChange`, and calls `DeferCompletion()` on that same deferrer. It blocks on `L`.

`A` waits for `T`, `T` waits for `_attachedSubjects`, `B` holds it and waits for `L`, and `A` holds `L`. Nothing resolves it, and unlike the three chain wedges above the blast radius is the whole process rather than one chain: `B` is holding `_attachedSubjects`, so every structural property write anywhere in the graph queues behind it.

Step 2 is no longer required for the cycle. `AttachHostedService` and `AttachHostedServiceAsync` take `_attachedSubjects` themselves, through `MarkLiveIfAttached`, before either of them appends anything. A caller that holds `L` and attaches a hosted service therefore blocks on `_attachedSubjects` directly, so `A` needs no transition body at all and the cycle has two parties rather than three. The constraint on the implementer is unchanged and the exposure is wider: a deferrer that takes a lock must not have that lock held across any hosted service attach.

**The call site is accepted rather than fixed, and it is not by itself the deadlock.** The hold must exist before the append completes, or the window it closes reopens: a subsystem that treats "the graph has finished starting" as a completion point would pass that point with a queued start still on its way in. On the lifecycle driven path the event that appends arrives already inside `_attachedSubjects`, so there is no earlier point at which to take it. Every alternative that keeps the guarantee either calls `DeferCompletion` from the same place, or needs a new cross-package protocol between Hosting and Connectors, which is a design change rather than a defect fix. Deferring only the release off the lock was considered and rejected: it costs an allocation and a thread hop on a rare path and leaves the take, which is the main exposure, exactly where it was.

What the call site does is put a constraint on the implementer, and an implementation that follows it cannot supply the step the cycle needs. The constraint is stated on [`IStartupCompletionDeferrer`](../../src/Namotion.Interceptor.Tracking/IStartupCompletionDeferrer.cs), where an implementer meets it. Step 3 of the cycle is the only step a deferrer supplies, so a deferrer that never blocks there leaves nothing for `A` and `T` to close a cycle against. The exposure is therefore per implementation, not per consumer: an application whose deferrers all follow the rule is not exposed to this hazard at all.

`SourceMonitor`, the only implementation in this repository, follows it: its take is an `Interlocked.Increment` that acquires nothing, and its release takes the monitor's `_lock` in an order that type already fixes for itself, which its `DeferCompletion` remarks set out. What holds that order is that nothing under `_lock` ever waits on anything that needs `_attachedSubjects`: the graph walk in `IsBranchSynchronized` reads parent sets, and completing a wait uses `RunContinuationsAsynchronously`, so no continuation runs on the releasing thread.

### 5. Awaiting a captured start, or its detach, inside its own startup scope

The flow holds the scope open, the start is parked on it, and the flow waits for something ordered behind that start: the start itself, or a detach whose stop is appended to the same chain. Only that flow can dispose the scope, and it cannot get there.

`host.StartAsync()` is the instance that does not look like one. A subject registered with `AddSubject<T>` has its start captured by the enclosing scope through the factory's own nested scope, `SubjectActivation<T>` waits for that start, and the host waits for the activation, so the scope is disposed only after the call it is blocking returns. Nothing bounds it unless the application sets `HostOptions.StartupTimeout`.

Stated for consumers in [Configuration Before Startup](../hosting.md#configuration-before-startup), because every one of these waits is public API and none of them is guarded.

### Disposal from a handler transition

The handler disposing what it created puts a constraint on connectors that nothing enforces and no test covers: it disposes from a transition that can run while a detach cascade still holds `_attachedSubjects`, so a connector's dispose path and that lock interleave.

The concrete collision is `SourceOwnershipManager`. Both its `Dispose` and its `SubjectDetaching` handler take its own lock and invoke `onReleasing` from inside it, and the `SubjectDetaching` handler runs from inside `_attachedSubjects`. That fixes one lock order, `_attachedSubjects` then the manager's lock, and a dispose that runs from a handler transition takes the manager's lock without holding `_attachedSubjects`. The order reverses the moment anything on that dispose path enters `_attachedSubjects`, which is what an `onReleasing` callback that writes a subject typed property or attaches or detaches a subject does.

The constraint that follows is stated in [the user documentation](../hosting.md#keep-the-dispose-path-out-of-the-lifecycle-lock). Note that `LifecycleInterceptor.WriteProperty` takes the lock only when the property type can contain subjects, which is why writing a scalar from a dispose path is harmless and writing a subject typed or collection typed property is not, and why attaching or detaching a subject enters the same lock without being a property write at all.

## What Has No Test Behind It

Most guards in this file are recorded with the test that fails when they are deleted. These are not, and each one is here so that a later reader does not mistake silence for coverage. Each was confirmed by mutation where there is a change to make: the suite stays green.

- **`MarkDetached`'s lock, and its position before the `AppendStop` in `DetachHostedService`.** Both the lock and the ordering are argued in [Refusing a start for an attachment a detach already removed](#refusing-a-start-for-an-attachment-a-detach-already-removed), and `WhenADetachRacesTheAppendInsideTheChainLock_ThenTheStartIsOrderedAheadOfTheStop` covers the interleaving where the attach holds the chain lock. It does not cover the other one: a mark that lands after its own stop is appended lets an attach that already snapshotted the attachments read `_detached` as false, take the target and append a start behind that stop, which then creates an instance no later detach can reach. Reaching it needs a seam between the two statements, and nothing parks one there. That no longer means a seam added to a public extension method: `Models/DataGatedSubject` reaches this class of window from the test project, through a hand written subject whose `Data` accessor runs one queued action, which is how [the second handler lookup's window](#an-attach-and-a-context-entry-are-the-same-two-facts-in-opposite-orders) is driven. It does not reach this one as written, because it fires on the read the removal itself makes and this window opens once that removal has returned. What is missing is a gate that fires on the way out, not a seam in production code. The third mark, on the faulted path of `AttachHostedServiceAsync`, is uncovered the same way: deleting it leaves the suite green, because the race it closes is a context attach that snapshotted the attachment before the removal.
- **The `Interlocked.MemoryBarrier` in `TryResolveHandlerAfterPublish`.** Deleting it leaves the suite green, and no test can do better: the two gated tests above drive both sides into one deterministic order on one thread, and the reordering this closes needs two threads and a machine that takes it. The add is a release store made under a data bucket lock, and release does not order that store against this later load, so without the fence the two sides can miss each other however they are ordered in time. Only the reading side spells a fence out, because the publishing side already carries its own: `InterceptorSubjectContext.PublishState` publishes with an `Interlocked.Exchange`. Recorded here so it is not removed as noise.
- **A queued stop for a target an explicit detach retired.** `DetachHostedService` retires the ownership record without releasing ownership, so the drain's `_owned` snapshot does not contain that target and its queued stop is held inside the barrier by nothing but the append's own `EnterTransition`. Deleting that increment leaves the suite green while letting the stop run after `StopAsync` returned, against a provider the host is disposing. The master handler covered this path with a regression test that its shutdown drain needed and this design does not have.
- **The shutdown map's comparer.** `subjectStops` is keyed by reference for the same reason `_liveSubjects` is, and reverting that half alone leaves the suite green: two value equal subjects would share one stop signal, so one subject's attachment waits on the other's stop and can be disposed before its own subject has stopped. It cannot hang, because an accepted stop always signals in its `finally`, so the symptom is an ordering violation with no event to observe it by. `WhenTwoValueEqualSubjectsAreHosted_ThenDetachingOneLeavesTheOtherLive` pins the liveness half only.
- **A stop appended for a target this handler never took**, which only `DetachHostedService` can produce, since it appends without an ownership check. Such a stop landing entirely after the drain's second wait is outside the barrier. Benign today because `TryGetService<HostedServiceHandler>` throws when two hosting contexts are reachable, so the handler that appends is the one draining, and its own snapshot covered the target. Reasoned, not demonstrated.
- **`DetachSubject`'s owner read is not atomic against the drain either**, and that makes the second wait's argument one statement weaker than it reads. The detach reads `Owner`, then appends, and the two are separate statements: a detach preempted between them, having read this handler as the owner before the release loop, can append its stop after the second wait's read returned zero. It is the same shape as the `DetachHostedService` residual above, one statement narrower, and no seam exists between those two statements to drive it. The shape self-protects for a subject with both a subject target and attachments, because the subject stop's own increment covers the attachment appends behind it. Reasoned, not demonstrated.
- **Not clearing `_owned` at the end of the drain.** Leaves the suite green, and is an equivalent mutant as far as anything can currently observe: every entry left after the release loop is either one the loop released or one whose own gate re-read releases it while the gate still reads `Draining`. It stays uncleared because the argument for that, rather than the test for it, is what makes it safe.
- **`WaitForStartAsync` attributing its empty transition.** Leaving it unattributed is green, and provably inert: the body is `Task.CompletedTask`, so nothing it does can outlive the provider. It is attributed for the rule rather than for a case.
- **Two overlapping drains.** The gate's ratchet makes a repeat `StopAsync` legal, and the second would observe the same counter and the same set. Sequential in practice.

Two more mutations survive the suite and are recorded here as **equivalent mutants** rather than as coverage gaps, because in each case the difference is unreachable rather than untested:

- **Recording on a repeat take as well as on the install.** A repeat take can only differ where the record is absent while the owner is this handler. The sole writer of that state is a retirement without a release, which exists on exactly two paths, `DetachHostedService` and `DetachHostedServiceAsync`, and both call `MarkDetached` first, which makes every later take on that target refuse before it reaches the exchange. So no repeat take can observe the difference.
- **Moving the decrement out of `RunAsync`'s `finally` to the end of its `try`.** The enclosing `catch` has no filter and nothing in `RunAsync` can raise a non-`Exception` throwable, so the two positions are reachable under identical conditions. The `finally` stays as defence in depth against a body that stops obeying the rule that bodies never throw.

## Invariants

Once lifecycle events and user driven attach and detach calls have settled:

1. **One instance per target.** A target holds at most one non null `Current`, and only a start body ever sets it.
2. **In the graph implies running, out of the graph implies stopped.** Each chain drains in append order, so every target ends in the state its last event demanded. Chain order alone does not carry this, because it only orders what was appended: an attach racing the subject's context entry can leave a target neither side appended anything for, and nothing revisits it afterwards. What closes that is [the attach resolving the handler a second time](#an-attach-and-a-context-entry-are-the-same-two-facts-in-opposite-orders). Execution across targets is concurrent, so this is quiescent consistency rather than a moment by moment guarantee.
3. **Created implies disposed by the same owner.** The handler disposes exactly the instances it created through a factory, once, and never disposes a subject.
4. **A subject's stop precedes the disposal of its own attachments**, on both the context detach path and the shutdown path, whenever that stop is not cancelled.
5. **No transition body runs under `_attachedSubjects`.** Every lifecycle driven action is an append, and an append never runs a body. This is not the same as "no user code runs under that lock": `DeferCompletion` and the hold disposal on the refused append path do, which is [residual hazard 4](#4-a-deferrer-that-takes-a-lock-of-its-own).
6. **A drained handler roots nothing.** It clears its liveness set, and it releases every target its snapshot held, which retires that target's record. Any record written after the snapshot belongs to an install whose own gate re-read releases it, so `_owned` is empty once things have settled rather than at the moment `StopAsync` returns. The records on the subjects are left for the next handler.
