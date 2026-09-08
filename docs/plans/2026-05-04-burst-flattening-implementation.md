# Burst Flattening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add adaptive burst flattening to `ChangeQueueProcessor` so it predictively caps flush batches, carries over excess, and drops oldest under sustained overload.

**Architecture:** Single adaptive bounded buffer. Dual EMA learns per-item write cost. Each tick: drain queue, merge with carry-over, dedup full combined set, send up to `maxItems`, carry over rest. Drop oldest when carry-over exceeds cap. Carry-over buffer is a separate ArrayPool rental, only allocated on first carry-over event.

**Tech Stack:** .NET 9.0, C# 13, ArrayPool, Stopwatch, xUnit, [InternalsVisibleTo] (already configured)

**Design document:** `docs/plans/2026-02-19-burst-flattening-design.md`

**Branch:** `feature/burst-flattening` (worktree at `.worktrees/burst-flattening/`)

---

### Task 1: Add ChangeQueueProcessorOptions and BurstFlatteningMode

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessorOptions.cs`
- Create: `src/Namotion.Interceptor.Connectors/BurstFlatteningMode.cs`

**Step 1: Create BurstFlatteningMode enum**

Create `src/Namotion.Interceptor.Connectors/BurstFlatteningMode.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Controls how the <see cref="ChangeQueueProcessor"/> handles bursts
/// of property changes.
/// </summary>
public enum BurstFlatteningMode
{
    /// <summary>
    /// No burst flattening. Every flush tick sends all deduplicated changes
    /// to the write handler. This is the default and matches the original behavior.
    /// </summary>
    None,

    /// <summary>
    /// Enables adaptive burst flattening: learns per-item write cost via dual EMA,
    /// predictively caps batches to stay within time budget, carries over excess
    /// to re-deduplicate with next tick's arrivals, and drops oldest carry-over
    /// entries when the cap is exceeded.
    /// </summary>
    Adaptive
}
```

**Step 2: Create ChangeQueueProcessorOptions class**

Create `src/Namotion.Interceptor.Connectors/ChangeQueueProcessorOptions.cs`:

```csharp
using System;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Configuration options for <see cref="ChangeQueueProcessor"/>.
/// </summary>
public class ChangeQueueProcessorOptions
{
    /// <summary>
    /// Time interval between flush ticks. Each tick, queued changes are
    /// deduplicated and sent to the write handler.
    /// Default: 8ms.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Controls adaptive burst flattening behavior.
    /// Requires <see cref="BufferTime"/> greater than <see cref="TimeSpan.Zero"/>.
    /// Default: <see cref="BurstFlatteningMode.None"/>.
    /// </summary>
    public BurstFlatteningMode BurstFlattening { get; init; } = BurstFlatteningMode.None;

    /// <summary>
    /// Fraction of <see cref="BufferTime"/> used as the write duration budget.
    /// Lower values leave more headroom but reduce throughput.
    /// Default: 0.8 (80% of BufferTime).
    /// </summary>
    public double WriteTimeBudgetRatio { get; init; } = 0.8;

    /// <summary>
    /// Maximum number of changes held in carry-over before oldest entries
    /// are evicted. Default: 10000.
    /// </summary>
    public int MaxCarryOverCount { get; init; } = 10_000;

    internal void Validate()
    {
        if (BurstFlattening == BurstFlatteningMode.Adaptive && BufferTime <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "BurstFlattening.Adaptive requires BufferTime > TimeSpan.Zero.",
                nameof(BufferTime));
        }

        if (WriteTimeBudgetRatio is <= 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WriteTimeBudgetRatio),
                WriteTimeBudgetRatio,
                "WriteTimeBudgetRatio must be between 0 (exclusive) and 1 (inclusive).");
        }

        if (MaxCarryOverCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCarryOverCount),
                MaxCarryOverCount,
                "MaxCarryOverCount must be positive.");
        }
    }
}
```

**Step 3: Verify it builds**

Run: `dotnet build src/Namotion.Interceptor.Connectors`

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessorOptions.cs src/Namotion.Interceptor.Connectors/BurstFlatteningMode.cs
git commit -m "Add ChangeQueueProcessorOptions and BurstFlatteningMode"
```

---

