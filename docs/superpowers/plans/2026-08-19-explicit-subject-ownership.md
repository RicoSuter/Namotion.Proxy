# Explicit Subject Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace fallback-driven lifecycle attachment with strict explicit roots, one ownership domain, one effective ownership route, and prospective structural-write validation.

**Architecture:** Core owns compact per-subject ownership state, domain activation, reservations, route publication, and the structural-write admission boundary. Tracking implements the one supported lifecycle coordinator, graph discovery, parent selection, recursive callbacks, and reconciliation through the public stack-only provider facade. Public fallbacks remain service composition only, while generated, Dynamic, connector, OPC UA, sample, and HomeBlaze callers migrate together.

**Tech Stack:** C# 13 preview, .NET Standard 2.0 Core, .NET 9/10 feature projects, source generators, xUnit, Verify/PublicApiGenerator, immutable context snapshots, monitor-based synchronization, thread-static reusable buffers.

**Spec:** `docs/superpowers/specs/2026-08-18-explicit-subject-ownership-design.md`

## Global Constraints

- Implement from exact stacked base `a88b456ef681dc4505f1edce040b56fb83a6a034`; compare performance with that base and exact master `55df0a84ebc19489cc114297b1e5fb6b4aa0b4b9`.
- Correctness wins over performance, performance wins over style, and allocations normally matter more than CPU when they trade.
- Preserve the checked-in normal single-context callback sequence and callback-time service visibility.
- Expected ownership conflicts fail before the backing property or any library-owned ownership, route, lifecycle, registry, parent, or reference-count state commits.
- Ordinary same-domain contention waits; same-domain reentrancy works; unsupported synchronous nesting always throws `SubjectOwnershipNestingException` before target-gate availability is inspected.
- Keep the scalar null-context setter and scalar intercepted-write terminal on their current allocation-free shapes.
- Warmed-up route-free and owned structural writes allocate zero managed bytes beyond configured custom-interceptor work.
- Add no new friend-assembly access. The public provider facade is package infrastructure, only the built-in coordinator is supported, and applications are told not to implement it.
- Fallbacks remain mutable service composition; late nonunique services remain legal and invalidate dependent caches.
- Built-in lifecycle handlers and events uphold the synchronous no-throw contract. Application callback violations propagate without rollback, while Core finalization still runs.
- Concurrency tests use barriers, events, countdowns, or `AsyncTestHelpers.WaitUntilAsync`; never use `Task.Delay`, `Thread.Sleep`, or timing-dependent success.
- Local benchmark numbers are diagnostic only. Stop before the authoritative benchmark and Connector Tester handoff and ask the maintainer to run them on the stable machine.
- The temporary roadmap, design, plan, and SDD artifacts stay only while the stack is implemented and reviewed. Remove `docs/superpowers/` from final PR diffs before merge.
- Do not use em dashes in documentation, XML comments, PR text, or commit messages.
- Task 2 is one atomic three-phase implementation group. Assign all three phases to one implementation agent, do not hand off the shared worktree between them, and do not create an intermediate commit or claim a green boundary until Phase 3 completes. The public provider signature, Core terminal, fallback semantics, generated/Dynamic constructor attachment, and Tracking coordinator must change together without a temporary adapter.

## File and Responsibility Map

### Core ownership and provider boundary

- Create `src/Namotion.Interceptor/SubjectPropertyTypeClassifier.cs`: one cached runtime classifier for subject references, collections, dictionaries, and conservative ambiguous shapes.
- Modify `src/Namotion.Interceptor/SubjectPropertyMetadata.cs`: store the computed `CanContainSubjects` flag in every metadata instance.
- Create one public type per file under `src/Namotion.Interceptor/Ownership/`: `SubjectAttachmentContext.cs`, `SubjectDetachmentContext.cs`, `SubjectOwnershipWriteContext.cs`, `SubjectOwnershipView.cs`, `SubjectParentMembership.cs`, `SubjectParentMembershipEnumerable.cs`, and `SubjectParentMembershipEnumerator.cs`.
- Create `src/Namotion.Interceptor/Ownership/SubjectOwnershipOperation.cs`: public stack-only controlled mutation facade backed by one internal transition batch.
- Create `src/Namotion.Interceptor/Ownership/SubjectOwnershipNestingException.cs`: dedicated deterministic unsupported-nesting exception.
- Create `src/Namotion.Interceptor/Ownership/ContextAuthorityActivation.cs`: internal lifecycle-authority capture, explicit-root lease count, generation, transition token, and reentrant domain gate.
- Create `src/Namotion.Interceptor/Ownership/SubjectOwnershipState.cs`: internal explicit anchor, inline first parent, ordered overflow parents, active parent, domain, coordinator, generation, and pending transition.
- Create `src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs`: internal all-or-nothing multi-subject reservation, commit token, provider state, and deferred route-finalization storage.
- Create `src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs`: internal authority publication gate, coordinator binding map, domain-entry rule, activation, reservation, attach, detach, structural admission, retry, and cleanup.
- Modify `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`: inherit `IWriteInterceptor` and expose the exact attach, detach, and prepare methods from the spec.
- Modify `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`: expose direct `OwnershipReferenceCount`; Task 2 Phase 2 adds the conservative structural-write flag.
- Modify `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`: store nullable ownership state and inline route-free admission, remove fallback lifecycle callbacks, and enter ownership coordination only for potentially structural writes.
- Modify `src/Namotion.Interceptor/InterceptorSubjectExtensions.cs`: add strict attach, detach, and boolean/out attach-context APIs.
- Modify `src/Namotion.Interceptor/InterceptorSubjectContext.cs`: preserve activation in immutable states, add authority-safe publication, raw coordinator discovery, and exact transition-action selection without touching stable read/invoke/scalar paths.
- Modify `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`: carry the committed ownership activation record in `PropertyWriteContext<TProperty>` and expose it only during the matching unwind.
- Modify `src/Namotion.Interceptor/PropertyReferenceExtensions.cs`, `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`, and `src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs`: pass the conservative structural flag at every non-generated executor call site.
- Modify `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs` and `src/Namotion.Interceptor/Cache/WriteInterceptorChain.cs`: prepare at the terminal, commit around the backing write, and finalize the exact coordinator node in `finally`.

### Generator, Dynamic, and classification consumers

- Modify `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`, `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`, and create `src/Namotion.Interceptor.Generator/SubjectPropertySymbolClassifier.cs`: compute the compile-time conservative structural mirror.
- Modify `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`, `GeneratedMemberTable.cs`, and `SubjectBaseContract.cs`: emit the scalar direct path unchanged, the structural null-context handshake, and strict constructor attachment.
- Modify `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs`: strict constructor attachment and runtime-metadata structural admission.
- Modify `src/Namotion.Interceptor.Tracking/SubjectPropertyTypeExtensions.cs`: compatibility forwarders to the Core classifier with no second cache.

### Tracking ownership policy

- Create `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnershipTraversal.cs`: pooled cycle-aware property discovery, compatibility checks, active-parent selection, anchor detection, and affected-component release.
- Create `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleReconciliationState.cs`: committed property baselines, per-occurrence index metadata, and pooled provider state used only during a live Core operation.
- Rewrite `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`: implement the complete provider contract, dispatch its recursive seam at its ordered handler position, and reconcile after commit under Core's domain gate.
- Modify `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`: make `WithLifecycle()` recursive and remove `WithContextInheritance()`.
- Delete `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs` and `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyReferenceSet.cs`: their responsibilities move to the permanent Core ledger and built-in coordinator.
- Update ordering attributes that target `ContextInheritanceHandler` to target `LifecycleInterceptor`.

### Consumers, docs, and verification

- Modify `src/Namotion.Interceptor.Connectors/ISubjectFactory.cs`, `DefaultSubjectFactory.cs`, update appliers, path creation, OPC UA loaders/factories, first-party roots, samples, and HomeBlaze root ownership boundaries.
- Update `docs/interceptor.md`, `docs/tracking.md`, `docs/registry.md`, `docs/connectors.md`, `docs/connectors-subject-updates.md`, `docs/connectors-opcua-client.md`, and `docs/design/tracking-lifecycle.md` without duplicating the canonical model.
- Update Core, Tracking, Connectors, and any actually changed package Public API snapshots after inspecting each received diff.

---

### Task 1: Canonical Structural Property Classification

**Files:**
- Create: `src/Namotion.Interceptor/SubjectPropertyTypeClassifier.cs`
- Create: `src/Namotion.Interceptor.Tests/SubjectPropertyTypeClassifierTests.cs`
- Create: `src/Namotion.Interceptor.Generator/SubjectPropertySymbolClassifier.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/StructuralPropertyClassificationTests.cs`
- Modify: `src/Namotion.Interceptor/SubjectPropertyMetadata.cs`
- Modify: `src/Namotion.Interceptor.Tracking/SubjectPropertyTypeExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/SubjectPropertyTypeExtensionsTests.cs`
- Modify: `src/Namotion.Interceptor.Generator/Models/PropertyMetadata.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectMetadataExtractor.cs`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: existing Tracking classification behavior and current `SubjectPropertyMetadata` constructors.
- Produces: `SubjectPropertyTypeClassifier`, `SubjectPropertyMetadata.CanContainSubjects`, and generator `PropertyMetadata.CanContainSubjects`; Task 2 Phases 2 and 3 use these exact contracts for admission and interpretation.

- [ ] **Step 1: Add failing Core and generator parity tests**

Create a `[Theory]` corpus that covers primitive, string, subject, `object`, plain interface, `IEnumerable<subject>`, `IReadOnlyList<subject>`, `IDictionary<string, subject>`, `IReadOnlyDictionary<string, subject>`, non-generic containers, and ambiguous enumerable shapes. Assert mutual exclusivity and the OR invariant:

```csharp
[Theory]
[MemberData(nameof(ClassificationCases))]
public void WhenClassifyingPropertyType_ThenCoreAndExpectedShapeAgree(
    Type type, bool isReference, bool isCollection, bool isDictionary)
{
    // Act
    var actualReference = SubjectPropertyTypeClassifier.IsSubjectReferenceType(type);
    var actualCollection = SubjectPropertyTypeClassifier.IsSubjectCollectionType(type);
    var actualDictionary = SubjectPropertyTypeClassifier.IsSubjectDictionaryType(type);

    // Assert
    Assert.Equal(isReference, actualReference);
    Assert.Equal(isCollection, actualCollection);
    Assert.Equal(isDictionary, actualDictionary);
    Assert.Equal(isReference || isCollection || isDictionary,
        SubjectPropertyTypeClassifier.CanContainSubjects(type));
    Assert.InRange((actualReference ? 1 : 0) + (actualCollection ? 1 : 0) +
        (actualDictionary ? 1 : 0), 0, 1);
}
```

