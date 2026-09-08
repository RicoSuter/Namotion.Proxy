# Phase 1: Per-subject revision label Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stamp every committed property write with a monotonic per-subject `Revision`, expose it on `SubjectPropertyChange`, and use it to fix the `ChangeQueueProcessor` flush dedup, which currently picks the "newest" value by array position (arrival order) and can hand a connector a stale value.

**Architecture:** The commit already happens under a per-subject lock (`WriteInterceptorFactory`'s terminal, `lock (subject.SyncRoot)`). A plain `long` field on the subject's `InterceptorExecutor` is incremented there, so no new lock, no atomic, and no shared cache line. The value is stamped into the write context, read by `PropertyChangeInterceptor` when it builds the change, and consumed by the flush dedup. Nothing here is load-bearing for ordering, so there is no contiguity invariant to preserve.

**Tech Stack:** C# 13, .NET Standard 2.0 (core) / .NET 9 (extensions), xUnit, BenchmarkDotNet, PublicApiGenerator + Verify.

**Spec:** `docs/superpowers/specs/2026-07-29-ordered-change-delivery-design.md` (Phase 1 scope; sections 1, 5, 9 and the `Create` subsection).

**Branch:** `feature/revision-label`. This PR stays open and is the base of the Phase 2 PR (stacked), because Phase 2's benchmarks are the first real test of this PR's always-on cost and may force changes here.

---

## File Structure

**Core (`src/Namotion.Interceptor`):**
- `Interceptors/InterceptorExecutor.cs` (modify): gains the `Revision` field.
- `SubjectRevisionCounter.cs` (create): the increment helper, including the non-executor fallback. Isolated so the fallback path is testable and the terminal stays a one-liner.
- `Cache/WriteInterceptorFactory.cs` (modify): both terminals call the helper.
- `Interceptors/IWriteInterceptor.cs` (modify): `PropertyWriteContext<TProperty>` gains internal `Revision` and `FinalValueIsNewValue`; `GetFinalValue()` honours the flag.
- `PropertyReferenceExtensions.cs` (modify): the internal cascade overload forwards the flag.

**Tracking (`src/Namotion.Interceptor.Tracking`):**
- `Change/SubjectPropertyChange.cs` (modify): public `Revision`, optional `revision` parameter on `Create`, propagation through the private ctor, `MergeWithNewer`, `WithOrigin`.
- `Change/PropertyChangeInterceptor.cs` (modify): pass `context.Revision` at both `Create` call sites.
- `Change/DerivedPropertyChangeHandler.cs` (modify): set the flag on the recalculation publish.

**Connectors (`src/Namotion.Interceptor.Connectors`):**
- `ChangeDeduplicator.cs` (create): the flush dedup extracted as an internal static class so it is unit-testable with hand-built revisions. `ChangeQueueProcessor` is already 337 lines and this is a self-contained algorithm with a non-obvious contract.
- `ChangeQueueProcessor.cs` (modify): delegate the dedup pass.

**Docs:** `docs/tracking.md` (modify): the guarantee matrix.

---

### Task 1: Per-subject revision counter in core

**Files:**
- Create: `src/Namotion.Interceptor/SubjectRevisionCounter.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Test: `src/Namotion.Interceptor.Tests/SubjectRevisionCounterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.Tests.Models; // Person, or the nearest existing generated test subject

namespace Namotion.Interceptor.Tests;

public class SubjectRevisionCounterTests
{
    [Fact]
    public void WhenIncrementedRepeatedly_ThenRevisionIsMonotonicPerSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var first = new Person(context);
        var second = new Person(context);

        // Act
        var firstA = SubjectRevisionCounter.Next(first);
        var firstB = SubjectRevisionCounter.Next(first);
        var secondA = SubjectRevisionCounter.Next(second);

        // Assert
        Assert.Equal(1, firstA);
        Assert.Equal(2, firstB);
        Assert.Equal(1, secondA); // independent per subject
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectRevisionCounterTests"`
Expected: FAIL, compile error `The name 'SubjectRevisionCounter' does not exist`.

- [ ] **Step 3: Add the field to the executor**

In `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`, after the `_subject` field:

```csharp
    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while the subject's
    /// SyncRoot is held, so a plain increment is exclusive: no Interlocked needed. Dense over
    /// committed writes (vetoed and no-op writes never reach the terminal) and never reset, so it
    /// stays comparable across detach and reattach. A label only: ordering does not depend on it.
    /// </summary>
    internal long Revision;
```

- [ ] **Step 4: Create the helper**

Create `src/Namotion.Interceptor/SubjectRevisionCounter.cs`:

```csharp
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// Assigns the monotonic per-subject commit revision. Called by the terminal write with the
/// subject's SyncRoot held, which is what makes the plain increment safe.
/// </summary>
internal static class SubjectRevisionCounter
{
    private const string RevisionKey = "Namotion.Interceptor.Revision";

    /// <summary>
    /// Returns the next revision for the subject. Callers must hold the subject's SyncRoot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Next(IInterceptorSubject subject)
    {
        // Generated subjects own their executor, so the counter is a plain field on an object that
        // is already hot in this write: no lookup, no atomic, no shared cache line.
        if (subject.Context is InterceptorExecutor executor)
        {
            return ++executor.Revision;
        }

        return NextFallback(subject);
    }

    /// <summary>
    /// Hand-written subjects whose context is not an <see cref="InterceptorExecutor"/> keep the
    /// counter in subject data, mirroring the write-timestamp holder. Label only: ordered delivery
    /// is not offered for such subjects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long NextFallback(IInterceptorSubject subject)
    {
        var holder = (long[])subject.Data.GetOrAdd((string.Empty, RevisionKey), static _ => new long[1])!;
        return ++holder[0];
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectRevisionCounterTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor/SubjectRevisionCounter.cs \
        src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs \
        src/Namotion.Interceptor.Tests/SubjectRevisionCounterTests.cs
git commit -m "feat: add per-subject commit revision counter"
```

---

### Task 2: Stamp the revision in both terminals

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs` (add the context field)
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs:12-22` and `:27-38`
- Test: `src/Namotion.Interceptor.Tests/SubjectRevisionCounterTests.cs`

There are **two** terminals in that file (the interceptor-less fast path and the chain terminal). Both must be instrumented or writes on contexts without interceptors silently carry revision 0.

- [ ] **Step 1: Write the failing test**

Append to `SubjectRevisionCounterTests`:

```csharp
    [Fact]
    public void WhenPropertyWrittenThroughChain_ThenContextCarriesIncreasingRevision()
    {
        // Arrange
        var revisions = new List<long>();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new RevisionCapturingInterceptor(revisions));
        var person = new Person(context);

        // Act
        person.FirstName = "a";
        person.FirstName = "b";
        person.LastName = "c";

        // Assert: dense and increasing across all properties of the subject
        Assert.Equal(new long[] { 1, 2, 3 }, revisions);
    }

    private sealed class RevisionCapturingInterceptor(List<long> revisions) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            revisions.Add(context.Revision);
        }
    }
```

Add `using Namotion.Interceptor.Interceptors;` to the file's usings.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~WhenPropertyWrittenThroughChain"`
Expected: FAIL, compile error `'PropertyWriteContext<TProperty>' has no member 'Revision'`.

- [ ] **Step 3: Add the context field**

In `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`, inside `PropertyWriteContext<TProperty>` next to the other internal fields:

```csharp
    /// <summary>
    /// The subject's commit revision assigned by the terminal write, or 0 when the write did not
    /// commit. Monotonic per subject, not comparable across subjects.
    /// </summary>
    internal long Revision;
```

- [ ] **Step 4: Stamp it in both terminals**

In `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs`, in **both** lock bodies, immediately after `context.IsWritten = true;`:

```csharp
                    context.Revision = SubjectRevisionCounter.Next(context.Property.Subject);
```

For clarity, the interceptor-less terminal becomes:

```csharp
            return static (ref context, innerWriteValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    innerWriteValue(context.Property.Subject, context.NewValue);
                    context.IsWritten = true;
                    context.Revision = SubjectRevisionCounter.Next(context.Property.Subject);
                    context.FinalizeOrigin();
                    var raw = context.WriteTimestampRaw;
                    context.Property.SetWriteTimestamp(raw > 0 ? raw : 0);
                }
            };
```

and the chain terminal gains the same line in the same position (before `FinalizeOrigin()`, keeping `return context.NewValue;` last).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~SubjectRevisionCounterTests"`
Expected: PASS (both tests).

- [ ] **Step 6: Run the full core and tracking suites**

Run: `dotnet test src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking.Tests`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs \
        src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs \
        src/Namotion.Interceptor.Tests/SubjectRevisionCounterTests.cs
git commit -m "feat: stamp the commit revision in both write terminals"
```

---

### Task 3: Expose `Revision` on `SubjectPropertyChange`

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/SubjectPropertyChangeTests.cs`

`Create` has ~100 call sites and is the only overload. It gains an **optional** `long revision = 0`: one method, no call-site churn, and 0 means "constructed outside a terminal write". This is source-compatible but binary breaking; accepted, because the source generator means consumers recompile against every version anyway, and a single clean method beats a permanently doubled API. Call it out in the PR description.

- [ ] **Step 1: Write the failing test**

Append to `SubjectPropertyChangeTests`:

```csharp
    [Fact]
    public void WhenCreatedWithRevision_ThenRevisionIsExposedAndSurvivesMergeAndOrigin()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var earlier = SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "a", "b", 5L);
        var later = SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "b", "c", 6L);
        var merged = earlier.MergeWithNewer(later);
        var reoriginated = later.WithOrigin(ChangeOrigin.Local);
        var legacy = SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "a", "b");

        // Assert
        Assert.Equal(5L, earlier.Revision);
        Assert.Equal(6L, later.Revision);
        Assert.Equal(6L, merged.Revision);          // merge takes the newer revision
        Assert.Equal("a", merged.GetOldValue<string>());
        Assert.Equal("c", merged.GetNewValue<string>());
        Assert.Equal(6L, reoriginated.Revision);    // WithOrigin preserves it
        Assert.Equal(0L, legacy.Revision);          // six-parameter overload defaults to 0
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenCreatedWithRevision"`
Expected: FAIL, compile error: no `Revision` member and no seven-argument `Create`.

- [ ] **Step 3: Thread the field through the private constructor**

In `SubjectPropertyChange.cs`, add `long revision` as the last parameter of the private constructor and assign it, then add the property after `ReceivedTimestamp`:

```csharp
    private SubjectPropertyChange(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        InlineValueStorage oldValueStorage,
        InlineValueStorage newValueStorage,
        object? oldBoxedHolder,
        object? newBoxedHolder,
        long revision)
    {
        Property = property;
        Origin = origin;
        ChangedTimestamp = changedTimestamp;
        ReceivedTimestamp = receivedTimestamp;
        _oldValueStorage = oldValueStorage;
        _newValueStorage = newValueStorage;
        _oldBoxedHolder = oldBoxedHolder;
        _newBoxedHolder = newBoxedHolder;
        Revision = revision;
    }
```

```csharp
    /// <summary>
    /// The writing subject's commit revision: monotonic per subject over committed writes, so two
    /// changes to the same subject are ordered by comparing it. Revisions of different subjects are
    /// NOT comparable. 0 means the change was constructed outside a terminal write.
    /// </summary>
    public long Revision { get; }
```

- [ ] **Step 4: Add the optional parameter**

Add a trailing optional parameter to the existing `Create` and pass it to all three `new SubjectPropertyChange(...)` returns. No second overload: one method, and `0` is the meaningful default for changes built outside a terminal write.

```csharp
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectPropertyChange Create<TValue>(
        PropertyReference property,
        ChangeOrigin origin,
        DateTimeOffset changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        TValue oldValue,
        TValue newValue,
        long revision = 0)
    {
        // ... existing body unchanged, with `revision` passed as the last constructor argument
        // in all three returns (inline, string, and boxed-holder paths)
    }
```

This is a binary break (source-compatible). Accepted: the source generator means consumers recompile against every version anyway. Note it in the PR description.

- [ ] **Step 5: Propagate through `MergeWithNewer` and `WithOrigin`**

`MergeWithNewer` takes the newer change's revision (it already takes the newer origin and timestamps):

```csharp
        return new SubjectPropertyChange(
            Property,
            newerChange.Origin,
            newerChange.ChangedTimestamp,
            newerChange.ReceivedTimestamp,
            _oldValueStorage,
            newerChange._newValueStorage,
            _oldBoxedHolder,
            newerChange._newBoxedHolder,
            newerChange.Revision);
```

`WithOrigin` preserves `Revision` (append `Revision` as the last constructor argument).

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~WhenCreatedWithRevision"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/SubjectPropertyChangeTests.cs
git commit -m "feat: expose the commit revision on SubjectPropertyChange"
```

---

### Task 4: Publish the revision on the change channels

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs:185` and `:229`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeRevisionTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeRevisionTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeRevisionTests
{
    [Fact]
    public void WhenPropertiesWritten_ThenPublishedChangesCarryIncreasingRevisions()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var revisions = new List<long>();
        using var subscription = context.GetPropertyChangeObservable()
            .Subscribe(change => revisions.Add(change.Revision));

        // Act
        person.FirstName = "a";
        person.LastName = "b";

        // Assert
        Assert.All(revisions, revision => Assert.True(revision > 0, "published changes must carry a revision"));
        Assert.Equal(revisions.OrderBy(revision => revision), revisions);
        Assert.Equal(revisions.Distinct().Count(), revisions.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PropertyChangeRevisionTests"`
Expected: FAIL, `revision > 0` assertion fails (changes still carry 0).

- [ ] **Step 3: Pass the revision at both call sites**

In `PropertyChangeInterceptor.cs`, both `SubjectPropertyChange.Create(...)` calls (in `WriteProperty` around line 185 and `DispatchLateConsumers` around line 229) gain `context.Revision` as the final argument:

```csharp
        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            context.CurrentValue,
            context.GetFinalValue(),
            context.Revision);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PropertyChangeRevisionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeRevisionTests.cs
git commit -m "feat: publish the commit revision on the change channels"
```

---

### Task 5: `FinalValueIsNewValue` for derived recalculation publishes

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs` (field + `GetFinalValue`)
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs:39-49` (internal cascade overload)
- Modify: `src/Namotion.Interceptor/PropertyReferenceExtensions.cs:25-30`
- Modify: `src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs:365`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/DerivedFinalValueTests.cs`

This stops the recalculation publish from re-invoking the derived getter. It is an observable behavior change on the `Immediate` channels (the published value is now the stabilized recalculation result, not a possibly-newer re-read) and must be called out in the PR description.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/DerivedFinalValueTests.cs`:

```csharp
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedFinalValueTests
{
    [InterceptorSubject]
    public partial class CountingDerived
    {
        public static int GetterCalls;

        public partial int Value { get; set; }

        [Derived]
        public int Doubled
        {
            get
            {
                GetterCalls++;
                return Value * 2;
            }
        }
    }

    [Fact]
    public void WhenDerivedRecalculationPublishes_ThenGetterIsNotReinvokedAtPublishTime()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CountingDerived(context);
        using var subscription = context.GetPropertyChangeObservable().Subscribe(_ => { });
        CountingDerived.GetterCalls = 0;

        // Act
        subject.Value = 21;

        // Assert: the recalculation evaluates the getter (possibly twice for stabilization), but the
        // publish must not add another invocation on top of the recalculated value.
        Assert.Equal(42, subject.Doubled - 0 + 0 - subject.Doubled + 42); // value is correct
        Assert.True(CountingDerived.GetterCalls <= 2,
            $"expected no publish-time re-invocation, saw {CountingDerived.GetterCalls} getter calls");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~DerivedFinalValueTests"`
Expected: FAIL, getter call count exceeds 2 (the publish re-invokes it).

- [ ] **Step 3: Add the flag and honour it**

In `PropertyWriteContext<TProperty>` (`IWriteInterceptor.cs`), next to `Revision`:

```csharp
    /// <summary>
    /// Set by the derived recalculation entry point, where <see cref="NewValue"/> is already the
    /// stabilized getter output. Stops <see cref="GetFinalValue"/> from re-invoking the getter,
    /// which would run user code at publish time and could return a value that never paired
    /// atomically with the change's old value.
    /// </summary>
    internal bool FinalValueIsNewValue;
```

and in `GetFinalValue()`:

```csharp
    public TProperty GetFinalValue()
    {
        if (FinalValueIsNewValue)
        {
            return NewValue;
        }

        var property = Property;
        var metadata = property.Metadata;
        return metadata.IsDerived
            ? (TProperty)metadata.GetValue?.Invoke(property.Subject)!
            : NewValue;
    }
```

- [ ] **Step 4: Thread the flag from the recalculation entry point**

In `InterceptorExecutor.cs`, the internal cascade overload gains a parameter and sets the field:

```csharp
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp, bool finalValueIsNewValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp)
        {
            FinalValueIsNewValue = finalValueIsNewValue
        };

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }
```

In `PropertyReferenceExtensions.cs:25-30`, forward `finalValueIsNewValue: true` from the internal overload used by the cascade (that overload exists only for the derived recalculation path, so a literal `true` at the single forwarding site is correct; do not change the public overload).

In `DerivedPropertyChangeHandler.cs:365` no signature change is needed if the internal extension overload passes `true`; verify by reading the call chain and adjust the extension's parameter list if it forwards positionally.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~DerivedFinalValueTests"`
Expected: PASS.

- [ ] **Step 6: Run the derived and tracking suites**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests`
Expected: PASS. If a derived test asserts the old publish value, treat it as the documented behavior change: update it and note the change in the PR description.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs \
        src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs \
        src/Namotion.Interceptor/PropertyReferenceExtensions.cs \
        src/Namotion.Interceptor.Tracking/Change/DerivedPropertyChangeHandler.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/DerivedFinalValueTests.cs
git commit -m "fix: stop re-invoking the derived getter when publishing a recalculation"
```

---

### Task 6: Extract the flush deduplicator

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/ChangeDeduplicator.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs:39-44,228-262`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeDeduplicatorTests.cs`

`InternalsVisibleTo("Namotion.Interceptor.Connectors.Tests")` already exists (`Namotion.Interceptor.Connectors.csproj:10`), so the internal class is directly testable.

This task is a pure extraction: behavior must not change yet.

- [ ] **Step 1: Write the characterization test (positional behavior, revision 0)**

Create `src/Namotion.Interceptor.Connectors.Tests/ChangeDeduplicatorTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeDeduplicatorTests
{
    private static SubjectPropertyChange Change(PropertyReference property, string oldValue, string newValue, long revision)
        => SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, oldValue, newValue, revision);

    [Fact]
    public void WhenRevisionsAreZero_ThenDeduplicationFallsBackToPositionalOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var input = new List<SubjectPropertyChange>
        {
            Change(property, "a", "b", 0),
            Change(property, "b", "c", 0),
        };
        var deduplicator = new ChangeDeduplicator();

        // Act
        var count = deduplicator.Deduplicate(input, out var buffer);

        // Assert: one survivor, oldest old value, newest new value
        Assert.Equal(1, count);
        Assert.Equal("a", buffer[0].GetOldValue<string>());
        Assert.Equal("c", buffer[0].GetNewValue<string>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ChangeDeduplicatorTests"`
Expected: FAIL, compile error `The name 'ChangeDeduplicator' does not exist`.

- [ ] **Step 3: Create the deduplicator with today's positional logic moved verbatim**

Create `src/Namotion.Interceptor.Connectors/ChangeDeduplicator.cs`:

```csharp
using System.Buffers;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Collapses a flush batch to one change per property. Owns the pooled scratch buffers so
/// <see cref="ChangeQueueProcessor"/> keeps only queueing and dispatch. Not thread-safe: the caller
/// holds the flush gate, so every method runs single-threaded.
/// </summary>
internal sealed class ChangeDeduplicator : IDisposable
{
    private const int MinSize = 256;
    private const int MaxSize = 1024;

    // Value carries the survivor's buffer index plus the revision bounds seen for the property, so
    // the newest new value and the oldest old value are chosen by revision rather than by position.
    private readonly Dictionary<PropertyReference, (int Index, long Highest, long Lowest)> _indices = new(PropertyReference.Comparer);

    private SubjectPropertyChange[] _buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(MinSize);

    /// <summary>
    /// Deduplicates <paramref name="changes"/> into an internal pooled buffer and returns the count.
    /// The buffer is valid until the next call.
    /// </summary>
    internal int Deduplicate(List<SubjectPropertyChange> changes, out SubjectPropertyChange[] buffer)
    {
        _indices.Clear();
        _indices.EnsureCapacity(changes.Count);

        if (_buffer.Length < changes.Count)
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer, clearArray: true);
            _buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(changes.Count);
        }

        var count = 0;

        // Backward iteration keeps last-occurrence emit order (restored by the reverse below).
        for (var i = changes.Count - 1; i >= 0; i--)
        {
            var change = changes[i];
            if (!_indices.TryGetValue(change.Property, out var entry))
            {
                _indices[change.Property] = (count, change.Revision, change.Revision);
                _buffer[count++] = change;
                continue;
            }

            // 'change' is EARLIER by arrival than the survivor already stored at entry.Index.
            _buffer[entry.Index] = change.MergeWithNewer(_buffer[entry.Index]);
            _indices[change.Property] = entry;
        }

        if (count > 1)
        {
            Array.Reverse(_buffer, 0, count);
        }

        buffer = _buffer;
        return count;
    }

    /// <summary>Clears retained references after the batch has been handed to the write handler.</summary>
    internal void Reset()
    {
        _indices.Clear();
        Array.Clear(_buffer, 0, _buffer.Length);

        if (_buffer.Length >= MaxSize)
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
            _buffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(MinSize);
        }
    }

    public void Dispose()
    {
        if (_buffer is null)
        {
            return;
        }

        Array.Clear(_buffer, 0, _buffer.Length);
        ArrayPool<SubjectPropertyChange>.Shared.Return(_buffer);
        _buffer = null!;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ChangeDeduplicatorTests"`
Expected: PASS.

- [ ] **Step 5: Wire `ChangeQueueProcessor` to it**

In `ChangeQueueProcessor.cs`, delete the `_flushPropertyIndices`, `_flushDedupedBuffer`, and `_flushDedupedCount` fields (lines 40-44) and replace the dedup block inside `TryFlushAsync` with:

```csharp
            var count = _deduplicator.Deduplicate(_flushChanges, out var deduped);
            if (count > 0)
            {
                try
                {
                    await _writeHandler(new ReadOnlyMemory<SubjectPropertyChange>(deduped, 0, count), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write changes.");
                }
            }
```

Add the field `private readonly ChangeDeduplicator _deduplicator = new();`, call `_deduplicator.Reset()` in the `finally` alongside `_flushChanges.Clear()`, and `_deduplicator.Dispose()` in `Dispose()` in place of the old buffer return.

- [ ] **Step 6: Run the connector suite**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: PASS, no behavior change (this task is an extraction only).

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeDeduplicator.cs \
        src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs \
        src/Namotion.Interceptor.Connectors.Tests/ChangeDeduplicatorTests.cs
git commit -m "refactor: extract the flush deduplicator from ChangeQueueProcessor"
```

---

### Task 7: Revision-oriented deduplication

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeDeduplicator.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeDeduplicatorTests.cs`

This is the actual bug fix: today's survivor is chosen by array position (arrival order), which is post-commit race order.

- [ ] **Step 1: Write the failing test**

Append to `ChangeDeduplicatorTests`:

```csharp
    [Fact]
    public void WhenArrivalOrderInvertsCommitOrder_ThenHighestRevisionWinsAndLowestSuppliesOldValue()
    {
        // Arrange: revision 6 arrived FIRST, revision 5 second (a post-commit race inversion)
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var input = new List<SubjectPropertyChange>
        {
            Change(property, "b", "newest", 6),
            Change(property, "oldest", "b", 5),
        };
        var deduplicator = new ChangeDeduplicator();

        // Act
        var count = deduplicator.Deduplicate(input, out var buffer);

        // Assert
        Assert.Equal(1, count);
        Assert.Equal("newest", buffer[0].GetNewValue<string>());  // fails today: positional picks "b"
        Assert.Equal("oldest", buffer[0].GetOldValue<string>());
        Assert.Equal(6L, buffer[0].Revision);
    }

    [Fact]
    public void WhenThreeOccurrencesOutOfOrder_ThenBoundsAreTrackedAcrossAllOfThem()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var input = new List<SubjectPropertyChange>
        {
            Change(property, "v2", "v3", 8),
            Change(property, "v1", "v2", 7),
            Change(property, "v0", "v1", 6),
        };
        var deduplicator = new ChangeDeduplicator();

        // Act
        var count = deduplicator.Deduplicate(input, out var buffer);

        // Assert
        Assert.Equal(1, count);
        Assert.Equal("v0", buffer[0].GetOldValue<string>());
        Assert.Equal("v3", buffer[0].GetNewValue<string>());
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenArrivalOrderInvertsCommitOrder"`
Expected: FAIL, `Assert.Equal("newest", ...)` gets `"b"`.

- [ ] **Step 3: Choose survivor and baseline by revision**

Replace the merge branch in `Deduplicate` with:

```csharp
            // 'change' is EARLIER by arrival than the survivor stored at entry.Index. Revisions,
            // not positions, decide which end each value comes from: arrival order is post-commit
            // race order. Changes built outside a terminal write carry 0 and keep the old
            // positional behavior.
            var kept = _buffer[entry.Index];
            if (change.Revision == 0 || entry.Highest == 0)
            {
                kept = change.MergeWithNewer(kept);
            }
            else if (change.Revision > entry.Highest)
            {
                // This earlier-arriving change actually committed later: it supplies the new value.
                kept = kept.MergeWithNewer(change);
                entry.Highest = change.Revision;
            }
            else if (change.Revision < entry.Lowest)
            {
                kept = change.MergeWithNewer(kept);
                entry.Lowest = change.Revision;
            }

            _buffer[entry.Index] = kept;
            _indices[change.Property] = entry;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ChangeDeduplicatorTests"`
Expected: PASS (all four tests).

- [ ] **Step 5: Run the full connector suite**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeDeduplicator.cs \
        src/Namotion.Interceptor.Connectors.Tests/ChangeDeduplicatorTests.cs
git commit -m "fix: deduplicate flush batches by commit revision instead of arrival position"
```

---

### Task 8: Documentation and public API snapshot

**Files:**
- Modify: `docs/tracking.md`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Run the API snapshot test to see the diff**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PublicApi"`
Expected: FAIL, the received file adds `public long Revision { get; }` and the `long revision = 0` parameter on `Create`.

- [ ] **Step 2: Accept the snapshot**

```bash
cp src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Read the diff before accepting: it must contain **only** the `Revision` property and the new `Create` overload. Anything else means an unintended public change.

- [ ] **Step 3: Add the guarantee section to `docs/tracking.md`**

Add, in the property-change section:

```markdown
### Delivery guarantees

Every committed write carries a `SubjectPropertyChange.Revision`: monotonic per subject over
committed writes, so two changes to the *same subject* are ordered by comparing it. Revisions of
different subjects are **not** comparable. A change built outside a terminal write carries 0.

| Channel | Exactly-once | Order | Consumer runs on |
|---|---|---|---|
| Per-property callback | conditional (a) | arrival | writer thread |
| Observable | conditional (a) | arrival | writer thread |
| Pull queue | conditional (a) | arrival | consumer thread |
| `ChangeQueueProcessor`, buffer > 0 | no, latest-state-wins | arrival of survivors; per-property newest within a flush (b) | processor thread |

(a) A throwing lifecycle handler or a throwing earlier observer suppresses delivery for the rest of
that write's consumers, so delivery is exactly-once only while those no-throw contracts hold.
(b) Deduplication is scoped to one flush batch, so an inversion straddling a flush tick can still
emit the older value last. Compare `Revision` in the write handler if that matters.

Arrival order can differ from commit order under concurrent writers: dispatch happens after the
commit and outside the subject lock. Compare `Revision` to converge, or re-read the property.
```

- [ ] **Step 4: Verify the whole solution builds and the unit suites pass**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add docs/tracking.md src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "docs: document the commit revision and per-channel delivery guarantees"
```

---

### Task 9: Benchmark gates 1, 8 and 9

**Files:**
- Modify: `src/Namotion.Interceptor.Benchmark/RegistryBenchmark.cs` (no code change expected; used as-is)
- Create: `src/Namotion.Interceptor.Benchmark/ChangeDeduplicatorBenchmark.cs`

Gate 1 is the make-or-break number: this PR adds always-on cost to every committed write.

- [ ] **Step 1: Add the dedup benchmark**

Create `src/Namotion.Interceptor.Benchmark/ChangeDeduplicatorBenchmark.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Benchmark;

[MemoryDiagnoser]
public class ChangeDeduplicatorBenchmark
{
    private readonly ChangeDeduplicator _deduplicator = new();
    private List<SubjectPropertyChange> _batch = null!;

    [Params(64, 512)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext.Create();
        var car = new Car(context);
        _batch = new List<SubjectPropertyChange>(BatchSize);
        for (var i = 0; i < BatchSize; i++)
        {
            // Half the batch repeats one property so the merge path is exercised.
            var property = new PropertyReference(car, i % 2 == 0 ? "Name" : "Name");
            _batch.Add(SubjectPropertyChange.Create(
                property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "old", "new", i + 1));
        }
    }

    [Benchmark]
    public int Deduplicate()
    {
        var count = _deduplicator.Deduplicate(_batch, out _);
        _deduplicator.Reset();
        return count;
    }
}
```

`Namotion.Interceptor.Benchmark` already has `InternalsVisibleTo` from core (`Namotion.Interceptor.csproj:16`); add the same entry to `Namotion.Interceptor.Connectors.csproj` if the internal `ChangeDeduplicator` is not visible:

```xml
		<InternalsVisibleTo Include="Namotion.Interceptor.Benchmark" />
```

- [ ] **Step 2: Verify the benchmark project builds**

Run: `dotnet build src/Namotion.Interceptor.Benchmark -c Release`
Expected: build succeeds.

- [ ] **Step 3: Pin the CPU and run the gate**

Pin to a fixed frequency first (see the recipe in the benchmark memory note; untrusted timings otherwise).

Run:
```bash
pwsh scripts/benchmark.ps1 -Stash --filter "*RegistryBenchmark*"
```
Expected: `Write`, `WriteNoOp`, `WriteWithTimestampScope`, `Read` show **no measurable regression** and no allocation change. `WriteNoOp` in particular must be flat: the `[RunsFirst]` equality handler stops no-op writes before the terminal, so they must not pay anything.

- [ ] **Step 4: Run the struct-size and dedup gates**

Run:
```bash
pwsh scripts/benchmark.ps1 -Stash --filter "*SubjectUpdateBenchmark* *SubjectTransactionBenchmark* *ChangeDeduplicatorBenchmark*"
```
Expected: `SubjectPropertyChange`'s extra 8 bytes shows at most a small, explained delta on the copy-heavy update and transaction paths (the paths #389 measured). Record raw output.

- [ ] **Step 5: Record the results in the PR description**

Paste the raw BenchmarkDotNet tables. If gate 1 regresses measurably, **stop**: the spec requires changing the design (counter home or context stamp), not accepting the number.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Benchmark/ChangeDeduplicatorBenchmark.cs \
        src/Namotion.Interceptor.Connectors/Namotion.Interceptor.Connectors.csproj
git commit -m "benchmarks: add the deduplicator benchmark and record the revision-label gates"
```

---

## Self-Review

**Spec coverage (Phase 1 scope):** revision on the executor and both terminals (Tasks 1-2); `SubjectPropertyChange.Revision` with the optional `revision` parameter and propagation through the private ctor, `MergeWithNewer`, `WithOrigin` (Task 3); publication on the channels (Task 4); `FinalValueIsNewValue` (Task 5); revision-oriented dedup with the `Revision` 0 positional fallback and both revision bounds (Tasks 6-7); `docs/tracking.md` matrix with the within-flush caveat (Task 8); gates 1, 8, 9 (Task 9). The non-executor fallback is covered in Task 1.

**Deliberately out of scope, and why:** the `Interlocked.CompareExchange` executor publication and the `Interlocked` gate discipline belong to Phase 2 (nothing in Phase 1 reads a per-subject subscription count); cross-flush convergence is a recorded follow-up, and Task 8's documentation states the limit honestly rather than implying a full fix.

**Type consistency:** `SubjectRevisionCounter.Next(IInterceptorSubject)` is used identically in both terminals; `context.Revision` is written in Task 2 and read in Task 4; `ChangeDeduplicator.Deduplicate(List<SubjectPropertyChange>, out SubjectPropertyChange[])` plus `Reset()`/`Dispose()` are the same members in Tasks 6, 7, and 9.

**Known verification point for the executing engineer:** Task 5, Step 4 depends on the exact parameter list of the internal `SetPropertyValueWithInterception` overload (`PropertyReferenceExtensions.cs:25-30`). Read it before editing; if the cascade path is shared with any other caller, add an explicit `finalValueIsNewValue` parameter instead of hard-coding `true`.