### Task 2: Update ChangeQueueProcessor Constructor to Accept Options

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs:59-83`

**Step 1: Write failing test for validation**

Add to `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`:

```csharp
[Fact]
public void WhenAdaptiveWithZeroBufferTime_ThenThrows()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var options = new ChangeQueueProcessorOptions
    {
        BufferTime = TimeSpan.Zero,
        BurstFlattening = BurstFlatteningMode.Adaptive
    };

    // Act & Assert
    Assert.Throws<ArgumentException>(() => new ChangeQueueProcessor(
        source: null,
        context: context,
        propertyFilter: _ => true,
        writeHandler: (_, _) => ValueTask.CompletedTask,
        options: options,
        logger: NullLogger.Instance));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "WhenAdaptiveWithZeroBufferTime_ThenThrows"`
Expected: Compilation error (constructor signature doesn't match yet)

**Step 3: Change constructor to accept ChangeQueueProcessorOptions**

In `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`, replace the constructor (lines 59-83):

- Change the `_bufferTime` field to be derived from `_options.BufferTime`
- Add `private readonly ChangeQueueProcessorOptions _options;` field
- Replace `TimeSpan? bufferTime` parameter with `ChangeQueueProcessorOptions options`
- Call `options.Validate()` in the constructor
- Set `_bufferTime = options.BufferTime;`

The constructor becomes:

```csharp
public ChangeQueueProcessor(
    object? source,
    IInterceptorSubjectContext context,
    Func<PropertyReference, bool> propertyFilter,
    Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
    ChangeQueueProcessorOptions options,
    ILogger logger)
{
    options.Validate();

    _source = source;
    _propertyFilter = propertyFilter;
    _writeHandler = writeHandler;
    _logger = logger;
    _options = options;
    _bufferTime = options.BufferTime;

    try
    {
        _subscription = context.CreatePropertyChangeQueueSubscription();
    }
    catch
    {
        ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
        _flushDedupedBuffer = null!;
        throw;
    }
}
```

**Step 4: Fix existing tests**

All existing tests pass `bufferTime: TimeSpan.FromMilliseconds(50)`. Update them to pass `options: new ChangeQueueProcessorOptions { BufferTime = TimeSpan.FromMilliseconds(50) }`. There are 6 test methods to update.

**Step 5: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass including the new validation test.

**Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Update ChangeQueueProcessor constructor to accept options"
```

---

### Task 3: Add Diagnostic Properties and Throughput EMA

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`

**Step 1: Write failing test for diagnostic counters**

Add to test file:

```csharp
[Fact]
public async Task WhenChangesAreFlushed_ThenDiagnosticCountersAreCorrect()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var subject = new Person(context);
    var options = new ChangeQueueProcessorOptions { BufferTime = TimeSpan.FromMilliseconds(50) };

    var processor = new ChangeQueueProcessor(
        source: null, context: context,
        propertyFilter: _ => true,
        writeHandler: (_, _) => ValueTask.CompletedTask,
        options: options,
        logger: NullLogger.Instance);

    // Act
    var property = new PropertyReference(subject, nameof(Person.FirstName));
    EnqueueChange(processor, property, null, "Value1");
    EnqueueChange(processor, property, "Value1", "Value2");

    await TriggerFlushAsync(processor);
    processor.Dispose();

    // Assert
    Assert.Equal(1, processor.TotalFlushedChanges);
    Assert.Equal(2, processor.TotalReceivedChanges);
    Assert.Equal(0, processor.TotalDroppedChanges);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "WhenChangesAreFlushed_ThenDiagnosticCountersAreCorrect"`
Expected: Compilation error (properties don't exist)

**Step 3: Add diagnostic fields and properties**

Add to `ChangeQueueProcessor.cs`:

Fields (after the existing field declarations):

```csharp
private long _totalFlushedChanges;
private long _totalReceivedChanges;
private long _totalDroppedChanges;
private double _flushedThroughput;
private double _receivedThroughput;
private long _lastFlushTimestamp;
```

Public diagnostic properties:

```csharp
public long TotalFlushedChanges => Volatile.Read(ref _totalFlushedChanges);
public long TotalReceivedChanges => Volatile.Read(ref _totalReceivedChanges);
public long TotalDroppedChanges => Volatile.Read(ref _totalDroppedChanges);
public double FlushedThroughputPerSecond => _flushedThroughput;
public double ReceivedThroughputPerSecond => _receivedThroughput;
```

In `TryFlushAsync`, after draining the queue into `_flushChanges`:

```csharp
Interlocked.Add(ref _totalReceivedChanges, _flushChanges.Count);
```

After the write handler call:

```csharp
Interlocked.Add(ref _totalFlushedChanges, _flushDedupedCount);
```

Add throughput EMA calculation after each write handler call:

```csharp
if (_lastFlushTimestamp != 0)
{
    var elapsed = Stopwatch.GetElapsedTime(_lastFlushTimestamp);
    if (elapsed.TotalSeconds > 0)
    {
        var ticksPerSecond = _bufferTime.TotalSeconds > 0
            ? 1.0 / _bufferTime.TotalSeconds
            : 125.0;
        var alpha = 2.0 / (ticksPerSecond + 1.0);

        var instantFlushed = _flushDedupedCount / elapsed.TotalSeconds;
        var instantReceived = _flushChanges.Count / elapsed.TotalSeconds;
        _flushedThroughput = alpha * instantFlushed + (1 - alpha) * _flushedThroughput;
        _receivedThroughput = alpha * instantReceived + (1 - alpha) * _receivedThroughput;
    }
}
_lastFlushTimestamp = Stopwatch.GetTimestamp();
```

Initialize `_lastFlushTimestamp` at the start of the constructor.

**Step 4: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Add diagnostic properties and throughput EMA to ChangeQueueProcessor"
```