Add generator tests named `WhenGeneratedPropertyShapeIsKnown_ThenCompileTimeAndRuntimeClassificationAgree` and `WhenGeneratedPropertyShapeIsAmbiguous_ThenClassificationIsConservative`. Extract generator `PropertyMetadata` for each declared shape and compare its retained `CanContainSubjects` mirror with Core runtime classification of the compiled property type.

- [ ] **Step 2: Run the tests and capture RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectPropertyTypeClassifierTests" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~StructuralPropertyClassificationTests" --no-restore
```

Expected: compile failure because the Core classifier and metadata flag do not exist.

- [ ] **Step 3: Move the classifier and emit the compile-time mirror**

Move the four caches and their pure algorithms from Tracking into this public Core surface:

```csharp
public static class SubjectPropertyTypeClassifier
{
    public static bool CanContainSubjects(Type type);
    public static bool IsSubjectReferenceType(Type type);
    public static bool IsSubjectCollectionType(Type type);
    public static bool IsSubjectDictionaryType(Type type);
}
```

Add `public bool CanContainSubjects { get; }` to `SubjectPropertyMetadata` and compute it from `Type` in the private constructor. Do not add a constructor parameter that lets callers lie. Make Tracking's existing extension methods forward directly to Core; retain the generic primitive fast-path overload there until Task 2 Phase 2 removes all admission dependence on it.

In the generator, compute one conservative boolean from Roslyn symbols and retain it on generator `PropertyMetadata`. Task 2 Phase 2 emits that stored mirror into generated setter calls. Rely on the runtime Core constructor for public `SubjectPropertyMetadata`, and do not invoke reflection classification from a hot generated setter.

- [ ] **Step 4: Run focused suites and inspect snapshots**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectPropertyTypeClassifierTests|FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~SubjectPropertyTypeExtensionsTests" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~StructuralPropertyClassificationTests" --no-restore
```

Inspect every `.received.txt`. Accept only the intended Core classifier/metadata API using `apply_patch`; remove no unrelated snapshot entry. Task 1 must not change generated source snapshots.

- [ ] **Step 5: Verify and commit**

Run `git diff --check`, ensure no second classifier cache remains in Tracking, then commit:

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests
git commit -m "Centralize subject property classification"
```

### Task 2: Atomic Ownership Protocol

#### Preflight: Capture the clean callback-order baseline

Before adding any RED test or changing the provider interface, run the existing order oracles on the exact clean Task 1 commit and save their checked-in expected sequences in the task report:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~WhenAddingAndRemovingContext_ThenInterceptorsAreCalledInTheRightOrder" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~LifecycleEventsTests|FullyQualifiedName~ParentAccessDuringLifecycleTests|FullyQualifiedName~WritePipelineOrderTests" --no-restore
```

Do not update an order snapshot merely because the new implementation emits a different sequence. Treat any ordinary single-context difference as a bug unless the approved callback-visibility table requires the exact phase change.

#### Phase 1: Core State, Activation, and Provider Facade

**Files:**
- Create: `src/Namotion.Interceptor/Ownership/SubjectAttachmentContext.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectDetachmentContext.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipWriteContext.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipView.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectParentMembership.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectParentMembershipEnumerable.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectParentMembershipEnumerator.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipOperation.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipNestingException.cs`
- Create: `src/Namotion.Interceptor/Ownership/ContextAuthorityActivation.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipState.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs`
- Create: `src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs`
- Create: `src/Namotion.Interceptor.Tests/Ownership/SubjectAttachmentTests.cs`
- Create: `src/Namotion.Interceptor.Tests/Ownership/ContextAuthorityActivationTests.cs`
- Create: `src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipProviderContractTests.cs`
- Create: `src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipOperationTests.cs`
- Create: `src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipStateTests.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/ILifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectExtensions.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Tests/InterceptorTests.cs`
- Modify: `src/Namotion.Interceptor.Dynamic.Tests/DynamicSubjectTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/ContextInheritanceHandlerTests.cs`

**Interfaces:**
- Consumes: PR 1 `ContextOwnershipRoute` and `TryChangeOwnershipRoute`, plus Task 1 metadata classification.
- Produces: all public provider signatures from the spec, `OwnershipReferenceCount`, strict root APIs, activation records, coordinator binding, and the nullable per-executor ownership ledger used by Phase 2 and Tasks 3 through 6.

- [ ] **Implementation-map gate: freeze exact internal entry points before production edits**

Read the current context mutation, executor write, PR 1 route, and Tracking lifecycle implementations and write `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-2-internal-map.md`. It must give exact C# signatures, owning files, lock preconditions, transition states, and cleanup owner for:

```text
ContextAuthorityActivation: inactive -> activating -> active -> releasing -> absent
SubjectOwnershipCoordinator: explicit attach, explicit detach, structural admission, domain entry, context-mutation validation
SubjectOwnershipBatch: reserve, validate generations, commit, cancel, publish route, finalize route
InterceptorExecutor: route-free writer announce/drain and owned structural action selection
LifecycleInterceptor: attach provider, detach provider, property-write prepare, committed unwind reconciliation
```

For every deterministic schedule family in the spec, include one concrete test skeleton naming the events/barriers and the exact assertion point. Obtain a scoped independent review of this map before Step 1. If the current code makes any planned entry point or lock ownership impossible, amend the spec and this plan deliberately and re-review them; do not add an unnamed helper, temporary adapter, or extra monitor while implementing.

The map must also replace the Phase 3 prose entry “every production/test registration or ordering attribute found by `rg`” with the exact path of every hit before any Phase 3 edit. No file outside the amended exact Task 2 list may be changed without stopping to amend and re-review the map.

- [ ] **Step 1: Add strict explicit-root and provider-contract RED tests**

Cover these exact test names and outcomes:

- `WhenAttachingToPlainContext_ThenAttachContextIsReported`
- `WhenAttachingTwiceToSameContext_ThenSecondAttachThrowsWithoutMutation`
- `WhenAttachingToDifferentContext_ThenSecondAttachThrowsWithoutMutation`
- `WhenAttachingToExecutorOrCustomContext_ThenArgumentExceptionIsThrownBeforeResolution`
- `WhenDetachingWithMissingOrDifferentContext_ThenOperationThrowsWithoutMutation`
- `WhenDetachingAndReattaching_ThenNewAttachmentSucceeds`
- `WhenExplicitRootHasNoPropertyParent_ThenReferenceCountIsZero`
- `WhenContextHasNoLifecycleCoordinator_ThenOnlyExplicitRouteIsInstalled`
- `WhenContextResolvesSameCoordinatorThroughRepeatedPaths_ThenActivationSucceeds`
- `WhenContextResolvesDistinctEqualCoordinators_ThenActivationRejectsByReferenceIdentity`
- `WhenCoordinatorIsBoundToActiveDomain_ThenSecondDomainRejectsItUntilFinalRelease`
- `WhenFirstActivationFails_ThenCoordinatorBindingIsImmediatelyReusable`
- `WhenNullCoordinatorIsCaptured_ThenAddingFirstCoordinatorIsRejectedUntilFinalRelease`
- `WhenActivationStateIsInvalidated_ThenExactActivationRecordIsPreserved`
- `WhenLateNonuniqueServiceIsAdded_ThenActiveDomainRemainsValidAndDependentChainsInvalidate`
- `WhenSeveralDisconnectedRootsUseSameContext_ThenTheyShareOneDomainAndIndependentExplicitLeases`
- `WhenOwnershipApiArgumentsAreNullOrUnsupported_ThenDocumentedExceptionTypesAreUsed`

Representative strict API test:

```csharp
[Fact]
public void WhenAttachingTwiceToSameContext_ThenSecondAttachThrowsWithoutMutation()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();
    var subject = new Car();
    subject.AttachToContext(context);

    // Act & Assert
    Assert.Throws<InvalidOperationException>(() => subject.AttachToContext(context));
    Assert.True(subject.TryGetAttachContext(out var attached));
    Assert.Same(context, attached);
    Assert.Equal(0, ((IInterceptorExecutor)subject.Context).OwnershipReferenceCount);
}
```

Use a Core test provider double implementing the full new `ILifecycleInterceptor`; record every attach/detach operation and deliberately implement `Equals` as true across instances to prove reference identity. Add ledger tests for lazy allocation/final clearing, inline and overflow ordered memberships, duplicate property collapse, stable enumeration, generation mismatch, all-or-nothing commit, and misordered/repeated route publication/finalization.

- [ ] **Step 2: Run Core tests and capture RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectAttachmentTests|FullyQualifiedName~ContextAuthorityActivationTests|FullyQualifiedName~SubjectOwnershipProviderContractTests|FullyQualifiedName~SubjectOwnershipOperationTests|FullyQualifiedName~SubjectOwnershipStateTests" --no-restore
```

Expected: compile failure for the missing APIs and expanded provider contract.

- [ ] **Step 3: Add the exact public package-infrastructure API**

Implement the spec signatures without public constructors or general mutation handles:

```csharp
public interface ILifecycleInterceptor : IWriteInterceptor
{
    void AttachSubjectToContext(
        ref SubjectAttachmentContext context,
        ref SubjectOwnershipOperation operation);

    void DetachSubjectFromContext(
        ref SubjectDetachmentContext context,
        ref SubjectOwnershipOperation operation);

    void PrepareSubjectPropertyWrite<TProperty>(
        ref SubjectOwnershipWriteContext<TProperty> context,
        ref SubjectOwnershipOperation operation);
}

public sealed class SubjectOwnershipNestingException : InvalidOperationException
{
    public SubjectOwnershipNestingException(string message) : base(message) { }
}
```

Keep the operation and view names/signatures consistent across Core and Tracking:

```csharp
public ref struct SubjectOwnershipOperation
{
    public IInterceptorSubjectContext OwnershipDomain { get; }
    public object? ProviderState { get; set; }

    public SubjectOwnershipView GetView(IInterceptorSubject subject);
    public void ReserveExplicitAttachment(IInterceptorSubject subject);
    public void ReserveExplicitDetachment(IInterceptorSubject subject);
    public void ReserveParentAddition(IInterceptorSubject subject, PropertyReference parentProperty);
    public void ReserveParentRemoval(IInterceptorSubject subject, PropertyReference parentProperty);
    public void SelectActiveParent(IInterceptorSubject subject, PropertyReference? parentProperty);
    public void ReserveFinalRelease(IInterceptorSubject subject);
    public bool TryCommit();
    public void PublishSelectedRoute(IInterceptorSubject subject);
    public void FinalizeSelectedRoute(IInterceptorSubject subject);
}

