# Phase 2: Ordered exactly-once delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `PropertyChangeDelivery.Ordered` mode that delivers property changes exactly once, in commit order per subject, on all four subscription channels.

**Architecture:** Order and content are split. Order is fixed inside the subject lock the commit already holds: the terminal reserves a slot (a header, not a payload) in each armed subscription's segmented buffer, so slot position *is* commit position. Content arrives later: `PropertyChangeInterceptor`'s `finally` builds the payload once, outside the lock, and releases the reserved slots, guaranteeing no slot is ever left pending. Delivery is a FIFO drain that waits on a pending head, delivers before advancing, and clears the slot after.

**Tech Stack:** C# 13, .NET Standard 2.0 (core) / .NET 9 (extensions), xUnit, BenchmarkDotNet, PublicApiGenerator + Verify.

**Spec:** `docs/superpowers/specs/2026-07-29-ordered-change-delivery-design.md` (sections 2-8; Decided 1-11).

**Branch:** `feature/ordered-delivery`, **stacked on `feature/revision-label`** (Phase 1). Do not rebase onto master until Phase 1 merges. If a benchmark here forces a Phase 1 change (counter home, context stamp, struct growth), amend Phase 1 rather than working around it.

**Breaking changes in this PR:** `PropertyChangeDelivery` becomes a required parameter on `GetPropertyChangeObservable`, `CreatePropertyChangeQueueSubscription`, `Subscribe`, and `SubscribeToProperty`. Mechanical for callers (add one argument).

---

## File Structure

**Core (`src/Namotion.Interceptor`), all internal:**
- `Ordering/OrderedSlotBuffer.cs` (create): non-generic header segments (revision, state) plus a generic derived payload buffer. The terminal is generic over `TProperty`, not over the payload, so it must reserve through a non-generic surface.
- `Ordering/OrderedSubscriptionRegistry.cs` (create): per-context volatile subscription array, captured in the chain closure.
- `Ordering/OrderedReservationRecord.cs` (create): pooled `(subscription, slotIndex)` tracker with a progress count.
- `Ordering/OrderedPropertyIndex.cs` (create): core-owned per-property index in `Subject.Data`, the parallel of #377's `Immediate` layout.
- `Interceptors/InterceptorExecutor.cs` (modify): `OrderedPropertyListenerCount` (`Interlocked`-maintained).
- `Interceptors/IWriteInterceptor.cs` (modify): `PublisherPresent`, `ReservationRecord`.
- `Cache/WriteInterceptorFactory.cs` (modify): reservation before the store, in both terminals.
- `InterceptorSubjectContext.cs` (modify): capture the ordered registries at chain build.

**Generator:** `SubjectCodeGenerator.cs` (modify): race-free executor publication.

**Tracking (`src/Namotion.Interceptor.Tracking`):**
- `Change/PropertyChangeDelivery.cs` (create): the public enum.
- `Change/OrderedSubscription.cs` (create): buffer owner, drain loop, cancellable pending-head wait, `IDisposable`.
- `Change/ExclusiveDrainGate.cs` (create): the CAS-gated on-demand drain scheduler, shared later with #381's `PathDeliveryQueue`.
- `Change/PropertyChangeInterceptor.cs` (modify): rent-once, marker, `try/finally` publish with consume-once and one shared payload.
- `Change/PropertyChangeSubscriptionExtensions.cs`, `InterceptorSubjectContextExtensions.cs` (modify): the required enum parameter on all entry points.

**Connectors:** `ChangeQueueProcessor.cs` (modify): `bufferTime = 0` subscribes `Ordered`, `bufferTime > 0` stays `Immediate`.

---

## Group A: Prerequisites (must complete before any reservation code)

### Task 1: Race-free executor publication

**Files:**
- Modify: `src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs:142`
- Modify: `src/Namotion.Interceptor.Dynamic/DynamicSubject.cs:30` (same shape)
- Test: `src/Namotion.Interceptor.Tests/ExecutorPublicationTests.cs`

The generated `_context ??= new InterceptorExecutor(this)` is a racy lazy initializer: two threads racing the first `Context` access each construct an executor and one store is discarded. Phase 2 puts the ordered-subscription count on the executor, so a discarded instance means a subscription that no write ever reserves into, permanently.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Namotion.Interceptor.Tests;