---

### Task 4: Add Dual EMA Per-Item Cost Learning

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`

**Step 1: Write failing test for EMA convergence**

```csharp
[Fact]
public async Task WhenMultipleFlushesWithAdaptive_ThenPerItemCostConverges()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var subject = new Person(context);
    var options = new ChangeQueueProcessorOptions
    {
        BufferTime = TimeSpan.FromMilliseconds(50),
        BurstFlattening = BurstFlatteningMode.Adaptive
    };

    var processor = new ChangeQueueProcessor(
        source: null, context: context,
        propertyFilter: _ => true,
        writeHandler: async (changes, _) =>
        {
            await Task.Delay(changes.Length); // ~1ms per item
        },
        options: options,
        logger: NullLogger.Instance);

    // Act - flush several batches to let EMA converge
    for (var i = 0; i < 10; i++)
    {
        var property = new PropertyReference(subject, nameof(Person.FirstName));
        EnqueueChange(processor, property, $"V{i}", $"V{i + 1}");
        await TriggerFlushAsync(processor);
    }

    processor.Dispose();

    // Assert - per-item cost should be > 0 after calibration
    Assert.True(processor.PerItemWriteCostMs > 0);
    Assert.True(processor.CurrentMaxItemsPerFlush > 0);
}
```

**Step 2: Run test to verify it fails**

Expected: Compilation error (`PerItemWriteCostMs`, `CurrentMaxItemsPerFlush` don't exist)

**Step 3: Add dual EMA fields and burst flattening diagnostic properties**

Add fields:

```csharp
private double _durationEma;
private double _itemsEma;
private bool _emaInitialized;
```

Add public diagnostic properties:

```csharp
public double PerItemWriteCostMs => _itemsEma > 0 ? _durationEma / _itemsEma : 0;

public int CurrentMaxItemsPerFlush
{
    get
    {
        var perItemCost = PerItemWriteCostMs;
        if (perItemCost <= 0) return int.MaxValue;
        var budget = _options.BufferTime.TotalMilliseconds * _options.WriteTimeBudgetRatio;
        return Math.Max(1, (int)(budget / perItemCost));
    }
}