public readonly ref struct SubjectOwnershipView
{
    public bool Exists { get; }
    public IInterceptorSubjectContext? ExplicitAttachContext { get; }
    public IInterceptorSubjectContext? OwnershipDomain { get; }
    public PropertyReference? ActiveParentProperty { get; }
    public int ReferenceCount { get; }
    public long Generation { get; }
    public SubjectParentMembershipEnumerable ParentMemberships { get; }
}
```

Implement the three input `readonly ref struct` types, `SubjectOwnershipOperation`, `SubjectOwnershipView`, `SubjectParentMembership`, and the stack-only enumerable/enumerator exactly as named in the spec. `ProviderState` is the only provider-owned mutable slot. Every mutation method validates the operation token, generation, permitted phase, and subject membership before delegating to its internal batch.

Update the built-in `LifecycleInterceptor` and every Core, Dynamic, and Tracking test double to the new method signatures in this atomic group. Its methods must consume the permanent operation facade immediately; do not keep the old subject-only callbacks or a fallback-driven adapter. Phase 3 fills the complete recursive graph policy into these same methods without replacing the facade or signatures.

Delete `InterceptorExecutor.AddFallbackContext` and `RemoveFallbackContext` in this phase, and update the old fallback-lifecycle characterization tests to the permanent composition-only contract. This closes every compile-time reference to the removed callbacks before the phase ends. Do not defer the deletion to Phase 3.

- [ ] **Step 4: Implement compact executor state and explicit operations**

Use one nullable `SubjectOwnershipState` field on `InterceptorExecutor`. Keep the first parent inline and allocate ordered overflow only on the second distinct `PropertyReference`. Store explicit attachment separately, and clear the complete state when explicit attachment, parent membership, and pending transition are all absent.

Add these public extensions with strict target validation before context resolution:

```csharp
public static void AttachToContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static void DetachFromContext(
    this IInterceptorSubject subject,
    IInterceptorSubjectContext context);

public static bool TryGetAttachContext(
    this IInterceptorSubject subject,
    out IInterceptorSubjectContext? context);
```

The supported target check is exact `context.GetType() == typeof(InterceptorSubjectContext)`. Reject `InterceptorExecutor`, subclasses, and other implementations with `ArgumentException`. A second attach always rejects, including the same target. Exact detach removes only the explicit anchor; Task 3 adds parent transfer behavior.

- [ ] **Step 5: Implement activation, raw coordinator discovery, and binding**

Add a derived immutable context state containing a lazy `ContextAuthorityActivation`; route-free inactive states keep their existing shape. Preserve the exact activation record through service additions, fallback changes, route changes, and `WithoutCaches()`.

Under one permanent authority publication gate:

1. walk local services, fallbacks, and the ownership route with pooled cycle-aware buffers;
2. collect `ILifecycleInterceptor` by `ReferenceEquals` without default-equality deduplication;
3. reject more than one distinct instance;
4. bind a non-null coordinator to at most one active plain context;
5. publish `Activating`, `Active`, or `Releasing` and its lease count atomically;
6. remove zero-lease failed activation and final-release bindings before they become unreachable.

Do not call user factories or callbacks under this gate. `TryAddService` predicates and factories continue under only the existing context mutation lock; after they return, acquire the authority gate, re-read state, validate the prospective coordinator for active reverse dependencies, and publish.

- [ ] **Step 6: Implement deterministic domain entry**

Track active domain entries, context-mutation callback scope, and route-free admission scope in reusable thread-static state. Use this rule before probing a target monitor:

```text
same domain                         -> enter reentrantly
no domain/callback/SyncRoot held    -> wait, then enter
different domain held              -> throw SubjectOwnershipNestingException
TryAddService callback scope held   -> throw SubjectOwnershipNestingException
initiating subject SyncRoot held    -> throw SubjectOwnershipNestingException
```

Exception text must tell the caller to defer the operation until the current callback or ownership operation returns. Ordinary contention has no timeout and never throws.

- [ ] **Step 7: Complete attach/detach provider coordination**

For a coordinator domain, build a live `SubjectOwnershipOperation`, call the provider while holding only the reentrant domain gate, require one all-or-nothing `TryCommit`, publish the exact explicit route, and finalize it in `finally`. For a null-coordinator domain, commit only the explicit subject and route with no graph walk or callback.

If a same-domain inherited subject receives its first explicit anchor, switch to the explicit route without callbacks. If a different domain owns it, throw before mutation. Detach keeps the old route visible through callbacks; clear after callbacks when no parent survives. Task 3 supplies the transfer selection when a parent survives.

- [ ] **Step 8: Pin context-mutation and nesting schedules**

Add event-controlled tests for:

- activation racing `AddService`, `TryAddService`, fallback addition, and deeper-target mutation;
- coordinator-preserving publication winning with activation retry;
- coordinator-changing publication rejecting before state publication;
- ordinary attach contention waiting and both calls resolving to strict first-wins state;
- a transition called from a `TryAddService` predicate/factory rejecting with the target gate both free and occupied;
- same-subject `SyncRoot` nesting rejecting before provider invocation.

Use `ManualResetEventSlim`, `Barrier`, and `CountdownEvent`; assert no callback or factory count changes on rejected operations.

- [ ] **Step 9: Run the phase-local Core and Public API verification**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectAttachmentTests|FullyQualifiedName~ContextAuthorityActivationTests|FullyQualifiedName~SubjectOwnershipProviderContractTests|FullyQualifiedName~SubjectOwnershipOperationTests|FullyQualifiedName~SubjectOwnershipStateTests" --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~ContextOwnershipRouteTests|FullyQualifiedName~ContextSubtreeServiceTests|FullyQualifiedName~ContextConcurrencyTests" --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet build src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj --no-restore
```

Inspect the public snapshot for only the provider facade, nesting exception, strict root extensions, classifier/metadata, and `OwnershipReferenceCount`. A phase-local command may remain RED only where a named Phase 2 or Phase 3 RED test already proves the intentionally incomplete atomic protocol. It must not fail from a missing method, stale test double, old fallback callback, or uncompilable project.

- [ ] **Step 10: Static allocation/lock review and continue without committing**

Confirm by diff inspection that a never-owned executor has only one nullable ownership reference plus the inline admission word added in Phase 2, inactive contexts have no activation allocation, the authority gate is absent from `GetServices`, read, invoke, and scalar write entry, and no new Tracking friend access exists. Run `git diff --check`, then continue directly to Phase 2 in the same worktree and agent session. Do not commit or hand off this incomplete atomic group.

#### Phase 2: Prospective Structural Admission and Constructor Cutover

**Files:**
- Create: `src/Namotion.Interceptor.Tests/Ownership/StructuralOwnershipAdmissionTests.cs`
- Create: `src/Namotion.Interceptor.Tests/Ownership/StructuralOwnershipConcurrencyTests.cs`
- Create: `src/Namotion.Interceptor.Generator.Tests/StructuralSetterShapeTests.cs`
- Create: `src/Namotion.Interceptor.Dynamic.Tests/DynamicOwnershipAdmissionTests.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IInterceptorExecutor.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorChain.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs`
- Modify: `src/Namotion.Interceptor.Generator/GeneratedMemberTable.cs`
- Modify: `src/Namotion.Interceptor.Generator/SubjectBaseContract.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/Snapshots/*.verified.txt`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubjectFactory.cs`
- Modify: `src/Namotion.Interceptor/PropertyReferenceExtensions.cs`
- Modify: `src/Namotion.Interceptor.Registry/Abstractions/RegisteredSubject.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextFunctionCacheTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseShapeTests.cs`
- Modify: `src/Namotion.Interceptor.Generator.Tests/SubjectBaseDiagnosticsTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: Task 1 `CanContainSubjects` flags and Phase 1 activation, provider facade, state, and batch.
- Produces: inline route-free admission, transition-generation write actions, terminal reservation/commit, committed-unwind operation exposure, strict generated/Dynamic context construction, and null-context structural safety used by Phase 3 and Tasks 3 through 4.

- [ ] **Step 1: Add RED tests for precommit rejection and old-chain races**

Use the Core provider double to reserve a proposed child and reject a different domain. Cover:

- `WhenFinalStructuralValueIsIncompatible_ThenBackingFieldDoesNotChange`
- `WhenOuterInterceptorTransformsValue_ThenTerminalValidatesTransformedValue`
- `WhenBackingWriteThrows_ThenCompleteReservationIsCancelled`
- `WhenRouteFreeCachedChainRacesAdoption_ThenOldWriteDrainsBeforePendingDomainPublishes`
- `WhenAdoptionWinsRace_ThenStructuralWriteUsesTransitionGenerationAction`
- `WhenNullContextStructuralSetterRacesExecutorPublication_ThenWriteOrReservationLinearizes`
- `WhenScalarNullContextSetterRuns_ThenGeneratedShapeRemainsDirect`
- `WhenCommittedProviderUnwindThrows_ThenCoreFinalizationStillClearsPendingState`
- `WhenTransitionActionInjectsCoordinator_ThenItsConcreteOrderMatchesStableResolvedChain`
- `WhenOuterCustomInterceptorRecordsRejectedAttempt_ThenLibraryStateStillRemainsUncommitted`
- `WhenGeneratedContextConstructorRuns_ThenItCreatesStrictExplicitAttachment`
- `WhenDynamicContextConstructorRuns_ThenItCreatesStrictExplicitAttachment`
- `WhenContextConstructedChildLeavesParent_ThenExplicitAnchorRemainsUntilDetach`
- `WhenParameterlessChildLeavesFinalParent_ThenInheritedOwnershipReleasesAutomatically`
- `WhenUnownedChildFallbackComposesDifferentCoordinator_ThenParentWriteRejectsWithoutAdoption`

The key fail-fast assertion is:

```csharp
// Arrange
var originalOwnership = SubjectOwnershipTestSnapshot.Capture(childFromOtherDomain);

// Act & Assert
Assert.Throws<InvalidOperationException>(() => parent.Child = childFromOtherDomain);
Assert.Same(originalChild, parent.Child);
Assert.Equal(originalOwnership, SubjectOwnershipTestSnapshot.Capture(childFromOtherDomain));
Assert.Empty(provider.PropertyCallbacks);
```

The internal test snapshot contains exact explicit attach context, direct reference count, ownership domain, route descriptor reference, generation, and pending-transition state. The separate unowned-fallback test starts with no anchor or parent membership, composes a different coordinator only through a public fallback, rejects the parent write, and asserts the unowned subject remains route-free with its composition unchanged.