public class ExecutorPublicationTests
{
    [Fact]
    public void WhenContextIsAccessedConcurrently_ThenAllThreadsSeeTheSameExecutor()
    {
        // Arrange
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var person = new Person();
            var contexts = new IInterceptorSubjectContext[2];
            using var start = new ManualResetEventSlim(false);

            var first = new Thread(() => { start.Wait(); contexts[0] = ((IInterceptorSubject)person).Context; });
            var second = new Thread(() => { start.Wait(); contexts[1] = ((IInterceptorSubject)person).Context; });
            first.Start();
            second.Start();

            // Act
            start.Set();
            first.Join();
            second.Join();

            // Assert
            Assert.Same(contexts[0], contexts[1]);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ExecutorPublicationTests"`
Expected: FAIL intermittently (`Assert.Same` failure on at least one attempt). If it passes 200 attempts, raise the attempt count and add `Thread.SpinWait` jitter before the read; the race is narrow but real.

- [ ] **Step 3: Emit interlocked publication**

In `SubjectCodeGenerator.cs`, replace the generated accessor:

```csharp
                    IInterceptorSubjectContext IInterceptorSubject.Context
                    {
                        get
                        {
                            var context = _context;
                            if (context is not null)
                            {
                                return context;
                            }

                            // Race-free publication: a loser's instance is discarded before any
                            // state (services, chain cache, ordered-subscription count) reaches it.
                            var created = new InterceptorExecutor(this);
                            return System.Threading.Interlocked.CompareExchange(ref _context, created, null) ?? created;
                        }
                    }
```

Apply the identical shape to `DynamicSubject.cs:30`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~ExecutorPublicationTests"`
Expected: PASS.

- [ ] **Step 5: Run the generator snapshot and full unit suites**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS. Generator snapshot tests will show the new accessor shape; accept those snapshots after reading the diff.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Generator/SubjectCodeGenerator.cs \
        src/Namotion.Interceptor.Dynamic/DynamicSubject.cs \
        src/Namotion.Interceptor.Tests/ExecutorPublicationTests.cs
git commit -m "fix: publish the subject executor race-free instead of via a lazy ??="
```

---

### Task 2: Verify chain-build snapshot consistency (investigation, no production code)

**Files:**
- Read: `src/Namotion.Interceptor/InterceptorSubjectContext.cs:96-99,183-195,226-237,343-388`
- Create: `docs/superpowers/plans/2026-07-30-ordered-delivery-phase2-findings.md`

The registry must be captured from the *same* service snapshot as the interceptors, and the spec's "invalidated on every attach" claim is known to be literally false on the `_noServicesSingleFallbackContext` delegation path.

- [ ] **Step 1: Answer these questions in writing, with file:line evidence**

1. `GetWriteInterceptorFunction` (`:226-237`) fetches only `IWriteInterceptor`. Can the registry be fetched in the same critical section, and under which lock?
2. `_noServicesSingleFallbackContext` (`:183-195`): when a subject's executor has no services, the chain and its closure live on the shared parent context. Does a `Subscribe` on the parent's registry therefore reach every delegating subject with no extra work? Confirm by reading, not by assumption.
3. `AddFallbackContext` fast path (`:96-99`) skips `OnContextChanged` when no caches exist. Can a chain built *before* a registry existed survive a later `Subscribe`? (Expected answer: yes and that is fine, because `WithPropertyChangeSubscriptions()` registers the registry object up front and `Subscribe` only mutates the array inside it. **Verify that the registry is in fact registered at configuration time, not at first subscribe.** If it is not, make it so.)
4. Under aggregation, does `GetServices` return one registry instance per subscribing context (so the terminal must loop), or a deduplicated set?

- [ ] **Step 2: Record the answers**

Write the findings file with one section per question, each with file:line evidence and a yes/no conclusion. If question 3's expected answer is wrong, stop and raise it: the registry must move to configuration time before any other task proceeds.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/2026-07-30-ordered-delivery-phase2-findings.md
git commit -m "docs: record the chain-build snapshot findings for ordered delivery"
```

---

## Group B: Core reservation machinery

### Task 3: The slot buffer

**Files:**
- Create: `src/Namotion.Interceptor/Ordering/OrderedSlotBuffer.cs`
- Test: `src/Namotion.Interceptor.Tests/Ordering/OrderedSlotBufferTests.cs`

Slots are structs in segmented arrays: reservation must not allocate per write, and the terminal (generic over `TProperty`, not the payload) reserves through a non-generic surface.

- [ ] **Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Tests.Ordering;

public class OrderedSlotBufferTests
{
    [Fact]
    public void WhenSlotsAreReserved_ThenIndicesAreSequentialAndStatesArePending()
    {
        // Arrange
        var buffer = new OrderedSlotBuffer<int>(initialCapacity: 4);

        // Act
        var first = buffer.ReserveSlot(revision: 7);
        var second = buffer.ReserveSlot(revision: 8);

        // Assert
        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(OrderedSlotState.Pending, buffer.GetState(first));
        Assert.Equal(7, buffer.GetRevision(first));
    }

    [Fact]
    public void WhenSlotIsReleasedAndDrained_ThenPayloadIsDeliveredThenCleared()
    {
        // Arrange
        var buffer = new OrderedSlotBuffer<int>(initialCapacity: 4);
        var index = buffer.ReserveSlot(revision: 1);

        // Act
        buffer.Release(index, 42);
        var hasHead = buffer.TryPeekHead(out var headIndex);
        var state = buffer.GetState(headIndex);
        var payload = buffer.GetPayload(headIndex);
        buffer.AdvanceHead();

        // Assert
        Assert.True(hasHead);
        Assert.Equal(index, headIndex);
        Assert.Equal(OrderedSlotState.Released, state);
        Assert.Equal(42, payload);
        Assert.False(buffer.TryPeekHead(out _));
        Assert.Equal(0, buffer.GetPayload(index)); // cleared on advance, no retained references
    }

    [Fact]
    public void WhenCapacityIsExceeded_ThenBufferGrowsAndPreservesOrder()
    {
        // Arrange
        var buffer = new OrderedSlotBuffer<int>(initialCapacity: 2);

        // Act
        for (var i = 0; i < 10; i++)
        {
            buffer.Release(buffer.ReserveSlot(i + 1), i);
        }

        // Assert
        for (var i = 0; i < 10; i++)
        {
            Assert.True(buffer.TryPeekHead(out var index));
            Assert.Equal(i, buffer.GetPayload(index));
            buffer.AdvanceHead();
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OrderedSlotBufferTests"`
Expected: FAIL, `OrderedSlotBuffer` does not exist.

- [ ] **Step 3: Implement the buffer**

Create `src/Namotion.Interceptor/Ordering/OrderedSlotBuffer.cs`:

```csharp
using System.Threading;

namespace Namotion.Interceptor.Ordering;

internal enum OrderedSlotState
{
    Pending = 0,
    Released = 1,
    Cancelled = 2,
}

/// <summary>
/// Non-generic reservation surface. The terminal write is generic over the property type, not over
/// the payload type, so it reserves through this base and the derived buffer owns the payload.
/// </summary>
internal abstract class OrderedSlotBufferBase
{
    /// <summary>
    /// Reserves the next slot and returns its index. Called with the subject's SyncRoot held, which
    /// is what makes slot order equal commit order for that subject. Allocation-free except for
    /// amortized segment growth.
    /// </summary>
    internal abstract int ReserveSlot(long revision);

    /// <summary>Marks a reserved slot as carrying no delivery (an uncommitted or failed write).</summary>
    internal abstract void Cancel(int index);
}

internal sealed class OrderedSlotBuffer<TPayload>(int initialCapacity = 8) : OrderedSlotBufferBase
{
    private struct Slot
    {
        internal long Revision;
        internal int State;
        internal TPayload Payload;
    }

    private readonly object _growLock = new();
    private Slot[] _slots = new Slot[initialCapacity];
    private int _tail;      // next index to reserve
    private int _head;      // next index to deliver

    internal override int ReserveSlot(long revision)
    {
        lock (_growLock)
        {
            if (_tail == _slots.Length)
            {
                Compact();
            }

            var index = _tail++;
            _slots[index].Revision = revision;
            _slots[index].State = (int)OrderedSlotState.Pending;
            return index;
        }
    }

    // Called under _growLock. Drops already-delivered slots, growing only when the live window fills
    // the array, so steady-state delivery never allocates.
    private void Compact()
    {
        var live = _tail - _head;
        if (live == 0)
        {
            Array.Clear(_slots, 0, _slots.Length);
            _head = 0;
            _tail = 0;
            return;
        }

        if (live < _slots.Length / 2)
        {
            Array.Copy(_slots, _head, _slots, 0, live);
            Array.Clear(_slots, live, _slots.Length - live);
        }
        else
        {
            var grown = new Slot[_slots.Length * 2];
            Array.Copy(_slots, _head, grown, 0, live);
            _slots = grown;
        }

        _head = 0;
        _tail = live;
    }

    internal void Release(int index, TPayload payload)
    {
        _slots[index].Payload = payload;
        Volatile.Write(ref _slots[index].State, (int)OrderedSlotState.Released);
    }

    internal override void Cancel(int index)
        => Volatile.Write(ref _slots[index].State, (int)OrderedSlotState.Cancelled);

    internal OrderedSlotState GetState(int index) => (OrderedSlotState)Volatile.Read(ref _slots[index].State);

    internal long GetRevision(int index) => _slots[index].Revision;

    internal TPayload GetPayload(int index) => _slots[index].Payload;

    internal bool TryPeekHead(out int index)
    {
        lock (_growLock)
        {
            if (_head == _tail)
            {
                index = -1;
                return false;
            }

            index = _head;
            return true;
        }
    }

    /// <summary>
    /// Retires the head slot. Must be called only AFTER the payload has been delivered: clearing and
    /// making the slot reclaimable while a consumer still reads it by reference would tear the read.
    /// </summary>
    internal void AdvanceHead()
    {
        lock (_growLock)
        {
            _slots[_head].Payload = default!;   // do not pin subjects or boxed values
            _slots[_head].Revision = 0;
            _head++;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OrderedSlotBufferTests"`
Expected: PASS (all three).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor/Ordering/OrderedSlotBuffer.cs \
        src/Namotion.Interceptor.Tests/Ordering/OrderedSlotBufferTests.cs
git commit -m "feat: add the ordered slot buffer with non-generic reservation"
```

---

### Task 4: The pooled reservation record

**Files:**
- Create: `src/Namotion.Interceptor/Ordering/OrderedReservationRecord.cs`
- Test: `src/Namotion.Interceptor.Tests/Ordering/OrderedReservationRecordTests.cs`

The record cannot hold a `ref` to a struct slot, so it stores `(buffer, slotIndex)` pairs. Its progress count is what makes reservation failure-atomic: it is incremented only *after* a successful reserve, so a throw mid-loop leaves exactly the tracked prefix for the `finally` to cancel.

- [ ] **Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Tests.Ordering;

public class OrderedReservationRecordTests
{
    [Fact]
    public void WhenTrackingThrowsMidLoop_ThenOnlyTheTrackedPrefixIsCancelled()
    {
        // Arrange
        var record = new OrderedReservationRecord();
        var first = new OrderedSlotBuffer<int>();
        var second = new OrderedSlotBuffer<int>();
        record.Track(first, first.ReserveSlot(1));
        var untrackedIndex = second.ReserveSlot(2); // reserved but NOT tracked (simulates a throw)

        // Act
        record.CancelAll();

        // Assert
        Assert.Equal(OrderedSlotState.Cancelled, first.GetState(0));
        Assert.Equal(OrderedSlotState.Pending, second.GetState(untrackedIndex));
        Assert.Equal(0, record.Count);
    }

    [Fact]
    public void WhenReset_ThenNoBufferReferencesAreRetained()
    {
        // Arrange
        var record = new OrderedReservationRecord();
        var buffer = new OrderedSlotBuffer<int>();
        record.Track(buffer, buffer.ReserveSlot(1));

        // Act
        record.CancelAll();

        // Assert
        Assert.Equal(0, record.Count);
        Assert.False(record.HoldsAnyBuffer, "a pooled record must not pin buffers between writes");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OrderedReservationRecordTests"`
Expected: FAIL, type does not exist.

- [ ] **Step 3: Implement the record**

Create `src/Namotion.Interceptor/Ordering/OrderedReservationRecord.cs`:

```csharp
namespace Namotion.Interceptor.Ordering;

/// <summary>
/// Tracks the slots one write reserved, so the publishing finally can release or cancel exactly
/// those. Pooled and rented outside the subject lock. Not thread-safe: it belongs to one write on
/// one thread, reachable only through that write's by-ref context.
/// </summary>
internal sealed class OrderedReservationRecord
{
    private (OrderedSlotBufferBase Buffer, int Index)[] _entries = new (OrderedSlotBufferBase, int)[4];

    internal int Count { get; private set; }

    internal bool HoldsAnyBuffer
    {
        get
        {
            for (var i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Buffer is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Records a successful reservation. Count is incremented last, so a throw between reserving and
    /// tracking leaves the untracked slot out of the cancel set (that write never commits, and the
    /// slot's own subscription is torn down with its buffer).
    /// </summary>
    internal void Track(OrderedSlotBufferBase buffer, int index)
    {
        if (Count == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        _entries[Count] = (buffer, index);
        Count++;
    }

    internal (OrderedSlotBufferBase Buffer, int Index) this[int i] => _entries[i];

    internal void CancelAll()
    {
        for (var i = 0; i < Count; i++)
        {
            _entries[i].Buffer.Cancel(_entries[i].Index);
        }

        Reset();
    }

    internal void Reset()
    {
        Array.Clear(_entries, 0, _entries.Length);
        Count = 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OrderedReservationRecordTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor/Ordering/OrderedReservationRecord.cs \
        src/Namotion.Interceptor.Tests/Ordering/OrderedReservationRecordTests.cs
git commit -m "feat: add the pooled ordered reservation record"
```

---

### Task 5: The registry, the per-property index, and the interlocked gate

**Files:**
- Create: `src/Namotion.Interceptor/Ordering/OrderedSubscriptionRegistry.cs`
- Create: `src/Namotion.Interceptor/Ordering/OrderedPropertyIndex.cs`
- Modify: `src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs`
- Test: `src/Namotion.Interceptor.Tests/Ordering/OrderedPropertyIndexTests.cs`

The gate must follow #377's discipline exactly (`PropertyChangeSubscriptions.cs:12-22`, `PropertyChangeSubscription.cs:33,60,67`): `Interlocked` increment **before** install, decrement **after** removal, and `Interlocked.MemoryBarrier()` after the install, because a `ConcurrentDictionary` bucket-lock release is not a full fence. A plain field loses updates under concurrent subscribes to two properties of one subject, and a lost increment closes the gate permanently on a live subscription.

- [ ] **Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Tests.Ordering;

public class OrderedPropertyIndexTests
{
    [Fact]
    public void WhenConcurrentInstallsRace_ThenTheGateCountsEveryLiveSubscription()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var executor = (Interceptors.InterceptorExecutor)((IInterceptorSubject)person).Context;
        var buffers = new OrderedSlotBufferBase[64];
        for (var i = 0; i < buffers.Length; i++)
        {
            buffers[i] = new OrderedSlotBuffer<int>();
        }

        // Act: install 64 listeners across two properties from many threads
        Parallel.For(0, buffers.Length, i =>
            OrderedPropertyIndex.Install(
                new PropertyReference(person, i % 2 == 0 ? nameof(Person.FirstName) : nameof(Person.LastName)),
                buffers[i]));

        // Assert
        Assert.Equal(buffers.Length, Volatile.Read(ref executor.OrderedPropertyListenerCount));
        Assert.True(OrderedPropertyIndex.TryGet(new PropertyReference(person, nameof(Person.FirstName)), out var installed));
        Assert.Equal(buffers.Length / 2, installed!.Length);
    }

    [Fact]
    public void WhenAllRemoved_ThenGateReturnsToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var executor = (Interceptors.InterceptorExecutor)((IInterceptorSubject)person).Context;
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var buffer = new OrderedSlotBuffer<int>();

        // Act
        OrderedPropertyIndex.Install(property, buffer);
        OrderedPropertyIndex.Remove(property, buffer);

        // Assert
        Assert.Equal(0, Volatile.Read(ref executor.OrderedPropertyListenerCount));
        Assert.False(OrderedPropertyIndex.TryGet(property, out _));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OrderedPropertyIndexTests"`
Expected: FAIL, types do not exist.

- [ ] **Step 3: Add the gate field**

In `InterceptorExecutor.cs`, next to `Revision`:

```csharp
    /// <summary>
    /// Number of ordered per-property subscriptions installed on this subject. Maintained with
    /// Interlocked (increment before install, decrement after removal) and read under the subject's
    /// SyncRoot by the terminal, so a nonzero value always precedes a visible install. A plain field
    /// would lose updates and permanently close the gate on a live subscription.
    /// </summary>
    internal int OrderedPropertyListenerCount;
```

- [ ] **Step 4: Implement the registry and the index**

Create `src/Namotion.Interceptor/Ordering/OrderedSubscriptionRegistry.cs`:

```csharp
using System.Threading;

namespace Namotion.Interceptor.Ordering;

/// <summary>
/// Per-context set of context-wide ordered subscriptions. Registered at configuration time (not at
/// first subscribe) so the compiled write chain can capture it in its closure and a later Subscribe
/// is just an array swap, needing no chain rebuild.
/// </summary>
internal sealed class OrderedSubscriptionRegistry
{
    private readonly object _lock = new();

    private volatile OrderedSlotBufferBase[] _subscriptions = [];

    internal OrderedSlotBufferBase[] Subscriptions => _subscriptions;

    internal void Add(OrderedSlotBufferBase buffer)
    {
        lock (_lock)
        {
            var current = _subscriptions;
            var updated = new OrderedSlotBufferBase[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[current.Length] = buffer;
            _subscriptions = updated;
        }

        // The volatile publish orders the install against a terminal's in-lock read; the explicit
        // barrier mirrors PropertyChangeSubscription.Create, since a lock release is not a full fence.
        Interlocked.MemoryBarrier();
    }

    internal void Remove(OrderedSlotBufferBase buffer)
    {
        lock (_lock)
        {
            var current = _subscriptions;
            var index = Array.IndexOf(current, buffer);
            if (index < 0)
            {
                return;
            }

            var updated = new OrderedSlotBufferBase[current.Length - 1];
            Array.Copy(current, updated, index);
            Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
            _subscriptions = updated;
        }
    }
}
```

Create `src/Namotion.Interceptor/Ordering/OrderedPropertyIndex.cs`:

```csharp
using System.Threading;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Ordering;

/// <summary>
/// Core-owned per-property index of ordered subscriptions, stored in subject data. Deliberately a
/// parallel structure to the Tracking-owned Immediate listener index: core cannot reference Tracking
/// types, and the terminal must read this inside the subject lock.
/// </summary>
internal static class OrderedPropertyIndex
{
    private const string IndexKey = "ni.opl";

    internal static void Install(PropertyReference property, OrderedSlotBufferBase buffer)
    {
        // Increment BEFORE the install: a write that sees the gate open may find nothing (harmless),
        // but a write that finds the gate closed must be certain nothing is installed.
        if (property.Subject.Context is InterceptorExecutor executor)
        {
            Interlocked.Increment(ref executor.OrderedPropertyListenerCount);
        }

        var key = (property.Name, IndexKey);
        var data = property.Subject.Data;
        while (true)
        {
            if (data.TryGetValue(key, out var existing))
            {
                var current = (OrderedSlotBufferBase[])existing!;
                var updated = new OrderedSlotBufferBase[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = buffer;
                if (data.TryUpdate(key, updated, current))
                {
                    break;
                }
            }
            else if (data.TryAdd(key, new[] { buffer }))
            {
                break;
            }
        }

        Interlocked.MemoryBarrier(); // see OrderedSubscriptionRegistry.Add
    }

    internal static void Remove(PropertyReference property, OrderedSlotBufferBase buffer)
    {
        var key = (property.Name, IndexKey);
        var data = property.Subject.Data;
        while (true)
        {
            if (!data.TryGetValue(key, out var existing) || existing is not OrderedSlotBufferBase[] current)
            {
                break;
            }

            var index = Array.IndexOf(current, buffer);
            if (index < 0)
            {
                break;
            }

            if (current.Length == 1)
            {
                if (new PropertyReference(property.Subject, property.Name).TryRemovePropertyData(IndexKey, current))
                {
                    break;
                }
            }
            else
            {
                var updated = new OrderedSlotBufferBase[current.Length - 1];
                Array.Copy(current, updated, index);
                Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
                if (data.TryUpdate(key, updated, current))
                {
                    break;
                }
            }
        }

        // Decrement AFTER removal, so the gate never reads closed while a listener is still installed.
        if (property.Subject.Context is InterceptorExecutor executor)
        {
            Interlocked.Decrement(ref executor.OrderedPropertyListenerCount);
        }
    }

    internal static bool TryGet(PropertyReference property, out OrderedSlotBufferBase[]? buffers)
    {
        if (property.Subject.Data.TryGetValue((property.Name, IndexKey), out var value) &&
            value is OrderedSlotBufferBase[] installed)
        {
            buffers = installed;
            return true;
        }

        buffers = null;
        return false;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~OrderedPropertyIndexTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor/Ordering/OrderedSubscriptionRegistry.cs \
        src/Namotion.Interceptor/Ordering/OrderedPropertyIndex.cs \
        src/Namotion.Interceptor/Interceptors/InterceptorExecutor.cs \
        src/Namotion.Interceptor.Tests/Ordering/OrderedPropertyIndexTests.cs
git commit -m "feat: add the ordered subscription registry, per-property index and interlocked gate"
```

---

### Task 6: Reserve in both terminals

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs`
- Modify: `src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs` (both terminals)
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs` (capture registries at chain build)
- Test: `src/Namotion.Interceptor.Tests/Ordering/TerminalReservationTests.cs`

Two invariants this task must establish: reservation happens **before** the value store (so a throwing reservation aborts an uncommitted write, making committed-implies-reserved structural), and reservation is gated on **both** the publisher marker and a non-null executor (ordered delivery is unsupported for non-executor subjects, and the gate has no home there).

- [ ] **Step 1: Write the failing test**

```csharp
using Namotion.Interceptor.Ordering;

namespace Namotion.Interceptor.Tests.Ordering;

public class TerminalReservationTests
{
    [Fact]
    public void WhenMarkerAbsent_ThenNoSlotIsReserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var buffer = new OrderedSlotBuffer<int>();
        OrderedPropertyIndex.Install(new PropertyReference(person, nameof(Person.FirstName)), buffer);

        // Act: no PropertyChangeInterceptor in the chain, so no marker is ever set
        person.FirstName = "a";

        // Assert: a miss, never a pending slot that would stall a drain forever
        Assert.False(buffer.TryPeekHead(out _));
    }

    [Fact]
    public void WhenMarkerSet_ThenSlotsAreReservedInCommitOrder()
    {
        // Arrange
        var buffer = new OrderedSlotBuffer<int>();
        var context = InterceptorSubjectContext.Create().WithService(() => new MarkerInterceptor());
        var person = new Person(context);
        OrderedPropertyIndex.Install(new PropertyReference(person, nameof(Person.FirstName)), buffer);

        // Act
        person.FirstName = "a";
        person.FirstName = "b";

        // Assert
        Assert.True(buffer.TryPeekHead(out var head));
        Assert.Equal(OrderedSlotState.Pending, buffer.GetState(head));
        Assert.True(buffer.GetRevision(head) > 0);
    }

    private sealed class MarkerInterceptor : Interceptors.IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref Interceptors.PropertyWriteContext<TProperty> context, Interceptors.WriteInterceptionDelegate<TProperty> next)
        {
            context.ReservationRecord = new OrderedReservationRecord();
            context.PublisherPresent = true;
            next(ref context);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~TerminalReservationTests"`
Expected: FAIL, `PropertyWriteContext` has no `ReservationRecord` or `PublisherPresent`.

- [ ] **Step 3: Add the context fields**

In `PropertyWriteContext<TProperty>`:

```csharp
    /// <summary>
    /// Set by <c>PropertyChangeInterceptor</c> before calling next(). The terminal reserves slots
    /// only when this is set, so "a slot was reserved but nothing on this stack will resolve it"
    /// is unrepresentable: a chain without the publisher misses the write rather than stalling a
    /// drain forever.
    /// </summary>
    internal bool PublisherPresent;

    /// <summary>Pooled tracker for the slots this write reserved; null when unarmed.</summary>
    internal Ordering.OrderedReservationRecord? ReservationRecord;
```

- [ ] **Step 4: Capture registries at chain build**

Following Task 2's findings, extend chain construction so the compiled terminal closes over the ordered registries visible in the aggregated context set (plural: one per subscribing context). Pass them into `WriteInterceptorFactory<TProperty>.Create` alongside the interceptors, fetched in the same service snapshot as `IWriteInterceptor`.

- [ ] **Step 5: Reserve in both terminals**

In each of the two lock bodies in `WriteInterceptorFactory.cs`, **before** `innerWriteValue(...)`:

```csharp
                    var revision = ++context.Executor.Revision;
                    context.Revision = revision;

                    // The executor is on the write context, so there is no lookup and no subject that
                    // can miss out: every committed write gets the revision label above, and the marker
                    // below only decides whether slots are also reserved.
                    if (context.PublisherPresent &&
                        context.ReservationRecord is { } record)
                    {
                        for (var r = 0; r < registries.Length; r++)
                        {
                            var subscriptions = registries[r].Subscriptions;
                            for (var s = 0; s < subscriptions.Length; s++)
                            {
                                record.Track(subscriptions[s], subscriptions[s].ReserveSlot(revision));
                            }
                        }

                        if (Volatile.Read(ref context.Executor.OrderedPropertyListenerCount) != 0 &&
                            OrderedPropertyIndex.TryGet(property, out var listeners))
                        {
                            for (var l = 0; l < listeners!.Length; l++)
                            {
                                record.Track(listeners[l], listeners[l].ReserveSlot(revision));
                            }
                        }
                    }

                    innerWriteValue(subject, context.NewValue);
                    context.IsWritten = true;
```

Both terminals already hoist `var property = context.Property;` and `var subject = property.Subject;` above the lock, so use those locals rather than re-reading `context.Property`, which is a struct copy the JIT cannot fold across the opaque `innerWriteValue` call.

Remove the Phase 1 `context.Revision = ...` line and its `Debug.Assert` that sat after `IsWritten`, since the revision is now assigned above. Keep the assert, moved up next to the new increment: it is what pins the executor-owns-subject pairing the plain increment depends on.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~TerminalReservationTests"`
Expected: PASS.

- [ ] **Step 7: Run the Phase 1 revision tests to confirm no regression**

Run: `dotnet test src/Namotion.Interceptor.Tests src/Namotion.Interceptor.Tracking.Tests`
Expected: PASS, including `CommitRevisionTests` and `PropertyChangeRevisionTests` from Phase 1.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor/Interceptors/IWriteInterceptor.cs \
        src/Namotion.Interceptor/Cache/WriteInterceptorFactory.cs \
        src/Namotion.Interceptor/InterceptorSubjectContext.cs \
        src/Namotion.Interceptor.Tests/Ordering/TerminalReservationTests.cs
git commit -m "feat: reserve ordered slots in the terminal write, gated on the publisher marker"
```

---

## Group C: Tracking publish and delivery

### Task 7: Publish in the finally with rent-once and consume-once

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeDelivery.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/OrderedPublishTests.cs`

Three defects the review caught must be prevented by construction here: renting the record twice under aggregated contexts leaks one pooled object per write (rent only when the field is null); publishing twice under aggregation corrupts already-consumed slots (null the record when consuming); and building the payload twice can make the `Immediate` and `Ordered` channels disagree for the same write (build once, share).

- [ ] **Step 1: Write the failing tests**

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class OrderedPublishTests
{
    [Fact]
    public void WhenLifecycleHandlerThrows_ThenTheCommittedChangeIsStillDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithService(() => new ThrowingLifecycleHandler());
        var person = new Person(context);
        var delivered = new List<long>();
        using var subscription = person.SubscribeToProperty(
            p => p.FirstName,
            (in SubjectPropertyChange change) => delivered.Add(change.Revision),
            PropertyChangeDelivery.Ordered);

        // Act
        Assert.ThrowsAny<Exception>(() => person.FirstName = "a");

        // Assert: committed implies delivered; the exception still propagated above
        Assert.Single(delivered);
    }

    [Fact]
    public void WhenWriteIsVetoed_ThenNoDeliveryOccursAndNoSlotStaysPending()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithService(() => new VetoingInterceptor());
        var person = new Person(context);
        var delivered = 0;
        using var subscription = person.SubscribeToProperty(
            p => p.FirstName,
            (in SubjectPropertyChange _) => delivered++,
            PropertyChangeDelivery.Ordered);

        // Act
        person.FirstName = "a";

        // Assert
        Assert.Equal(0, delivered);
    }
}
```

(Define `ThrowingLifecycleHandler` as an `ILifecycleHandler` whose attach throws, and `VetoingInterceptor` as an `IWriteInterceptor` that returns without calling `next`, following the existing patterns in `PropertyChangeInterceptorTests`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~OrderedPublishTests"`
Expected: FAIL, `PropertyChangeDelivery` does not exist.

- [ ] **Step 3: Add the enum**

Create `src/Namotion.Interceptor.Tracking/Change/PropertyChangeDelivery.cs` with the exact XML documentation from the spec's section 8 (including the per-subject-only order wording and the performance paragraphs).

- [ ] **Step 4: Rewrite `WriteProperty`'s arming and publish**

```csharp
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Rent-once: under aggregated contexts several interceptor instances run this method for one
        // write. An unconditional rent would overwrite the field and leak one pooled record per write.
        if (IsOrderedArmed(ref context) && context.ReservationRecord is null)
        {
            context.ReservationRecord = ReservationRecordPool.Rent();
            context.PublisherPresent = true;
        }

        try
        {
            // ... existing idle fast path and dispatch, unchanged, except the payload is built once
            // and reused for both the Immediate channels and the slot publish
            next(ref context);
            DispatchImmediateChannels(ref context, out var payload, out var hasPayload);
            PublishOrdered(ref context, payload, hasPayload);
        }
        finally
        {
            // Consume-once: the innermost instance resolves and clears; outer instances no-op.
            if (context.ReservationRecord is { } record)
            {
                context.ReservationRecord = null;
                if (!context.IsWritten)
                {
                    record.CancelAll();
                }

                ReservationRecordPool.Return(record);
            }
        }
    }
```

`PublishOrdered` releases each tracked slot with the shared payload (or cancels it when the payload could not be built, the derived-with-setter getter-throw case), then resets the record so the `finally` sees nothing left to cancel.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~OrderedPublishTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/PropertyChangeDelivery.cs \
        src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/OrderedPublishTests.cs
git commit -m "feat: publish ordered slots in the interceptor finally with rent-once and consume-once"
```

---

### Task 8: The drain with deliver-before-advance and cancellable waits

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Change/ExclusiveDrainGate.cs`
- Create: `src/Namotion.Interceptor.Tracking/Change/OrderedSubscription.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/OrderedDrainTests.cs`

The wait must be event-based and cancellable, mirroring `PropertyChangeQueueSubscription.cs:20,52-102,104-120` (`ManualResetEventSlim`, token-aware wait with the reset-then-recheck dance for lost wakeups, and `Dispose` setting the signal). Otherwise a consumer blocked on a slot whose producer is stuck in a user lifecycle handler can never be shut down, and `ChangeQueueProcessor.Dispose` hangs.

- [ ] **Step 1: Write the failing tests**

```csharp
public class OrderedDrainTests
{
    [Fact]
    public async Task WhenHeadIsPending_ThenDisposeUnblocksTheWaiter()
    {
        // Arrange
        var subscription = new OrderedSubscription<int>();
        subscription.Buffer.ReserveSlot(1); // pending forever: no producer will release it

        // Act
        var pump = Task.Run(() => subscription.TryDequeue(out _, CancellationToken.None));
        subscription.Dispose();

        // Assert
        var completed = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pump, completed);
        Assert.False(await pump);
    }

    [Fact]
    public async Task WhenHeadIsPending_ThenCancellationUnblocksTheWaiter()
    {
        // Arrange
        using var subscription = new OrderedSubscription<int>();
        subscription.Buffer.ReserveSlot(1);
        using var cancellation = new CancellationTokenSource();

        // Act
        var pump = Task.Run(() => subscription.TryDequeue(out _, cancellation.Token));
        await cancellation.CancelAsync();

        // Assert
        var completed = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pump, completed);
        Assert.False(await pump);
    }

    [Fact]
    public void WhenSlotsAreReleasedOutOfOrder_ThenDeliveryStillFollowsReservationOrder()
    {
        // Arrange
        using var subscription = new OrderedSubscription<int>();
        var first = subscription.Buffer.ReserveSlot(1);
        var second = subscription.Buffer.ReserveSlot(2);

        // Act: release the SECOND slot first
        subscription.Buffer.Release(second, 200);
        Assert.False(subscription.TryDequeueImmediately(out _)); // head still pending: must not skip
        subscription.Buffer.Release(first, 100);

        // Assert
        Assert.True(subscription.TryDequeueImmediately(out var firstValue));
        Assert.Equal(100, firstValue);
        Assert.True(subscription.TryDequeueImmediately(out var secondValue));
        Assert.Equal(200, secondValue);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~OrderedDrainTests"`
Expected: FAIL, `OrderedSubscription<T>` does not exist.

- [ ] **Step 3: Implement the gate and the subscription**

`ExclusiveDrainGate`: a CAS flag plus a `ThreadPool.UnsafeQueueUserWorkItem` scheduled only when the buffer turns non-empty and no drain is in flight, following `ChangeQueueProcessor._flushGate` (`ChangeQueueProcessor.cs:29,209,304`). Zero cost while idle, one work item per burst.

`OrderedSubscription<TPayload>`: owns an `OrderedSlotBuffer<TPayload>`, a `ManualResetEventSlim`, and a one-shot disposed flag. The drain loop is exactly:

```csharp
        while (Buffer.TryPeekHead(out var index))
        {
            if (!WaitForResolution(index, cancellationToken))
            {
                return false;   // cancelled or disposed
            }

            var state = Buffer.GetState(index);
            if (state == OrderedSlotState.Released)
            {
                // Deliver BEFORE advancing: advancing makes the slot reclaimable, and a concurrent
                // producer could overwrite it while the consumer still reads the payload.
                Deliver(Buffer.GetPayload(index));
            }

            Buffer.AdvanceHead();   // clears the payload: no retained subject or boxed references
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~OrderedDrainTests"`
Expected: PASS (all three).

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/ExclusiveDrainGate.cs \
        src/Namotion.Interceptor.Tracking/Change/OrderedSubscription.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/OrderedDrainTests.cs
git commit -m "feat: add the ordered drain with deliver-before-advance and cancellable waits"
```

---

### Task 9: Required delivery parameter on all four channels

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/OrderedSubscribeValidationTests.cs`

- [ ] **Step 1: Write the failing validation tests**

```csharp
    [Fact]
    public void WhenSubscribingOrderedOnAttachedContextWithoutTracking_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();   // no PropertyChangeInterceptor
        var person = new Person(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => person.SubscribeToProperty(
            p => p.FirstName, (in SubjectPropertyChange _) => { }, PropertyChangeDelivery.Ordered));
    }

    [Fact]
    public void WhenSubscribingOrderedBeforeAttach_ThenAllowedAndDormantUntilAttached()
    {
        // Arrange
        var person = new Person();   // no context yet

        // Act
        using var subscription = person.SubscribeToProperty(
            p => p.FirstName, (in SubjectPropertyChange _) => { }, PropertyChangeDelivery.Ordered);

        // Assert: no throw, and no delivery while unattached
        person.FirstName = "a";
        Assert.NotNull(subscription);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~OrderedSubscribeValidationTests"`
Expected: FAIL to compile (no delivery parameter yet).

- [ ] **Step 3: Add the required parameter and validation**

Every entry point gains a required `PropertyChangeDelivery delivery` parameter: `Subscribe(observer|callback)`, `SubscribeToProperty(selector, observer|callback)`, `GetPropertyChangeObservable()`, `CreatePropertyChangeQueueSubscription()`, plus the new `GetPropertyChangesAsync(delivery, cancellationToken)`. `Ordered` throws `InvalidOperationException` when the subject or context is attached and the aggregated context set has no `PropertyChangeInterceptor`, and when `subject.Context is not InterceptorExecutor`.

- [ ] **Step 4: Update every in-repo call site**

Run: `dotnet build src/Namotion.Interceptor.slnx 2>&1 | grep -c "error CS"` and fix each until zero. Every existing call site takes `PropertyChangeDelivery.Immediate` (today's behavior), so the migration is mechanical.

- [ ] **Step 5: Accept the API snapshot**

Run the `PublicApi` test, read the diff (it must contain only the enum, the new parameters, and `GetPropertyChangesAsync`), then copy `.received.txt` over `.verified.txt`.

- [ ] **Step 6: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A src/Namotion.Interceptor.Tracking src/Namotion.Interceptor.Tracking.Tests
git commit -m "feat!: require an explicit PropertyChangeDelivery on every subscription entry point"
```

---

### Task 10: `ChangeQueueProcessor` mode wiring

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs:88`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

`bufferTime = 0` subscribes `Ordered` (exactly-once, commit order); `bufferTime > 0` stays `Immediate` so connector contexts remain unarmed and keep the cheap queue path, relying on the Phase 1 revision dedup.

- [ ] **Step 1: Write the failing test** asserting that a processor constructed with `bufferTime: TimeSpan.Zero` receives changes in commit order under concurrent writers, and that one with `bufferTime: 8ms` leaves the context unarmed (assert `executor.OrderedPropertyListenerCount == 0` and that the registry has no subscriptions).

- [ ] **Step 2: Run it and confirm it fails.**

- [ ] **Step 3: Wire the mode**

```csharp
            _subscription = context.CreatePropertyChangeQueueSubscription(
                _bufferTime > TimeSpan.Zero
                    ? PropertyChangeDelivery.Immediate   // dedup mode: revision-based convergence
                    : PropertyChangeDelivery.Ordered);   // no buffering: exactly-once, commit order
```

- [ ] **Step 4: Run the connector suite.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs \
        src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "feat: back the unbuffered ChangeQueueProcessor with ordered delivery"
```

---

### Task 11: Concurrency and lifetime test suite

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Change/OrderedDeliveryConcurrencyTests.cs`

Each test below targets a specific failure mode the reviews identified. Use `AsyncTestHelpers.WaitUntilAsync` or `CountdownEvent`, never `Task.Delay`.

- [ ] **Step 1: Write the suite**

1. **Commit order under concurrent writers**: many threads write one subject; assert every committed write is delivered exactly once and revisions are strictly increasing in delivery order.
2. **Boundary, no stall ever**: subscribe repeatedly during a write storm; assert every write committed after `Subscribe` returned is delivered, delivered prefixes have no observable gaps, and the drain never stalls.
3. **Record-pool balance at aggregation depth 2+**: aggregate two contexts, run N writes, assert rents equal returns (expose counters behind `InternalsVisibleTo`). This is the test that catches the double-rent leak, which the reservation-count and publish-count assertions cannot see.
4. **Two registries**: two aggregated subscribing contexts; assert a write reserves into both.
5. **Aggregation publish-once**: assert exactly one reservation set and exactly one publish per write at depth 2+.
6. **Torn-read stress**: concurrent producers recycling slots while a consumer delivers; assert payload integrity and, with `WeakReference` in the #390 style, that advanced slots retain nothing.
7. **Veto and no-op**: neither reserves a slot.
8. **Transactions**: capture reserves nothing; commit replay delivers in replay order; origin filtering still suppresses self-echoes.
9. **Dispose and detach**: per-subscription state is evicted, no leak; `Dispose` unblocks a pending-head waiter.
10. **Stuck producer**: a lifecycle handler blocks; assert the drain parks and releases on resolve, and that cancellation still works throughout.

- [ ] **Step 2: Run the suite.** Expected: PASS. Any flake is a design bug, not a test bug: report it rather than adding a retry.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Change/OrderedDeliveryConcurrencyTests.cs
git commit -m "test: add the ordered delivery concurrency and lifetime suite"
```

---

### Task 12: Benchmark gates 2-7 and 10

**Files:**
- Modify: `src/Namotion.Interceptor.Benchmark/PropertyChangeSubscriptionsBenchmark.cs`
- Create: `src/Namotion.Interceptor.Benchmark/OrderedDeliveryBenchmark.cs`

- [ ] **Step 1: Add an `Ordered` counterpart** for each of the nine existing `Write*` variants.

- [ ] **Step 2: Add the usage-profile benchmark** (gate 3): 100 ordered per-property subscriptions across a graph; measure writes to a subject with no ordered subs, to an observed subject's unobserved property, and to an observed property.

- [ ] **Step 3: Add the contention matrix** (gate 5): {same subject, distinct subjects} x {unarmed, 1 ordered sub, 4 ordered subs}.

- [ ] **Step 4: Add latency and throughput** (gate 6) including the held-head case, and drain scheduling under bursty versus sustained load (gate 7).

- [ ] **Step 5: Run every gate on a pinned CPU**

```bash
pwsh scripts/benchmark.ps1 -Stash
```

Acceptance, from the spec: gate 1 (unarmed) shows no measurable regression; gate 2 shows 0 B steady state on primitive-valued writes, including pool traffic and slot clearing; gate 3 shows unobserved subjects paying only the count load. **A gate 1 regression means the design changes** (amend Phase 1's counter home or context stamp), not that the number is accepted.

- [ ] **Step 6: Record raw output in both PR descriptions and update the cost column** in `docs/tracking.md` from measurements.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Benchmark docs/tracking.md
git commit -m "benchmarks: add the ordered delivery gates and record measured costs"
```

---

## Self-Review

**Spec coverage:** prerequisites (Tasks 1-2); slot buffer with non-generic reservation surface (3); pooled record with progress-count atomicity (4); registry, core-owned per-property index, interlocked gate with trailing fence (5); reservation before the store in both terminals, gated on marker and executor (6); rent-once, consume-once, single shared payload, cancel on veto and on getter-throw (7); drain with deliver-before-advance, clear-on-advance, cancellable event-based wait, on-demand gate (8); required enum on all four channels plus validation and the pre-attach dormancy rule (9); processor modes (10); the full test list from the spec (11); gates 2-7 and 10 (12).

**Deliberately deferred:** the exclusive-drain extraction shared with #381's `PathDeliveryQueue` (Task 8 creates the primitive in a form that can absorb it, but the #381 migration is a separate PR), and the cross-flush convergence follow-up.

**Type consistency:** `OrderedSlotBufferBase.ReserveSlot(long)`/`Cancel(int)` are the only members the terminal uses (Tasks 3, 6); `OrderedSlotBuffer<TPayload>` adds `Release/GetState/GetRevision/GetPayload/TryPeekHead/AdvanceHead` used in Tasks 3 and 8; `OrderedReservationRecord.Track/CancelAll/Reset/Count/this[int]` are consistent across Tasks 4, 6, and 7; `OrderedPropertyIndex.Install/Remove/TryGet` across Tasks 5 and 6; `PropertyWriteContext.PublisherPresent`/`ReservationRecord` written in Task 6 and read in Task 7.

**Known blocker for Task 6, Step 4:** the registry capture depends on Task 2's findings, specifically whether `WithPropertyChangeSubscriptions()` registers the registry at configuration time. If it does not, Task 2 must fix that before Task 6 begins, or a chain compiled before the first subscribe will never see the registry.