public int CurrentCarryOverCount { get; private set; }
```

Add internal EMA update method (called after each write handler call when Adaptive):

```csharp
private void UpdateEma(double writeDurationMs, int itemsSent)
{
    if (!_emaInitialized)
    {
        _durationEma = writeDurationMs;
        _itemsEma = Math.Max(1.0, itemsSent);
        _emaInitialized = true;
        return;
    }

    var ticksPerSecond = 1000.0 / _options.BufferTime.TotalMilliseconds;
    var alpha = 2.0 / (ticksPerSecond + 1.0);

    _durationEma = alpha * writeDurationMs + (1 - alpha) * _durationEma;
    _itemsEma = alpha * itemsSent + (1 - alpha) * _itemsEma;
    _itemsEma = Math.Max(1.0, _itemsEma);
}
```

In `TryFlushAsync`, when `_options.BurstFlattening == BurstFlatteningMode.Adaptive`, measure write duration with `Stopwatch` and call `UpdateEma`:

```csharp
var writeStart = Stopwatch.GetTimestamp();
await _writeHandler(...).ConfigureAwait(false);
var writeDurationMs = Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds;
UpdateEma(writeDurationMs, sentCount);
```

**Step 4: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Add dual EMA per-item cost learning for burst flattening"
```

---

### Task 5: Implement Predictive Batch Cap and Carry-Over

This is the core task. The flush method needs to: cap the batch at `maxItems`, store excess in a carry-over buffer, and merge carry-over with new arrivals on each tick.

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`

**Step 1: Write failing test for carry-over**

```csharp
[Fact]
public async Task WhenBatchExceedsMaxItems_ThenExcessIsCarriedOver()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var subject = new Person(context);
    var writtenCounts = new List<int>();

    var options = new ChangeQueueProcessorOptions
    {
        BufferTime = TimeSpan.FromMilliseconds(50),
        BurstFlattening = BurstFlatteningMode.Adaptive,
        WriteTimeBudgetRatio = 0.8
    };

    var processor = new ChangeQueueProcessor(
        source: null, context: context,
        propertyFilter: _ => true,
        writeHandler: async (changes, _) =>
        {
            writtenCounts.Add(changes.Length);
            await Task.Delay(changes.Length * 5); // 5ms per item to make cost high
        },
        options: options,
        logger: NullLogger.Instance);

    // Act - calibration tick (sends everything)
    EnqueueChange(processor, new PropertyReference(subject, nameof(Person.FirstName)), null, "Cal");
    await TriggerFlushAsync(processor);

    // Now enqueue many distinct changes to exceed maxItems
    // After calibration, per-item cost ~5ms, budget = 50ms * 0.8 = 40ms, maxItems = 8
    for (var i = 0; i < 20; i++)
    {
        EnqueueChange(processor, new PropertyReference(subject, $"Prop{i}"), null, $"V{i}");
    }
    await TriggerFlushAsync(processor);

    // Assert - second flush should have been capped, carry-over should exist
    Assert.True(processor.CurrentCarryOverCount > 0);
    Assert.True(writtenCounts[1] < 20);

    processor.Dispose();
}
```

**Step 2: Write failing test for carry-over merge and dedup**

```csharp
[Fact]
public async Task WhenCarryOverExists_ThenNewArrivalsForSamePropertyCollapse()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var subject = new Person(context);
    var lastWrittenChanges = new List<SubjectPropertyChange>();

    var options = new ChangeQueueProcessorOptions
    {
        BufferTime = TimeSpan.FromMilliseconds(50),
        BurstFlattening = BurstFlatteningMode.Adaptive,
        WriteTimeBudgetRatio = 0.8
    };

    var processor = new ChangeQueueProcessor(
        source: null, context: context,
        propertyFilter: _ => true,
        writeHandler: async (changes, _) =>
        {
            lastWrittenChanges.Clear();
            lastWrittenChanges.AddRange(changes.ToArray());
            await Task.Delay(changes.Length * 5);
        },
        options: options,
        logger: NullLogger.Instance);

    // Calibration tick
    EnqueueChange(processor, new PropertyReference(subject, nameof(Person.FirstName)), null, "Cal");
    await TriggerFlushAsync(processor);

    // Create carry-over by exceeding maxItems
    for (var i = 0; i < 20; i++)
    {
        EnqueueChange(processor, new PropertyReference(subject, $"Prop{i}"), null, $"V{i}");
    }
    await TriggerFlushAsync(processor);
    var carryOverAfterBurst = processor.CurrentCarryOverCount;

    // Act - enqueue changes for properties already in carry-over
    for (var i = 0; i < 10; i++)
    {
        EnqueueChange(processor, new PropertyReference(subject, $"Prop{i}"), $"V{i}", $"Updated{i}");
    }
    await TriggerFlushAsync(processor);

    // Assert - carry-over should not have grown (dedup absorbed the new arrivals)
    Assert.True(processor.CurrentCarryOverCount <= carryOverAfterBurst);

    processor.Dispose();
}
```

**Step 3: Write failing test for carry-over drain (burst recovery)**

```csharp
[Fact]
public async Task WhenBurstSubsides_ThenCarryOverDrainsToZero()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var subject = new Person(context);

    var options = new ChangeQueueProcessorOptions
    {
        BufferTime = TimeSpan.FromMilliseconds(50),
        BurstFlattening = BurstFlatteningMode.Adaptive,
        WriteTimeBudgetRatio = 0.8
    };

    var processor = new ChangeQueueProcessor(
        source: null, context: context,
        propertyFilter: _ => true,
        writeHandler: async (changes, _) =>
        {
            await Task.Delay(changes.Length * 5);
        },
        options: options,
        logger: NullLogger.Instance);

    // Calibration tick
    EnqueueChange(processor, new PropertyReference(subject, nameof(Person.FirstName)), null, "Cal");
    await TriggerFlushAsync(processor);

    // Create carry-over
    for (var i = 0; i < 20; i++)
    {
        EnqueueChange(processor, new PropertyReference(subject, $"Prop{i}"), null, $"V{i}");
    }
    await TriggerFlushAsync(processor);
    Assert.True(processor.CurrentCarryOverCount > 0);

    // Act - keep flushing without new arrivals until carry-over drains
    for (var i = 0; i < 30; i++)
    {
        await TriggerFlushAsync(processor);
        if (processor.CurrentCarryOverCount == 0) break;
    }

    // Assert
    Assert.Equal(0, processor.CurrentCarryOverCount);

    processor.Dispose();
}
```

**Step 4: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: Tests fail (carry-over not implemented)

**Step 5: Implement carry-over in TryFlushAsync**

Add carry-over fields to `ChangeQueueProcessor`:

```csharp
private SubjectPropertyChange[]? _carryOverBuffer;
private int _carryOverCount;
```

Modify `TryFlushAsync` to:

1. After draining the queue into `_flushChanges`, merge with carry-over:
   - If `_carryOverCount > 0`, prepend carry-over items to `_flushChanges`
   - Reset `_carryOverCount = 0`

2. After dedup, if Adaptive and EMA is initialized:
   - Compute `maxItems = budget / perItemCost`, minimum 1
   - If `_flushDedupedCount > maxItems`:
     - Send first `maxItems` items to write handler
     - Copy remainder to carry-over buffer (rent from ArrayPool if null)
     - Set `CurrentCarryOverCount`
   - Else: send all, `CurrentCarryOverCount = 0`

3. In the finally block:
   - Don't clear carry-over portion
   - Don't shrink dedup buffer if carry-over is active

4. In `Dispose`: return carry-over buffer to pool if rented.

**Step 6: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass including new carry-over tests.

**Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Implement predictive batch cap and carry-over for burst flattening"
```