- [ ] **Step 2: Run the focused tests and capture RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~StructuralOwnershipAdmissionTests|FullyQualifiedName~StructuralOwnershipConcurrencyTests" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~StructuralSetterShapeTests" --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --filter "FullyQualifiedName~DynamicOwnershipAdmissionTests" --no-restore
```

Expected: failures because structural writes still select cached actions before domain admission and generated null-context setters do not handshake.

- [ ] **Step 3: Add the inline route-free admission word**

Store `ActiveWriterCount` and `Generation` inline on every executor without allocating a waiter. For potentially structural route-free writes, announce under the subject's `SyncRoot`, release that lock while the chain runs, and clear in `finally`. Track the active admission in reusable thread-static state so a synchronous self-upgrade throws instead of waiting on itself.

When adoption sees an earlier writer, cancel the complete tentative batch, release domain/publication/subject locks, wait on generation change, re-enter the domain gate, and restart terminal discovery from the already transformed value. Never replay the write-interceptor prefix.

- [ ] **Step 4: Move domain admission ahead of action selection**

Change executor write entry to accept the compile-time or runtime `canContainSubjects` flag. Known scalar types keep the existing action selection exactly. A structural write in a coordinator domain captures the activation, enters its reentrant gate, re-reads state/generation, and only then selects its stable or transition action. A null-coordinator explicit domain keeps the ordinary cached path.

Use this exact executor signature in Core, the generated helper, base-contract checks, Dynamic entry/factory, `PropertyReferenceExtensions`, and Registry's `RegisteredSubject` delegate:

```csharp
bool SetPropertyValue<TProperty>(
    string propertyName,
    TProperty newValue,
    TProperty currentValue,
    bool canContainSubjects,
    Action<IInterceptorSubject, TProperty> writeValue);
```

Transition actions contain local/fallback interceptors plus the exact coordinator ordered by its normal concrete identity. They are cached only by transition generation and discarded on publish, transfer, cancel, clear, or supersession. They must not expose pending parent services through `GetServices`.

- [ ] **Step 5: Prepare and commit at the write terminal**

At the terminal, construct `SubjectOwnershipWriteContext<TProperty>` from `context.Property`, the final transformed `context.NewValue`, and the expected revision. Call `PrepareSubjectPropertyWrite` before taking the backing-write `SyncRoot`. Require every reserved generation and the initiating revision to match before commit.

Perform this sequence once:

```text
prepare complete reservation batch
validate batch and initiating revision
backing field write under subject SyncRoot
commit write revision and ownership ledger
expose committed operation only to exact coordinator unwind
finalize deferred route work in finally
```

On a stale baseline/generation, cancel and retry only terminal work. On incompatibility, cancel and throw before the backing write. On backing-write exception, cancel and rethrow.

- [ ] **Step 6: Expose the operation only during exact provider unwind**

Add:

```csharp
public bool TryGetSubjectOwnershipOperation(
    out SubjectOwnershipOperation operation);
```

to `PropertyWriteContext<TProperty>`. It returns true only while the exact coordinator node unwinds a committed matching operation. Wrap that node in `finally`, not the entire application interceptor chain. A route-free fallback-composed coordinator receives no operation, calls `next` normally, and must remain transparent.

- [ ] **Step 7: Emit strict constructor attachment and the structural null-context handshake**

Change generated and Dynamic context constructors from fallback mutation to:

```csharp
((IInterceptorSubject)this).AttachToContext(context);
```

Keep parameterless constructors route-free. Update generator snapshots, generated member tests, Dynamic tests, and XML comments so Task 2's final Tracking gates exercise real explicit roots rather than the removed fallback-lifecycle shorthand. Old generated assemblies are unsupported and must be rebuilt.

Preserve the scalar generated shape as a direct `_context is null` field write. For a property whose compile-time mirror is true, emit:

```csharp
if (_context is null)
{
    lock (((IInterceptorSubject)this).SyncRoot)
    {
        if (_context is null)
        {
            setValue(this, newValue);
            return true;
        }
    }
}

return _context.SetPropertyValue(
    propertyName, newValue, currentValue, canContainSubjects: true, setValue);
```

Use `InterceptorExecutor.GetOrCreate`'s compare-and-swap winner if executor publication is needed while holding `SyncRoot`. Update generated-member/base-contract signatures consistently. Dynamic writes use `SubjectPropertyMetadata.CanContainSubjects` and the same executor admission path.

- [ ] **Step 8: Add deterministic admission schedules**

Use barriers/events to force both orders for cached route-free write versus adoption and null-context structural setter versus executor publication. Add a same-domain reentrant write test and a different-domain nested write test with target gate both free and occupied. Assert the different-domain result is always `SubjectOwnershipNestingException` before an interceptor prefix or field change.

- [ ] **Step 9: Run focused and regression suites for the completed admission phase**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~StructuralOwnershipAdmissionTests|FullyQualifiedName~StructuralOwnershipConcurrencyTests|FullyQualifiedName~WhenWritingProperties_ThenInterceptorsAreCalledInTheRightOrder" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~StructuralSetterShapeTests|FullyQualifiedName~GeneratorShapeBehaviorTests|FullyQualifiedName~SubjectBaseShapeTests" --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --filter "FullyQualifiedName~DynamicOwnershipAdmissionTests|FullyQualifiedName~DynamicSubjectExecutorPublicationTests" --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --filter "FullyQualifiedName~InterceptorSubjectTests|FullyQualifiedName~SourceGeneratorTests" --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --filter "FullyQualifiedName~DynamicSubjectTests" --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
dotnet build src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj --no-restore
```

- [ ] **Step 10: Static hot-path audit and continue without committing**

Inspect generated scalar snapshots and the Core terminal diff. Confirm scalar writes have no ownership branch after the compile-time false flag is inlined, route-free structural admission creates no waiter/task/closure, and transition actions cannot survive their generation. Run `git diff --check`, then continue directly to Phase 3 in the same worktree and agent session. Do not commit or hand off until the Tracking provider closes the atomic group.

#### Phase 3: Tracking Lifecycle Integration

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnershipTraversal.cs`
- Create: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleReconciliationState.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipMembershipTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/FallbackCompositionLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectPropertyLifecycleChange.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/LifecycleInterceptorTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/RecursiveAttachTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyReferenceSetTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Parent/ParentTrackingHandler.cs`
- Modify: `src/Namotion.Interceptor.Registry/SubjectRegistry.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Monitoring/SourceMonitor.cs`
- Modify: `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
- Modify: `src/Namotion.Interceptor.Registry/InterceptorSubjectContextExtensions.cs`
- Inventory checkpoint: the Task 2 internal map replaces this line with every exact production/test registration and ordering-attribute path returned by `rg -n "WithContextInheritance|ContextInheritanceHandler" src docs --glob '*.cs' --glob '*.md'` before Phase 1 Step 1.
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/ContextInheritanceHandler.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Lifecycle/PropertyReferenceSet.cs`
- Delete or replace: `src/Namotion.Interceptor.Tracking.Tests/ContextInheritanceHandlerTests.cs`
- Delete or replace: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/PropertyReferenceSetTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: Phase 1 provider facade and ledger plus Phase 2 committed unwind operation.
- Produces: complete built-in recursive lifecycle coordination, parent membership reservations, callback-phase route publication, composition-only fallbacks, and Core-owned reference counts. Tasks 3 and 4 extend its graph and concurrency coverage without replacing this dispatch loop.

- [ ] **Step 1: Add RED tests for recursive lifecycle and pure composition**

Cover these exact contracts:

- `WhenWithLifecycleIsConfigured_ThenPrepopulatedSubtreeAttachesRecursively`
- `WhenFallbackIsAddedOrRemoved_ThenNoLifecycleCallbackOrReferenceCountChanges`
- `WhenPropertyChildFirstEntersDomain_ThenOneMembershipAndOneRouteAreCommitted`
- `WhenRepeatedCollectionOccurrenceChanges_ThenReferenceCountRemainsOneAndIndexRefreshes`
- `WhenFinalParentMembershipIsRemoved_ThenChildDetachesAndOwnershipStateClears`
- `WhenLifecycleCoordinatorDispatchReachesItsOwnHandlerSlot_ThenRouteVisibilityMatchesCharacterization`
- `WhenLifecycleIsNotConfigured_ThenExplicitRootDoesNotRecursivelyOwnChildren`
- `WhenExistingCollectionIsMutatedWithoutSetter_ThenNoLifecycleTransitionIsClaimed`

For pure composition, attach a root explicitly, record lifecycle/event/registry counts, add and remove a fallback containing the same coordinator, and assert every count plus `OwnershipReferenceCount` remains unchanged while a nonunique service becomes visible and then disappears.

- [ ] **Step 2: Run Tracking tests and capture RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipMembershipTests|FullyQualifiedName~FallbackCompositionLifecycleTests" --no-restore
```

Expected: the pure-composition oracle is already green after Phase 1. Recursive membership, reference-count, and callback-phase tests remain RED because the complete built-in recursive provider is not implemented yet. Any fallback-lifecycle callback at this point is an unexpected regression, not an expected failure.

- [ ] **Step 3: Make the built-in coordinator the recursive handler**

Rewrite `LifecycleInterceptor` so it implements all three `ILifecycleInterceptor` methods plus `IWriteInterceptor` and `ILifecycleHandler`. `WithLifecycle()` registers exactly this one instance. Remove `WithContextInheritance()` and all direct `ContextInheritanceHandler` registration.

Confirm the Phase 1 deletion of `InterceptorExecutor.AddFallbackContext` and `RemoveFallbackContext` remains intact. The inherited base implementations are the only public fallback behavior and perform service composition, reverse invalidation, and return-value handling without lifecycle resolution or callbacks.

During ordered lifecycle dispatch, compare handler identity with `ReferenceEquals(handler, this)`. At that one slot, call the internal recursive seam with the live `SubjectOwnershipOperation` instead of invoking an ordinary handler callback. Invoke every other handler normally. Move ordering attributes that named `ContextInheritanceHandler` to `LifecycleInterceptor`.

- [ ] **Step 4: Replace Tracking's root sentinel and reference-count data**

Delete `_attachedSubjects` as the ownership source of truth and delete `PropertyReferenceSet`. Use `operation.GetView(subject)` and reservation methods for explicit and property membership. In `LifecycleInterceptorExtensions`, make `GetReferenceCount` return `((IInterceptorExecutor)subject.Context).OwnershipReferenceCount`; remove the subject-data counter key plus increment/decrement helpers.

Keep only provider reconciliation data in Tracking:

```text
PropertyReference -> committed structural value baseline
PropertyReference -> per-occurrence index metadata
live operation     -> pooled discovery/callback projection state
```

Record the committed baseline before invoking any callback so a later null or replacement write can balance an attach even if application callback code violates the no-throw contract.

- [ ] **Step 5: Reserve parent changes from the final transformed value**

In `PrepareSubjectPropertyWrite`, compare the final proposed value with the committed baseline, enumerate old/new subjects, and reserve parent additions/removals once per distinct parent property. Preserve occurrence indices for property-handler metadata without incrementing Core reference count for duplicates in one collection property.

Before reading a discovered child's structural properties, ensure its executor exists and reserve it against the current generation. Use Phase 2's drain/retry result for an active route-free writer. Put all traversal lists, sets, and batch storage in existing thread-static pools and clear every item before returning them.

- [ ] **Step 6: Publish and clear routes at the characterized phase**

For attach:

```text
handlers ordered before coordinator see local/previous route
coordinator publishes selected route
coordinator recursively attaches descendants
handlers ordered after coordinator and subject handler see inherited route
```

For final detach:

```text
SubjectDetaching, subject handler, and handlers before coordinator see old route
coordinator recursively detaches descendants
coordinator clears or transfers route
handlers after coordinator see new route or no route
```

Property lifecycle handlers remain in their characterized phase. Explicit-to-inherited transfer is handled outside a lifecycle callback sequence and emits no detach/attach pair.

- [ ] **Step 7: Migrate optional-inheritance call sites and tests**

Replace every `.WithContextInheritance()` with `.WithLifecycle()` only when no enclosing helper already calls `WithLifecycle()`. Remove duplicate calls from `WithFullPropertyTracking`, Registry, Hosting, Sources, samples, and tests. Replace tests that assert shallow lifecycle with tests that assert recursive lifecycle is always present. Remove the public type and method from the Tracking Public API snapshot.

- [ ] **Step 8: Run order, lifecycle, registry, and API suites**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipMembershipTests|FullyQualifiedName~FallbackCompositionLifecycleTests|FullyQualifiedName~LifecycleEventsTests|FullyQualifiedName~RecursiveAttachTests|FullyQualifiedName~ParentAccessDuringLifecycleTests|FullyQualifiedName~WritePipelineOrderTests" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi" --no-restore
```

Inspect all changed Verify files. The ordinary callback-order snapshots must be byte-for-byte unchanged unless only obsolete type names disappear from a Public API snapshot.

- [ ] **Step 9: Close the atomic group, run its complete green gate, and commit**

Run:

```bash
rg -n "_attachedSubjects|PropertyReferenceSet|WithContextInheritance|ContextInheritanceHandler|IncrementReferenceCount|DecrementReferenceCount" src docs --glob '*.cs' --glob '*.md'
git diff --check
```

Expected: no functional legacy inheritance, root sentinel, or boxed reference-count implementation remains. Commit:

```bash
dotnet build src/Namotion.Interceptor/Namotion.Interceptor.csproj --no-restore
dotnet build src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj --no-restore
dotnet build src/Namotion.Interceptor.Generator/Namotion.Interceptor.Generator.csproj --no-restore
dotnet build src/Namotion.Interceptor.Dynamic/Namotion.Interceptor.Dynamic.csproj --no-restore
dotnet build src/Namotion.Interceptor.Registry/Namotion.Interceptor.Registry.csproj --no-restore
dotnet build src/Namotion.Interceptor.Hosting/Namotion.Interceptor.Hosting.csproj --no-restore
dotnet build src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Hosting.Tests/Namotion.Interceptor.Hosting.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore
dotnet build src/Namotion.Interceptor.slnx --no-restore
git add src docs
git commit -m "Enforce explicit subject ownership"
```

This is the first commit and handoff boundary after Task 1. It must compile Core, Tracking, Generator, Dynamic, and Registry and leave every pre-existing focused suite touched by all three phases green.

### Task 3: Deterministic Parent Routes, Cycles, and Component Release

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipRouteSelectionTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipCycleTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipCompatibilityTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnershipTraversal.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipState.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs`
- Modify: `src/Namotion.Interceptor.Tests/Ownership/SubjectAttachmentTests.cs`
- Inventory checkpoint: the Task 3 route map replaces this line with every exact Registry parent/DAG/cycle test path that encodes old all-fallback inheritance before Step 1.

**Interfaces:**
- Consumes: Task 2 parent reservations and recursive callback seam.
- Produces: stable insertion-order membership, earliest compatible acyclic route, explicit-to-inherited transfer, complete-subtree incompatibility rejection, and anchored-component release. Task 4 stress-tests these exact invariants.

- [ ] **Implementation-map gate: freeze route and component-release entry points**

Before adding RED tests, write `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-3-route-map.md` with exact C# signatures and file paths for active-parent candidate enumeration, acyclic-route selection, explicit-to-inherited transfer, descendant compatibility validation, anchor-loss detection, and unanchored-component release. Include one concrete object, collection, dictionary, shared-DAG, and cyclic-graph test skeleton, with exact membership insertion order and expected descriptor identity. Replace the generic Registry test-file entry above with every exact path returned by the parent/DAG/cycle audit.

Obtain an independent scoped review and amend this plan with the exact map before Step 1. If the map would require a second graph ledger, a full graph walk on secondary-parent addition, unordered parent storage, or a temporary compatibility route, stop and amend/re-review the design rather than editing production.

- [ ] **Step 1: Add RED tests for route selection and capability boundaries**

Cover:

- `WhenSecondCompatibleParentIsAdded_ThenReferenceCountIncrementsWithoutRouteChurn`
- `WhenActiveParentIsRemoved_ThenEarliestSurvivingAcyclicParentBecomesRoute`
- `WhenExplicitAnchorExists_ThenParentMembershipDoesNotReplaceExplicitRoute`
- `WhenExplicitAnchorIsDetachedWithParentRemaining_ThenRouteTransfersWithoutLifecycleChurn`
- `WhenChildOrDescendantBelongsToDifferentDomain_ThenParentBackingValueDoesNotCommit`
- `WhenTwoPlainContextsShareCoordinator_ThenTheirSubjectsRemainIncompatible`
- `WhenRepeatedOccurrencesUseOneParentProperty_ThenTheyCountAsOneMembership`
- `WhenSameSubjectUsesTwoParentProperties_ThenTheyCountAsTwoMemberships`
- `WhenChildHasLocalAndFallbackBranchServices_ThenTheyFlowToDescendantsAndRemainSiblingIsolated`

Assert route descriptors change only on active-route transfer, not on secondary membership changes.

- [ ] **Step 2: Add RED cycle, DAG, and final-anchor tests**

Build object-property, collection, and dictionary graphs for:

- one anchored cycle with a back-edge;
- two anchors into the same cycle;
- shared DAG with two compatible parents;
- route selection that must skip a cycle-producing earliest membership;
- final external anchor removal from a cycle whose internal reference counts remain nonzero.

After final release, assert no attach context, no ownership route via test reflection, zero reference count, no registry/parent entries, and collectability through `WeakReference` after clearing test locals.

- [ ] **Step 3: Run focused tests and capture RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipRouteSelectionTests|FullyQualifiedName~OwnershipCycleTests|FullyQualifiedName~OwnershipCompatibilityTests" --no-restore
```

Expected: failures on multiple parents, transfer, cross-domain precommit, or unanchored component release.

- [ ] **Step 4: Implement ordered membership and active route selection**

Keep the first parent inline; allocate an insertion-ordered overflow only for the second distinct parent property. `SelectActiveParent` chooses explicit route first, otherwise the earliest surviving compatible membership whose target ancestry does not contain the child executor. Secondary add/remove remains O(1). Only active removal scans ordered survivors and route ancestry.

Every route install, transfer, or clear constructs a fresh PR 1 `ContextOwnershipRoute`. Never reinstall a cleared descriptor. Match both ownership generation and exact descriptor before a stale operation changes a route.

- [ ] **Step 5: Validate the complete proposed subtree**

Traverse every reachable new child before the parent terminal commits. Reject when any subject has a different exact `OwnershipDomain` or when its branch composition would change the domain's captured coordinator identity. Treat unowned subjects, same-domain owned subjects, repeated references, shared DAG nodes, and same-domain cycles as compatible.

Reserve each distinct subject once per batch. Do not publish routes, invoke callbacks, or update reconciliation baselines during validation.

- [ ] **Step 6: Implement explicit-to-inherited transfer**

On exact explicit detach with a compatible parent membership, reserve removal of only the explicit anchor, choose the earliest surviving acyclic parent, publish the new route atomically, invalidate affected compiled chains, decrement the explicit-root lease, and return. Emit no `SubjectDetaching`, `SubjectAttached`, or duplicate property lifecycle event. Service resolution is allowed to change at the route-switch linearization point.

- [ ] **Step 7: Release unanchored components**

When final explicit/external route anchor may disappear, walk only the affected component with pooled worklists. Mark an anchor if any subject has an explicit attachment or any incoming membership from outside the component. If none exists, reserve final release for the complete component, run balanced detach in characterized order, and clear every internal membership and route even when internal counts are nonzero.

- [ ] **Step 8: Run graph and consumer tests**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectAttachmentTests" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipRouteSelectionTests|FullyQualifiedName~OwnershipCycleTests|FullyQualifiedName~OwnershipCompatibilityTests|FullyQualifiedName~LifecycleInterceptorTests" --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --filter "FullyQualifiedName~DynamicPropertyLifecycleTests|FullyQualifiedName~AggregatedContextLifecycleTests|FullyQualifiedName~Parent" --no-restore
```

- [ ] **Step 9: Review allocation and traversal boundaries, then commit**

Confirm common one-parent state has no collection, secondary changes do not full-walk, route transfer walks only remaining memberships/ancestry, and component traversal occurs only on possible anchor loss. Run `git diff --check`, then commit:

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests src/Namotion.Interceptor.Registry.Tests
git commit -m "Enforce one effective ownership route"
```

### Task 4: Ownership Concurrency, Reentrancy, Failure, and Lifetime Hardening