---

### Task 6: Implement Drop-Oldest Overflow

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`

**Step 1: Write failing test for drop-oldest**

```csharp
[Fact]
public async Task WhenCarryOverExceedsMax_ThenOldestEntriesAreDropped()
{
    // Arrange
    var context = new InterceptorSubjectContext();
    context.WithRegistry();
    context.WithPropertyChangeQueue();

    var subject = new Person(context);

    var options = new ChangeQueueProcessorOptions
    {
        BufferTime = TimeSpan.FromMilliseconds(50),
        BurstFlattening = BurstFlatteningMode.Adaptive,
        WriteTimeBudgetRatio = 0.8,
        MaxCarryOverCount = 10
    };

    var processor = new ChangeQueueProcessor(
        source: null, context: context,
        propertyFilter: _ => true,
        writeHandler: async (changes, _) =>
        {
            await Task.Delay(changes.Length * 5);
        },
        options: options,
        logger: NullLogger.Instance);

    // Calibration tick
    EnqueueChange(processor, new PropertyReference(subject, nameof(Person.FirstName)), null, "Cal");
    await TriggerFlushAsync(processor);

    // Act - enqueue way more than MaxCarryOverCount distinct properties
    for (var i = 0; i < 50; i++)
    {
        EnqueueChange(processor, new PropertyReference(subject, $"Prop{i}"), null, $"V{i}");
    }
    await TriggerFlushAsync(processor);

    // Assert - carry-over should be capped at MaxCarryOverCount
    Assert.True(processor.CurrentCarryOverCount <= options.MaxCarryOverCount);
    Assert.True(processor.TotalDroppedChanges > 0);

    processor.Dispose();
}
```

**Step 2: Run test to verify it fails**

Expected: Fails (carry-over exceeds max, no dropping)

**Step 3: Add drop-oldest logic**

In `TryFlushAsync`, after computing carry-over from the excess beyond `maxItems`:

```csharp
if (_carryOverCount > _options.MaxCarryOverCount)
{
    var dropCount = _carryOverCount - _options.MaxCarryOverCount;
    Interlocked.Add(ref _totalDroppedChanges, dropCount);

    // Shift to drop oldest (beginning of buffer), keeping newest (end)
    Array.Copy(_carryOverBuffer!, dropCount, _carryOverBuffer!, 0, _options.MaxCarryOverCount);
    _carryOverCount = _options.MaxCarryOverCount;

    _logger.LogWarning(
        "Burst flattening: dropped {DropCount} oldest carry-over entries (carry-over exceeded {MaxCarryOverCount}). " +
        "The write handler cannot keep up with the mutation rate.",
        dropCount, _options.MaxCarryOverCount);
}
CurrentCarryOverCount = _carryOverCount;
```

**Step 4: Run tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Implement drop-oldest overflow for carry-over"
```