**Files:**
- Create: `src/Namotion.Interceptor.Tests/Ownership/SubjectOwnershipNestingTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipConcurrencyTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipLifetimeTests.cs`
- Create: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/OwnershipCallbackFailureTests.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipCoordinator.cs`
- Modify: `src/Namotion.Interceptor/Ownership/SubjectOwnershipBatch.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptor.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/SubjectOwnershipTraversal.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleReconciliationState.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/ConcurrentWriteLifecycleTests.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/LifecycleEventsTests.cs`

**Interfaces:**
- Consumes: Tasks 2 and 3 complete ownership semantics.
- Produces: deterministic lock schedules, stale-generation safety, quiescent invariants, callback-violation cleanup, and weak-reference release evidence required before consumer migration.

- [ ] **Implementation-map gate: freeze concurrency seams and cleanup ownership**

Before adding schedules, write `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-4-concurrency-map.md`. For every test family in Steps 1 through 6, name the exact production method where each event/barrier is inserted, the locks held at that seam, the generation/token/descriptor captured, the winning linearization point, and the `finally` block that releases pooled/TLS/provider state. Include concrete test skeletons for same-domain wait, cross-domain deterministic rejection with free and occupied target gates, route-free unfinished-chain mutation, stale descriptor ABA, callback throw after commit, and weak release.

Obtain an independent scoped review and amend this plan with the exact method/file map before Step 1. If deterministic observation needs a new production hook, timeout, global serializer, or retained test-only branch, stop and amend/re-review the plan instead of adding it.

- [ ] **Step 1: Add every deterministic schedule from the spec**

Create event-controlled tests for all listed seams: reserved descendant write, both cached-chain/adoption orders, first executor publication race, two subjects in one domain, same-domain reentrant write, activation/service mutation, fallback/deeper mutation, superseded commit, callback route visibility, and stale attach/detach/transfer/final-release using a later descriptor with the same target.

For ordinary same-domain contention, start all writers behind a `Barrier`, block the first callback with `ManualResetEventSlim`, then release it. Assert every caller completes, no ownership exception is recorded, the final property value owns exactly one child, and each displaced child is balanced.

- [ ] **Step 2: Add deterministic unsupported-nesting tests**

Test A-to-B and B-to-A writes from lifecycle callbacks with target gates free and occupied. From an unfinished route-free interceptor chain, test explicit attach, explicit detach, structural write, and a coordinator-changing context mutation with the target gate both free and occupied. Add a paired coordinator-preserving nonunique service mutation and prove it remains legal and invalidates the dependent chain. Test attach, detach, and structural write from `TryAddService` predicate and factory scopes. Test a structural setter while the caller holds the initiating subject's `SyncRoot`. In every unsupported schedule, assert the same `SubjectOwnershipNestingException` is thrown before target availability, interceptor prefix, factory side effect, backing-field write, context-state publication, or ownership change.

- [ ] **Step 3: Add different-domain independence test**

Block one structural callback in domain A, execute a structural write in domain B, and prove B completes before A is released. This pins that there is no global structural serializer and the authority publication gate is not held through callbacks.

Also compose the same coordinator into a route-free fallback chain and run its transparent `WriteProperty` call while its bound domain is blocked. Assert `TryGetSubjectOwnershipOperation` returns false on the route-free call, `next` executes normally, and no lifecycle baseline, membership, callback, or provider state is touched.

- [ ] **Step 4: Add bounded concurrent-load model tests**

Run fixed, bounded rounds combining structural replacement, reparenting, strict attach/detach, repeated references, shared DAGs, and cycles. Start workers together with barriers and settle them with task completion, not time. After each round check:

```text
committed backing values match the model
one domain per owned subject
one acyclic active route unless explicit wins
parent memberships match committed baselines
reference counts match distinct parent properties
registry and parent projections agree
no pending transition remains
unanchored components have no ownership projection
```

- [ ] **Step 5: Add callback-contract violation tests**

Add a handler/event that throws after the backing field and ownership ledger commit. Assert the same exception propagates, the property is not rolled back, Core route finalization ran, no pending token remains, and a later valid write reconciles from the committed baseline. Add built-in operational-failure tests proving first-party handlers catch/log expected failures and do not violate the no-throw contract.

- [ ] **Step 6: Add weak-reference release tests**

Cover an implicitly detached child, an explicitly retained then detached root, a released cycle, a multi-parent final detach, and failed/superseded reservations. Isolate construction in `[MethodImpl(MethodImplOptions.NoInlining)]` helpers, clear test locals, force collection through the repository helper, and assert each `WeakReference` dies only after its documented ownership anchor is released.

- [ ] **Step 7: Run the new schedules and capture RED or the exact baseline**

Run all newly added hardening tests before changing production code:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectOwnershipNestingTests" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipConcurrencyTests|FullyQualifiedName~OwnershipLifetimeTests|FullyQualifiedName~OwnershipCallbackFailureTests" --no-restore
```

Record every failing test and its invariant in the task report. If a new test is already green, record the existing production path that satisfies it; do not force an artificial failure.

- [ ] **Step 8: Harden stale-operation and pooled-state cleanup**

Fix only failures reproduced by the deterministic tests. Keep exact generation/token/descriptor checks at every cancel, publish, transfer, and finalize. Clear every pooled list/set entry, `ProviderState`, transition action, coordinator reference, batch item, and thread-static scope in `finally`.

- [ ] **Step 9: Run focused concurrency and order suites**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~SubjectOwnershipNestingTests|FullyQualifiedName~StructuralOwnershipConcurrencyTests|FullyQualifiedName~ContextConcurrencyTests" --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --filter "FullyQualifiedName~OwnershipConcurrencyTests|FullyQualifiedName~OwnershipLifetimeTests|FullyQualifiedName~OwnershipCallbackFailureTests|FullyQualifiedName~ConcurrentWriteLifecycleTests|FullyQualifiedName~LifecycleEventsTests" --no-restore
```

- [ ] **Step 10: Run complete Core and Tracking suites**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
```

- [ ] **Step 11: Lock/lifetime audit and commit**

Trace every lock acquisition in the diff. Confirm context mutation lock precedes authority publication; ownership reservation uses publication then one `SyncRoot`; route publication takes executor mutation lock then publication without `SyncRoot`; callbacks hold only the reentrant domain gate; no publication holder waits for context, domain, route-free writer, or second subject lock. Run `git diff --check`, then commit:

```bash
git add src/Namotion.Interceptor src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "Harden subject ownership concurrency"
```

### Task 5: First-Party Ownership Migration

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Services/RootManager.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/ConfigurableSubjectSerializer.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectFactory.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Storage/Internal/FileSubjectFactory.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services.Tests/Serialization/ConfigurableSubjectSerializerTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/SubjectFactoryTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Services.Tests/RootManagerTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.Storage.Tests/Internal/FileSubjectFactoryTests.cs`
- Modify: `src/Namotion.Interceptor.SamplesModel/Root.cs`
- Modify: `src/Namotion.Interceptor.ConnectorTester/Model/TestNode.cs`
- Modify: `src/Namotion.Interceptor.Mqtt.SampleClient/Program.cs`
- Modify: `src/Namotion.Interceptor.Mqtt.SampleServer/Program.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.SampleClient/Program.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.SampleServer/Program.cs`
- Modify: `src/Namotion.Interceptor.SampleBlazor/Program.cs`
- Modify: `src/Namotion.Interceptor.SampleConsole/Program.cs`
- Modify: `src/Namotion.Interceptor.SampleMachine/Program.cs`
- Modify: `src/Namotion.Interceptor.SampleWeb/Program.cs`
- Modify: `src/Namotion.Interceptor.WebSocket.SampleClient/Program.cs`
- Modify: `src/Namotion.Interceptor.WebSocket.SampleServer/Program.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectHierarchyBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/RegistryBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SubjectUpdateBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/SourcePathProviderBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/PropertyChangeSubscriptionsBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/DynamicSubjectBenchmark.cs`
- Modify: `src/Namotion.Interceptor.Benchmark/ContextDelegationDepthBenchmark.cs` so it continues measuring explicit fallback-composition depth rather than relying on a context-constructed subject's new explicit ownership route.
- Inventory checkpoint: Step 1 appends every additional concrete consumer/test path to `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-5-constructor-inventory.md` and amends this file list with those exact paths before Step 2 starts.

**Interfaces:**
- Consumes: strict explicit APIs and completed inherited ownership behavior.
- Produces: context-taking construction always means explicit ownership, parameterless construction means route-free child, and every first-party root has a visible ownership boundary.

- [ ] **Step 1: Generate the constructor/fallback migration inventory**

Run and save the complete results in the task report:

```bash
rg -n -i "new[[:space:]].*context|AddFallbackContext|RemoveFallbackContext|ActivatorUtilities\.CreateInstance|Activator\.CreateInstance" src --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'
rg -n -i -U "new[[:space:]][A-Za-z_][A-Za-z0-9_<>., ]*\([^;]{0,500}context[^;]{0,500}\)" src --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'
rg -n "IInterceptorSubjectContext[?]?[[:space:]]+[A-Za-z_][A-Za-z0-9_]*" src --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'
```

The first two searches deliberately match names such as `serverContext`, `clientContext`, `configuredContext`, and `_context`, including multiline construction. The third inventories context-typed parameters and fields. For every subject type exposing a context constructor, search that concrete type name across `src` and classify every object creation, including creations whose argument is an alias that does not contain the word `context`.

This is a bounded implementation-discovery checkpoint. Save every hit and classification in `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-5-constructor-inventory.md`, amend the Task 5 file list with every concrete production and test path, and obtain a scoped review that no constructor-bearing subject type or alias call site is absent. Do not continue to Step 2 while the file list still relies on a glob or prose-only “matching fixture” category.

Classify every hit as one of:

```text
explicit application root             -> keep context constructor; add exact detach at owned lifetime end
property child                         -> use route-free constructor/factory; parent property owns it
intentional service composition        -> keep AddFallbackContext/RemoveFallbackContext
legacy lifecycle shorthand             -> replace with explicit root or parent publication
test-only topology construction        -> state its intended category in the test name/comment
```

Do not bulk-replace constructors without this classification.

- [ ] **Step 2: Add RED first-party lifetime and factory tests**

Cover:

- `WhenRootManagerLoadsRoot_ThenItAttachesExplicitlyAndDetachesAtReplacementOrShutdown`;
- `WhenSerializerCreatesPropertyChildAndContextIsInServices_ThenChildRemainsRouteFree`;
- `WhenHomeBlazeSubjectFactoryResolvesOtherDependencies_ThenItDoesNotSelectContextConstructor`;
- `WhenFileSubjectFactoryCreatesStoredChild_ThenParentPublicationOwnsIt`;
- `WhenSampleModelCreatesPersons_ThenOnlyRootHasExplicitAttachment`;
- the already-green Task 2 headline contract remains unchanged while these consumer paths migrate.

Representative route-free factory assertion:

```csharp
[Fact]
public void WhenSerializerCreatesPropertyChildAndContextIsInServices_ThenChildRemainsRouteFree()
{
    // Arrange
    var context = InterceptorSubjectContext.Create().WithLifecycle();
    var serializer = CreateSerializerWithContext(context);

    // Act
    var child = serializer.Deserialize<Person>(serializedPerson);

    // Assert
    Assert.False(child.TryGetAttachContext(out _));
    Assert.Equal(0, child.GetReferenceCount());
}
```

- [ ] **Step 3: Run the first-party tests and capture RED**

Run:

```bash
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj --filter "FullyQualifiedName~RootManagerTests|FullyQualifiedName~ConfigurableSubjectSerializerTests|FullyQualifiedName~SubjectFactoryTests" --no-restore
dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj --filter "FullyQualifiedName~FileSubjectFactory" --no-restore
```

- [ ] **Step 4: Migrate property-child construction**

For every property child in the inventory, replace `new Child(context)` with `new Child()` and publish it through the parent property. In `Root.CreateWithPersons`, keep `new Root(context)` but construct each `Person` route-free before assigning `root.Persons`. Apply the same distinction to benchmarks and Connector Tester models so normal graph rows measure inherited children rather than thousands of explicit roots. In `ContextDelegationDepthBenchmark`, construct its context chain directly with fallbacks and keep the measured subject route-free so the row retains its original composition-depth meaning.

- [ ] **Step 5: Migrate explicit root lifetime boundaries**

Keep context constructors for application/server/client roots. Where a component owns and later replaces/disposes a root, call `DetachFromContext` with the same context before dropping the final owner. In `RootManager`, replace `Root.Context.AddFallbackContext(_context)` with `Root.AttachToContext(_context)` and detach on replacement/shutdown only at the existing ownership boundary. Do not infer detach from CLR reachability.

In `ConfigurableSubjectSerializer`, HomeBlaze `SubjectFactory`, and `FileSubjectFactory`, prevent `ActivatorUtilities.CreateInstance` from silently selecting the generated context constructor for values that are still property children or a route-free deserialized root. Use the same explicit constructor-selection rules specified for `DefaultSubjectFactory` in Task 6, implemented within each existing dependency boundary rather than adding a reverse package reference. Tests must prove non-context DI dependencies still resolve while an available `IInterceptorSubjectContext` is ignored for route-free creation.

- [ ] **Step 6: Run consumer and benchmark-build suites**

Run:

```bash
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj --no-restore
dotnet test src/HomeBlaze/HomeBlaze.Storage.Tests/HomeBlaze.Storage.Tests.csproj --no-restore
dotnet build src/Namotion.Interceptor.Benchmark/Namotion.Interceptor.Benchmark.csproj --no-restore
dotnet build src/Namotion.Interceptor.SamplesModel/Namotion.Interceptor.SamplesModel.csproj --no-restore
dotnet build src/Namotion.Interceptor.slnx --no-restore
```

The solution build must compile every sample and Connector Tester project touched by the reviewed inventory before this task commits. If the local zero-diagnostic solution-build anomaly recurs, do not treat it as proof: build every amended Task 5 project path individually and record each successful result before continuing.

- [ ] **Step 7: Re-run the inventory, review ownership intent, and commit**

Every remaining context constructor must be an explicit root by design; every remaining fallback must be composition-only. Run `git diff --check`, then commit:

```bash
git add src/Namotion.Interceptor.Generator src/Namotion.Interceptor.Generator.Tests src/Namotion.Interceptor.Dynamic src/Namotion.Interceptor.Dynamic.Tests src/Namotion.Interceptor.SamplesModel src/Namotion.Interceptor.Benchmark src/Namotion.Interceptor.ConnectorTester src/HomeBlaze src/Namotion.Interceptor.*.Sample* src/Namotion.Interceptor.Sample*
git commit -m "Migrate explicit and inherited subject construction"
```

### Task 6: Route-Free Factories and Connector Publication Order

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectFactory.cs`
- Modify: `src/Namotion.Interceptor.Connectors/DefaultSubjectFactory.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectFactoryExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs`
- Modify: `src/Namotion.Interceptor.Connectors/Paths/PathExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/DefaultSubjectFactoryTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateExtensionsTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCollectionTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateDictionaryTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateCycleTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Updates/SubjectUpdateReadOnlyTypesTests.cs`
- Create: `src/Namotion.Interceptor.Connectors.Tests/RouteFreeSubjectFactoryContractTests.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/OpcUaSubjectFactory.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectFactoryTests.cs`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaSubjectLoaderTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt` only if XML/API output changes.

**Interfaces:**
- Consumes: completed strict and inherited ownership plus constructor semantics.
- Produces: route-free child-factory contract, parent-first publication, post-publication identity verification, and recursive population only after ownership commits.

- [ ] **Implementation-map gate: freeze factory selection and publication entry points**

Before adding RED tests, write `.superpowers/sdd/2026-08-19-explicit-subject-ownership/task-6-connector-map.md` with exact signatures and file paths for constructor-candidate caching, per-provider winner selection, prepublication ownership diagnostics, object publication, collection/dictionary publication, committed-identity re-read, path creation, OPC UA DAG cache insertion, and recursive population. Include one concrete test skeleton for each path plus the two-provider and ambiguous-constructor schedules.

Obtain an independent scoped review and amend this plan with the exact map before Step 1. If any connector cannot publish through the normal intercepted parent setter before recursion without changing its public protocol, stop and amend/re-review the design and plan; do not restore fallback-as-attach or add a connector-only ownership bridge.

- [ ] **Step 1: Add RED factory contract tests**

Cover:

- `WhenServiceProviderContainsContext_ThenDefaultFactoryStillCreatesRouteFreeChild`
- `WhenFactoryUsesDependencyInjection_ThenNonContextDependenciesAreResolved`
- `WhenCustomFactoryReturnsExplicitlyAttachedSubject_ThenCallerRejectsBeforeParentPublication`
- `WhenCustomFactoryReturnsInheritedSubject_ThenCallerRejectsBeforeRecursivePopulation`
- `WhenFactoryResultIsPublished_ThenRecursivePopulationObservesParentOwnership`
- `WhenFactoryResultRacesCompatibleOwnership_ThenParentTerminalAppliesGeneralCompatibilityRules`
- `WhenTwoProvidersResolveDifferentDependencies_ThenTypeCacheDoesNotReuseProviderDecision`
- `WhenSeveralNonContextConstructorsAreResolvable_ThenFactoryRejectsAmbiguity`

The stable prepublication diagnostic is:

```csharp
if (subject.TryGetAttachContext(out _) ||
    ((IInterceptorExecutor)subject.Context).OwnershipReferenceCount != 0)
{
    throw new InvalidOperationException(
        "A property-child factory must return a route-free, unowned subject.");
}
```

This is diagnostic only. The parent terminal remains the atomic authority.

- [ ] **Step 2: Run Connectors tests and capture RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --filter "FullyQualifiedName~RouteFreeSubjectFactoryContractTests|FullyQualifiedName~DefaultSubjectFactoryTests" --no-restore
```

Expected: the DI path can select a context-taking constructor and update appliers still populate before parent publication.

- [ ] **Step 3: Make default child construction exclude context constructors**

Cache the ordered non-context constructor candidates and compiled invocation factories per subject type, not the provider-dependent winner. Prefer the parameterless constructor when present. Otherwise, on each call, evaluate which cached candidates have all remaining parameters available from that call's `IServiceProvider`; select exactly one and reject zero or several with an actionable `InvalidOperationException`. Resolve those non-context parameters and invoke the selected cached factory. Never let the first provider poison later selection, and never hide the context service through a wrapper and let `ActivatorUtilities` rediscover the context constructor.

Update `ISubjectFactory` XML to promise a route-free, unowned property child and explain that independent roots use their own explicit construction path.

- [ ] **Step 4: Publish object children before population**

In `SubjectUpdateApplier.ApplyObjectUpdate`:

```text
create and validate route-free child
set parent property through intercepted source write
re-read property/registry child and require exact ReferenceEquals result
only then recursively apply child properties
```

If publication is suppressed, transformed, or rejected, do not populate the detached factory result.

- [ ] **Step 5: Publish collection/dictionary children before population**

Create and validate all missing route-free items, form the final collection/dictionary, set the parent property once, re-read committed children by stable index/key and exact identity, then recursively apply properties only to committed children. Preserve existing DAG identity reuse and source-origin timestamps. Do not add a fallback before publication.

- [ ] **Step 6: Migrate path and OPC UA loaders**

For path-created object children, validate, set the parent property, verify exact committed identity, then return it. For OPC UA single references, collections, and dictionaries, publish through the parent before `LoadSubjectAsync` recursion. Keep `subjectsByNodeId` DAG reuse, but only cache a newly created subject after successful parent publication and exact identity verification. A custom attached result fails without implicit detach or ownership stealing.

- [ ] **Step 7: Run connector and OPC UA suites**

Run:

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Mqtt.Tests/Namotion.Interceptor.Mqtt.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.WebSocket.Tests/Namotion.Interceptor.WebSocket.Tests.csproj --no-restore
```

These are intentional targeted connector integration gates because this task changes connector publication order. Run Public API tests for Connectors and OPC UA and accept only intended XML/API changes. If a required external endpoint or credential is unavailable, record the exact skipped/blocked test and include it in the already agreed stable-machine Connector Tester handoff; do not silently replace an integration gate with `Category!=Integration`.

- [ ] **Step 8: Audit publication order and commit**

Run:

```bash
rg -n "CreateSubject|CreateCollectionSubject|AddFallbackContext" src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.OpcUa --glob '*.cs'
git diff --check
```

Every child path must show create, route-free diagnostic, parent publication, exact committed verification, then recursion. Commit:

```bash
git add src/Namotion.Interceptor.Connectors src/Namotion.Interceptor.Connectors.Tests src/Namotion.Interceptor.OpcUa src/Namotion.Interceptor.OpcUa.Tests
git commit -m "Publish route-free connector children through parents"
```

### Task 7: Canonical Documentation, Simplification Audit, and Release Gates

**Files:**
- Modify: `README.md`
- Modify: `docs/interceptor.md`
- Modify: `docs/generator.md`
- Modify: `docs/dynamic.md`
- Modify: `docs/subject-guidelines.md`
- Modify: `docs/tracking.md`
- Modify: `docs/registry.md`
- Modify: `docs/hosting.md`
- Modify: `docs/connectors.md`
- Modify: `docs/connectors-subject-updates.md`
- Modify: `docs/connectors-opcua-client.md`
- Modify: `docs/connectors-websocket.md`
- Modify: `docs/design/generator-supported-shapes.md`
- Modify: `docs/design/tracking-lifecycle.md`
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Registry.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Hosting.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.OpcUa.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.Mqtt.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Modify: `src/Namotion.Interceptor.WebSocket.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Temporary external artifact only: `/private/tmp/namotion-pr419-benchmark-harness.patch`
- Temporary external handoff only: `/private/tmp/namotion-pr419-stable-machine-handoff.md`