---

### Task 7: Add Remaining Unit Tests

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

**Step 1: Write and run the following tests**

Add these test methods one by one, verifying each passes:

```csharp
[Fact]
public async Task WhenFirstFlushWithAdaptive_ThenAllChangesAreSent()
{
    // Calibration tick: no prediction, send everything
    // Verify writtenChanges.Count == enqueued distinct property count
}

[Fact]
public async Task WhenLegitBurstWithinBudget_ThenNoCarryOver()
{
    // Write handler is fast enough. Burst should pass through unthrottled.
    // Verify CurrentCarryOverCount == 0 after flush.
}

[Fact]
public async Task WhenCarryOverDrains_ThenFIFOOrderIsPreserved()
{
    // Enqueue Prop0..Prop19, trigger flush (sends first maxItems).
    // Trigger another flush (no new arrivals, sends next maxItems from carry-over).
    // Verify the second batch contains the next properties in order.
}

[Fact]
public async Task WhenBurstFlatteningDisabled_ThenBehaviorIsUnchanged()
{
    // BurstFlattening = None. Enqueue 100 distinct changes.
    // Verify all are sent in one flush, no carry-over.
}

[Fact]
public async Task WhenDualEma_ThenLargeBatchesWeighMoreHeavily()
{
    // Flush 1 item (slow per-item due to overhead), then 100 items.
    // Verify PerItemWriteCostMs is closer to the large-batch cost than
    // the small-batch cost.
}

[Fact]
public async Task WhenMaxItemsComputed_ThenAlwaysAtLeastOne()
{
    // Very high per-item cost, tiny budget.
    // Verify CurrentMaxItemsPerFlush >= 1 (never stalls completely).
}

[Fact]
public void WhenNegativeWriteTimeBudgetRatio_ThenThrows()
{
    // Verify validation catches invalid ratio
}

[Fact]
public void WhenZeroMaxCarryOverCount_ThenThrows()
{
    // Verify validation catches invalid max carry-over
}
```

**Step 2: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass.

**Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Add comprehensive burst flattening unit tests"
```

---

### Task 8: Update Call Sites

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBackgroundService.cs:19-39,54-60`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttServerConfiguration.cs`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServerBackgroundService.cs:169-175`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerConfiguration.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServerBackgroundService.cs:204-207`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketServerConfiguration.cs`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs:368-373`

**Step 1: Update SubjectSourceBackgroundService**

Replace `TimeSpan? bufferTime = null` constructor parameter with `ChangeQueueProcessorOptions? options = null`.

Replace `_bufferTime` field with `_options`:
```csharp
private readonly ChangeQueueProcessorOptions _options;
```

In constructor:
```csharp
_options = options ?? new ChangeQueueProcessorOptions();
```

At the call site (line 54-60), change to:
```csharp
using var processor = new ChangeQueueProcessor(
    _source,
    _context,
    propertyReference => propertyReference.TryGetSource(out var source) && source == _source,
    WriteChangesAsync,
    _options,
    _logger);