**Interfaces:**
- Consumes: all completed PR #419 behavior.
- Produces: one canonical user model, migration guidance, a legacy-removal proof, exact local verification, and a stable-machine benchmark/Connector Tester handoff. It does not run or accept authoritative performance numbers locally.
- Every package XML comment must already have been updated in the exact task that changes its API. A new Public API received diff in this task is routed back to that owning task and re-reviewed; Task 7 does not first-touch an unlisted production source file.

- [ ] **Step 1: Rewrite the canonical ownership explanation**

Make `docs/interceptor.md` the single source for:

```text
subject executor versus plain configured context
explicit root attachment and exact detach
parent membership and distinct-property reference count
ownership domain and active parent transfer
local services, composition fallbacks, and one ownership route
registry as projection rather than ownership source
strict errors and unsupported nesting
context-constructor versus parameterless-child migration
```

Lead with the headline example from the spec: `new Child(context)` remains explicitly owned after parent removal, while `new Child()` releases with its final parent.

- [ ] **Step 2: Update feature docs without duplicating the model**

For each affected document, add a short introduction, local terms, and contract-at-a-glance section. Link to `docs/interceptor.md` for the complete model. Rewrite `docs/design/tracking-lifecycle.md` around Core ownership, prospective reservation, route forest, reconciliation, callback phase, concurrency, and release. Remove every claim that fallback mutation or `ContextInheritanceHandler` performs lifecycle work.

- [ ] **Step 3: Run the full legacy-coupling and capability audit**

Run:

```bash
rg -n "WithContextInheritance|ContextInheritanceHandler|PropertyReferenceSet|_attachedSubjects|IncrementReferenceCount|DecrementReferenceCount" src docs --glob '*.cs' --glob '*.md'
rg -n "AddFallbackContext|RemoveFallbackContext" src docs --glob '*.cs' --glob '*.md'
rg -n -i "new[[:space:]].*context|ActivatorUtilities\.CreateInstance|Activator\.CreateInstance" src --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'
rg -n -i -U "new[[:space:]][A-Za-z_][A-Za-z0-9_<>., ]*\([^;]{0,500}context[^;]{0,500}\)" src --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'
rg -n "IInterceptorSubjectContext[?]?[[:space:]]+[A-Za-z_][A-Za-z0-9_]*" src --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'
```

The first command must return no functional legacy implementation. Reconcile all three constructor inventories against Task 5's reviewed concrete-path report, classify every fallback as service composition and every remaining context constructor as an explicit root, and fail the audit on any new or missing hit. Delete duplicate production blocks rather than leaving compatibility paths for later stack work to remove.

- [ ] **Step 4: Run static performance and allocation analysis**

Record evidence in the task report for:

- executor field delta and route-free ownership-state nullability;
- inactive context-state shape;
- scalar generated setter and Core terminal diff against base;
- stable read, invoke, and cached service entry diff against base;
- monitor order and count on ordinary structural writes;
- pooled traversal and reentrant overflow cleanup;
- absence of closures, tasks, waiter nodes, or boxed reference counts on warmed-up paths;
- invalidation only when routes or legal context topology actually change.

If the diff visibly adds ownership work to a known scalar path or a third monitor to the normal structural path, stop and reopen the design before running broad gates.

- [ ] **Step 5: Run focused consumer matrix**

Run sequentially:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Generator.Tests/Namotion.Interceptor.Generator.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Dynamic.Tests/Namotion.Interceptor.Dynamic.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Tracking.Tests/Namotion.Interceptor.Tracking.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Registry.Tests/Namotion.Interceptor.Registry.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Hosting.Tests/Namotion.Interceptor.Hosting.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.Connectors.Tests/Namotion.Interceptor.Connectors.Tests.csproj --no-restore
dotnet test src/Namotion.Interceptor.OpcUa.Tests/Namotion.Interceptor.OpcUa.Tests.csproj --filter "Category!=Integration" --no-restore
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj --no-restore
```

- [ ] **Step 6: Run every Public API gate and reject stray snapshots**

Run the `VerifyChecksTests.PublicApi` filter in Core, Tracking, Registry, Hosting, Connectors, OPC UA, MQTT, and WebSocket test projects. Inspect each received file. The intended surface is the Core provider/ownership/classifier API, removal of Tracking optional inheritance, and documented factory XML. No hosting state-machine API belongs in this PR.

- [ ] **Step 7: Run repository release gates**

Run one command at a time with no concurrent dotnet worker:

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
dotnet pack src/Namotion.Interceptor.slnx
```

Record exact exit code, elapsed time, warnings, and errors. If the local five-minute zero-diagnostic SDK anomaly recurs, do not call the gate successful. Record it as inconclusive, verify affected project builds/packs separately, and include the exact solution commands in the stable-machine handoff.

- [ ] **Step 8: Commit permanent documentation and accepted snapshots**

Inspect the staged scope and commit only permanent feature documentation, XML comments, and accepted API snapshots. The temporary roadmap/spec/plan remains available until the next controller step.

```bash
git diff --check
git add README.md docs src
git commit -m "Document explicit subject ownership"
```

- [ ] **Step 9: Remove temporary design artifacts and freeze final stacked hashes**

Before final review, remove the tracked `docs/superpowers/` artifacts introduced by PR #474 from `feature/effective-ownership-route`, rebase this branch onto that cleaned exact head, then remove this PR's roadmap, spec, plan, and task reports. Preserve permanent documentation under `docs/design/` and normal feature docs. This is a controller/maintainer operation, not an implementation-subagent cleanup shortcut.

Verify both diffs independently and record the new exact PR #474 head, PR #419 head, and master comparison commit with `git rev-parse`:

```bash
git diff --name-only master...feature/effective-ownership-route | rg '^docs/superpowers/'
git diff --name-only feature/effective-ownership-route...HEAD | rg '^docs/superpowers/'
git rev-parse feature/effective-ownership-route
git rev-parse HEAD
git rev-parse master
git diff --check feature/effective-ownership-route...HEAD
find . -type f \( -name '*.received.txt' -o -name '*.received.*' \) -print
git status --short --branch
```

Expected: neither PR diff contains `docs/superpowers/`, no received snapshot remains, and the worktree is clean. Re-run the focused ownership, callback-order, Public API, solution build/test/pack, and documentation hygiene gates after the rebase. All following review and external commands use only the newly recorded exact hashes.

- [ ] **Step 10: Receive independent whole-PR and combined-stack review**

Generate one review package for the newly recorded exact PR #474 head through the exact PR #419 head. Ask one fresh reviewer to inspect correctness, concurrency, callback order, public API, consumer migration, lifetime, and performance shape. Ask a second fresh reviewer to inspect the combined exact master through PR #419 production diff for simplification: every new block must enforce a named invariant, and no legacy fallback/lifecycle coupling or temporary multi-domain bridge may survive.

Fix Critical and Important findings test-first. Re-run affected local gates, repeat Step 9's hash recording, and obtain a clean re-review after each fix round. No code or permanent documentation change may occur after the final clean review without invalidating the affected review and external evidence.

- [ ] **Step 11: Prepare, but do not run, the external benchmark harness**

Read `docs/benchmarking.md`. Create one temporary benchmark-only patch that adds unowned structural initialization and contended structural-write rows. Verify the same patch applies to three clean checkouts at the exact hashes recorded in Step 9: final PR #419 head, final cleaned PR #474 base, and final master comparison commit.

The handoff must also name unchanged Registry, context-depth, construction, uncontended structural write, graph attach/detach, and concurrent structural-stress filters. Require no repeatable timing regression outside control-row noise and no new steady-state allocation. Do not run or interpret authoritative timing on this development machine.

- [ ] **Step 12: Ask for the stable-machine and Connector Tester handoff**

Stop and ask the maintainer before external execution. The requested stable-machine work is:

```text
solution build and pack at the exact final hashes if locally inconclusive
benchmark comparison at exact final PR #419, cleaned PR #474, and master hashes
temporary structural benchmark patch applied identically to all three checkouts
Connector Tester: 100 cycles per connector
Connector Tester: every rotating chaos profile
Connector Tester: server plus both-client structural mutation at rate 1
```

Any repeatable normal one-global-context regression reopens the design. Do not finalize the PR on development-machine numbers.

- [ ] **Step 13: Receive and adjudicate the external results**

Record the exact machine, hashes, commands, BenchmarkDotNet artifacts, build/pack results, and Connector Tester reports returned by the maintainer. Apply these acceptance rules:

```text
solution build or pack emits a diagnostic failure -> fix and rerun the affected local and external gate
repeatable normal one-global-context timing regression -> reopen the design before accepting implementation cost
new steady-state allocation in an accepted zero-allocation row -> reopen the design
Connector Tester correctness, quiescence, or leak failure -> reproduce deterministically where possible, fix test-first, and rerun the affected profile
all comparisons within control-row noise and no allocation/correctness regression -> record PASS with artifact paths
```

Any code, permanent-doc, or stack-rebase change made while addressing a failure requires a new Step 9 hash record, affected review, and affected external run. Do not describe PR #419 as ready until this step has a PASS against the exact final heads.

- [ ] **Step 14: Push and verify the final stacked pull requests**

Update PR #474 and PR #419 titles and descriptions to the final capabilities, removals, migration guidance, verification results, exact head/base hashes, and benchmark/Connector Tester artifacts. Push each rewritten branch with an exact force-with-lease, keep #419 based on `feature/effective-ownership-route`, and verify remotely:

```text
PR #474 remote head equals the cleaned local PR #474 hash
PR #419 remote base is feature/effective-ownership-route
PR #419 remote head equals the locally reviewed and benchmarked hash
both PR bodies report the exact verified hashes and no pending gate as passed
both PR diffs contain no docs/superpowers artifact
```

Do not alter either branch after remote verification without rerunning the affected gates.

## Plan Self-Review Checklist

- [ ] Every capability removal and preserved capability in the approved spec maps to a task and named test.
- [ ] Every public signature in the approved spec appears exactly once in Tasks 1 or 2 and is consumed later without renaming.
- [ ] Every task compiles at its commit boundary; Task 2 has no intermediate phase commit or handoff, and its public interface, Core callers, generated/Dynamic constructors, test doubles, and Tracking implementation change in one atomic commit.
- [ ] No task introduces a compatibility bridge or public factory API that a later task deletes.
- [ ] The scalar fast path, route-free allocation contract, lock order, callback order, and stable-machine acceptance rule are explicit gates.
- [ ] The constructor, fallback, helper, connector, OPC UA, sample, benchmark, and HomeBlaze audits have no unclassified hit.
- [ ] The placeholder scan is empty and every failure path names its exception, cleanup, and assertion.