```

**Step 2: Add BurstFlattening properties to connector configurations**

Add to `MqttServerConfiguration.cs` (after `BufferTime` property at line 61):
```csharp
/// <summary>
/// Controls adaptive burst flattening for the change queue processor.
/// Default: None (disabled).
/// </summary>
public BurstFlatteningMode BurstFlattening { get; init; } = BurstFlatteningMode.None;

/// <summary>
/// Fraction of BufferTime used as write duration budget when burst flattening is enabled.
/// Default: 0.8.
/// </summary>
public double WriteTimeBudgetRatio { get; init; } = 0.8;

/// <summary>
/// Maximum carry-over before oldest entries are dropped when burst flattening is enabled.
/// Default: 10000.
/// </summary>
public int MaxCarryOverCount { get; init; } = 10_000;
```

Add the same three properties to `OpcUaServerConfiguration.cs` (after `BufferTime` at line 54) and `WebSocketServerConfiguration.cs` (after `BufferTime` at line 31).

**Step 3: Update MqttSubjectServerBackgroundService call site**

At lines 169-175, change `_configuration.BufferTime` to:
```csharp
using var changeQueueProcessor = new ChangeQueueProcessor(
    source: this,
    _context,
    propertyFilter: IsPropertyIncluded,
    writeHandler: WriteChangesAsync,
    new ChangeQueueProcessorOptions
    {
        BufferTime = _configuration.BufferTime,
        BurstFlattening = _configuration.BurstFlattening,
        WriteTimeBudgetRatio = _configuration.WriteTimeBudgetRatio,
        MaxCarryOverCount = _configuration.MaxCarryOverCount
    },
    _logger);
```

**Step 4: Update OpcUaSubjectServerBackgroundService call site**

At lines 204-207, same pattern:
```csharp
using var changeQueueProcessor = new ChangeQueueProcessor(
    source: this, _context,
    propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
    new ChangeQueueProcessorOptions
    {
        BufferTime = _configuration.BufferTime ?? TimeSpan.FromMilliseconds(8),
        BurstFlattening = _configuration.BurstFlattening,
        WriteTimeBudgetRatio = _configuration.WriteTimeBudgetRatio,
        MaxCarryOverCount = _configuration.MaxCarryOverCount
    },
    _logger);
```

Note: OPC UA config has `TimeSpan?` for BufferTime. The `?? TimeSpan.FromMilliseconds(8)` preserves existing default.

**Step 5: Update WebSocketSubjectHandler call site**

At lines 368-373, same pattern:
```csharp
public ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger) =>
    new(source: this, Context,
        propertyFilter: propertyReference =>
            propertyReference.TryGetRegisteredProperty() is { } property &&
            (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),
        writeHandler: BroadcastChangesAsync,
        new ChangeQueueProcessorOptions
        {
            BufferTime = _configuration.BufferTime,
            BurstFlattening = _configuration.BurstFlattening,
            WriteTimeBudgetRatio = _configuration.WriteTimeBudgetRatio,
            MaxCarryOverCount = _configuration.MaxCarryOverCount
        },
        logger);
```

**Step 6: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeds with 0 errors.

**Step 7: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All pass.

**Step 8: Commit**

```bash
git add -A
git commit -m "Update all call sites to use ChangeQueueProcessorOptions"
```

---

### Task 9: Replace Reflection-Based Test Helpers with Internal Access

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

**Step 1: Add internal test seams to ChangeQueueProcessor**

Add to `ChangeQueueProcessor.cs`:

```csharp
internal ConcurrentQueue<SubjectPropertyChange> Changes => _changes;

internal ValueTask TryFlushInternalAsync(CancellationToken cancellationToken) =>
    TryFlushAsync(cancellationToken);
```

**Step 2: Replace reflection helpers in tests**

Replace `EnqueueChange` helper:
```csharp
private static void EnqueueChange(
    ChangeQueueProcessor processor,
    PropertyReference property,
    string? oldValue,
    string? newValue,
    object? source = null)
{
    var change = SubjectPropertyChange.Create(
        property, source, DateTimeOffset.UtcNow,
        null, oldValue, newValue);
    processor.Changes.Enqueue(change);
}
```

Replace `TriggerFlushAsync` helper:
```csharp
private static async Task TriggerFlushAsync(ChangeQueueProcessor processor)
{
    await processor.TryFlushInternalAsync(CancellationToken.None);
}
```

**Step 3: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests`
Expected: All pass.

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "Replace reflection-based test helpers with internal access"
```

---

### Task 10: Update PerformanceProfiler for Burst Flattening Diagnostics

**Files:**
- Modify: `src/Namotion.Interceptor.SamplesModel/PerformanceProfiler.cs`

**Step 1: Add optional ChangeQueueProcessor parameter**

Add an optional `ChangeQueueProcessor?` parameter to the constructor:

```csharp
public PerformanceProfiler(
    IInterceptorSubjectContext context,
    string roleTitle,
    ChangeQueueProcessor? changeQueueProcessor = null)
```

Store in a field: `private readonly ChangeQueueProcessor? _changeQueueProcessor;`

**Step 2: Print burst flattening stats in PrintStats**

At the end of `PrintStats`, after the latency lines:

```csharp
if (_changeQueueProcessor is not null &&
    _changeQueueProcessor.PerItemWriteCostMs > 0)
{
    Console.WriteLine();
    Console.WriteLine("Burst Flattening");
    Console.WriteLine($"  Per-item write cost:       {_changeQueueProcessor.PerItemWriteCostMs:F2} ms");
    Console.WriteLine($"  Max items per flush:       {_changeQueueProcessor.CurrentMaxItemsPerFlush}");
    Console.WriteLine($"  Carry-over count:          {_changeQueueProcessor.CurrentCarryOverCount}");
    Console.WriteLine($"  Dropped changes:           {_changeQueueProcessor.TotalDroppedChanges}");
    Console.WriteLine($"  Flushed throughput:        {_changeQueueProcessor.FlushedThroughputPerSecond:F2} changes/s");
    Console.WriteLine($"  Received throughput:       {_changeQueueProcessor.ReceivedThroughputPerSecond:F2} changes/s");
}
```

**Step 3: Build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Succeeds. Existing callers don't pass the parameter so they're unaffected.

**Step 4: Commit**

```bash
git add src/Namotion.Interceptor.SamplesModel/PerformanceProfiler.cs
git commit -m "Add burst flattening diagnostics to PerformanceProfiler"
```

---

### Task 11: Update Public API Snapshots

**Files:**
- Modify: Verify snapshot files for Connectors library

**Step 1: Check if Connectors has public API snapshot tests**

Run: `find src -name "VerifyChecksTests.PublicApi.verified.txt" -path "*Connectors*"`

If snapshot files exist, run the tests:

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "PublicApi"`

If the test fails with a diff (expected - new public types/properties were added):
- Copy the `.received.txt` to `.verified.txt` to accept the new snapshot

**Step 2: Run full test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All pass.

**Step 3: Commit if snapshots changed**

```bash
git add "*.verified.txt"
git commit -m "Update public API snapshots for burst flattening"
```

---

### Task 12: Run Benchmarks and Verify No Regression

**Files:**
- None (read-only verification)

**Step 1: Run benchmarks**

Run: `dotnet run --project src/Namotion.Interceptor.Benchmarks -c Release`

**Step 2: Compare with baseline**

Compare against the baseline in the design doc:
- `WriteToRegistrySubjects`: baseline 2.328ms, Allocated: 0
- `WriteToSource`: baseline 1.716ms, Allocated: 0

Verify:
- No significant regression in mean time (< 5% increase)
- Allocated stays at 0 bytes (burst flattening is disabled by default)

**Step 3: Document results**

If results are within tolerance, no action needed. If regression found, investigate before proceeding.

---

### Task 13: Final Integration Verification

**Step 1: Build entire solution clean**

Run: `dotnet build src/Namotion.Interceptor.slnx --no-incremental`

**Step 2: Run all non-integration tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`

**Step 3: Verify git status is clean**

Run: `git status`
Expected: Nothing uncommitted.

**Step 4: Push**

```bash
git push
```
