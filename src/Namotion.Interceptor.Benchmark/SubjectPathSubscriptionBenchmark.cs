using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// Measures the path subscription hot paths against a bare per-property listener on the same leaf. The
/// MemoryDiagnoser Allocated column is the gate: delivery of an inline-sized value type, a string, or a value
/// through an <see cref="System.Collections.Immutable.ImmutableArray{T}"/> intermediate must be allocation-free
/// on the path side (no boxing of the value); a non-<see cref="IEquatable{T}"/> struct leaf boxes both operands
/// in the suppression comparison and is measured but not gated.
///
/// Each benchmark runs in its own process (BenchmarkDotNet default), set up by a targeted
/// <see cref="GlobalSetupAttribute"/>. That isolation matters: the per-property listener live count is a
/// process-wide static, so a subscription installed for one benchmark must not be visible to another. The
/// context is registered with <c>WithPropertyChangeSubscriptions()</c> only (no equality check): every write
/// reaches the segment listener even when the value does not change, which is what the hot-leaf suppressed case
/// needs (the walk is paid on every processed callback while path-level suppression drops the delivery). With
/// an equality check an unchanged write would be dropped before the listener and never pay the walk. All nodes
/// are created with the shared context so their writes dispatch without relying on context inheritance.
/// </summary>
[MemoryDiagnoser]
public class SubjectPathSubscriptionBenchmark
{
    private const int SuppressedValue = 7;
    private const int LeafValueA = 11;
    private const int LeafValueB = 22;

    private IInterceptorSubjectContext _context;
    private bool _toggle;

    private readonly string _textA = "alpha";
    private readonly string _textB = "beta";
    private readonly PlainPoint _pointA = new(1, 1);
    private readonly PlainPoint _pointB = new(2, 2);

    private readonly SubjectPathChangeCallback<int> _churnCallback = static (in SubjectPathChange<int> _) => { };

    // Delivery: path vs bare, depth 1.
    private PathNode _pathDeliverDepth1Leaf;
    private SubjectPathSubscription<int> _pathDeliverDepth1;
    private PathNode _bareDeliverDepth1Leaf;
    private IDisposable _bareDeliverDepth1;

    // Delivery: path vs bare, depth 4.
    private PathNode _pathDeliverDepth4Leaf;
    private SubjectPathSubscription<int> _pathDeliverDepth4;
    private PathNode _bareDeliverDepth4Leaf;
    private IDisposable _bareDeliverDepth4;

    // Delivery: leaf-only validation, depth 4.
    private PathNode _pathDeliverLeafOnlyDepth4Leaf;
    private SubjectPathSubscription<int> _pathDeliverLeafOnlyDepth4;

    // Hot leaf: repeated identical writes, walk paid, delivery suppressed.
    private PathNode _suppressedLeaf;
    private SubjectPathSubscription<int> _suppressedSubscription;

    // Allocation-gated shapes.
    private PathNode _stringLeaf;
    private SubjectPathSubscription<string> _stringSubscription;
    private PathNode _immutableElement;
    private SubjectPathSubscription<int> _immutableSubscription;

    // Measured but not gated: non-IEquatable struct leaf.
    private PathNode _structLeaf;
    private SubjectPathSubscription<PlainPoint> _structSubscription;

    // Structural retrack of a four-segment suffix.
    private PathNode _retrackRoot;
    private PathNode _retrackSubtreeA;
    private PathNode _retrackSubtreeB;
    private SubjectPathSubscription<int> _retrackSubscription;

    // Current reads.
    private SubjectPathSubscription<int> _currentDepth1;
    private SubjectPathSubscription<int> _currentDepth4;

    // Subscribe/dispose churn.
    private PathNode _churnRoot;

    // Deep mixed path (reference + collection + dictionary).
    private PathNode _mixedLeaf;
    private SubjectPathSubscription<int> _mixedSubscription;

    // --- Delivery: path vs bare, depth 1 -----------------------------------------------------------------

    [GlobalSetup(Target = nameof(PathDeliverDepth1))]
    public void SetupPathDeliverDepth1()
    {
        _context = CreateContext();
        _pathDeliverDepth1Leaf = new PathNode(_context);
        _pathDeliverDepth1 = _pathDeliverDepth1Leaf.SubscribeToPath(
            x => x.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark(Baseline = true)]
    public void PathDeliverDepth1()
    {
        _pathDeliverDepth1Leaf.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    [GlobalSetup(Target = nameof(BareDeliverDepth1))]
    public void SetupBareDeliverDepth1()
    {
        _context = CreateContext();
        _bareDeliverDepth1Leaf = new PathNode(_context);
        _bareDeliverDepth1 = _bareDeliverDepth1Leaf.SubscribeToProperty(
            x => x.Count, static (in SubjectPropertyChange _) => { });
    }

    [Benchmark]
    public void BareDeliverDepth1()
    {
        _bareDeliverDepth1Leaf.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    // --- Delivery: path vs bare, depth 4 -----------------------------------------------------------------

    [GlobalSetup(Target = nameof(PathDeliverDepth4))]
    public void SetupPathDeliverDepth4()
    {
        _context = CreateContext();
        var (root, leaf) = BuildChain(4);
        _pathDeliverDepth4Leaf = leaf;
        _pathDeliverDepth4 = root.SubscribeToPath(
            x => x.Child!.Child!.Child!.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathDeliverDepth4()
    {
        _pathDeliverDepth4Leaf.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    [GlobalSetup(Target = nameof(BareDeliverDepth4))]
    public void SetupBareDeliverDepth4()
    {
        _context = CreateContext();
        var (_, leaf) = BuildChain(4);
        _bareDeliverDepth4Leaf = leaf;
        _bareDeliverDepth4 = leaf.SubscribeToProperty(
            x => x.Count, static (in SubjectPropertyChange _) => { });
    }

    [Benchmark]
    public void BareDeliverDepth4()
    {
        _bareDeliverDepth4Leaf.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    // --- Delivery: leaf-only validation, depth 4 ----------------------------------------------------------

    [GlobalSetup(Target = nameof(PathDeliverLeafOnlyDepth4))]
    public void SetupPathDeliverLeafOnlyDepth4()
    {
        _context = CreateContext();
        var (root, leaf) = BuildChain(4);
        _pathDeliverLeafOnlyDepth4Leaf = leaf;
        _pathDeliverLeafOnlyDepth4 = root.SubscribeToPath(
            x => x.Child!.Child!.Child!.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.LeafOnly);
    }

    [Benchmark]
    public void PathDeliverLeafOnlyDepth4()
    {
        // Mirrors PathDeliverDepth4 but with leaf-only validation: the leaf write re-reads only the cached
        // leaf instead of walking all four segments, so the delta to PathDeliverDepth4 is the walk cost.
        _pathDeliverLeafOnlyDepth4Leaf.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    // --- Hot leaf: walk paid, delivery suppressed --------------------------------------------------------

    [GlobalSetup(Target = nameof(PathHotLeafSuppressedDepth4))]
    public void SetupPathHotLeafSuppressedDepth4()
    {
        _context = CreateContext();
        var (root, leaf) = BuildChain(4);
        leaf.Count = SuppressedValue;
        _suppressedLeaf = leaf;
        _suppressedSubscription = root.SubscribeToPath(
            x => x.Child!.Child!.Child!.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathHotLeafSuppressedDepth4()
    {
        // Same value every write: the segment listener fires and the walk runs, but path-level suppression
        // drops the delivery, so this measures the walk cost of a processed-but-not-delivered callback.
        _suppressedLeaf.Count = SuppressedValue;
    }

    // --- Allocation-gated shapes: string leaf, ImmutableArray intermediate -------------------------------

    [GlobalSetup(Target = nameof(PathDeliverStringDepth1))]
    public void SetupPathDeliverStringDepth1()
    {
        _context = CreateContext();
        _stringLeaf = new PathNode(_context);
        _stringSubscription = _stringLeaf.SubscribeToPath(
            x => x.Text, static (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathDeliverStringDepth1()
    {
        _stringLeaf.Text = _toggle ? _textA : _textB;
        _toggle = !_toggle;
    }

    [GlobalSetup(Target = nameof(PathDeliverImmutableArrayIntermediate))]
    public void SetupPathDeliverImmutableArrayIntermediate()
    {
        _context = CreateContext();
        var holder = new ImmutableArrayHolder(_context);
        _immutableElement = new PathNode(_context);
        holder.Items =
        [
            new PathNode(_context),
            new PathNode(_context),
            _immutableElement
        ];
        _immutableSubscription = holder.SubscribeToPath(
            x => x.Items[2].Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathDeliverImmutableArrayIntermediate()
    {
        _immutableElement.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    // --- Measured but not gated: non-IEquatable struct leaf ----------------------------------------------

    [GlobalSetup(Target = nameof(PathDeliverStructLeafDepth1))]
    public void SetupPathDeliverStructLeafDepth1()
    {
        _context = CreateContext();
        _structLeaf = new PathNode(_context);
        _structSubscription = _structLeaf.SubscribeToPath(
            x => x.Point, static (in SubjectPathChange<PlainPoint> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathDeliverStructLeafDepth1()
    {
        _structLeaf.Point = _toggle ? _pointA : _pointB;
        _toggle = !_toggle;
    }

    // --- Structural retrack of a four-segment suffix -----------------------------------------------------

    [GlobalSetup(Target = nameof(PathStructuralRetrackDepth4))]
    public void SetupPathStructuralRetrackDepth4()
    {
        _context = CreateContext();
        _retrackRoot = new PathNode(_context);

        var (subtreeA, leafA) = BuildChain(3);
        leafA.Count = LeafValueA;
        _retrackSubtreeA = subtreeA;

        var (subtreeB, leafB) = BuildChain(3);
        leafB.Count = LeafValueB;
        _retrackSubtreeB = subtreeB;

        _retrackRoot.Child = subtreeA;
        _retrackSubscription = _retrackRoot.SubscribeToPath(
            x => x.Child!.Child!.Child!.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathStructuralRetrackDepth4()
    {
        // Reassigning the top intermediate diverges the chain at position 0, forcing a full four-segment
        // suffix teardown and subscribe-and-read rebuild on every write.
        _retrackRoot.Child = _toggle ? _retrackSubtreeB : _retrackSubtreeA;
        _toggle = !_toggle;
    }

    // --- Current reads -----------------------------------------------------------------------------------

    [GlobalSetup(Target = nameof(CurrentReadDepth1))]
    public void SetupCurrentReadDepth1()
    {
        _context = CreateContext();
        var leaf = new PathNode(_context) { Count = LeafValueA };
        _currentDepth1 = leaf.SubscribeToPath(
            x => x.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public int CurrentReadDepth1()
    {
        return _currentDepth1.Current.GetValueOrDefault();
    }

    [GlobalSetup(Target = nameof(CurrentReadDepth4))]
    public void SetupCurrentReadDepth4()
    {
        _context = CreateContext();
        var (root, leaf) = BuildChain(4);
        leaf.Count = LeafValueA;
        _currentDepth4 = root.SubscribeToPath(
            x => x.Child!.Child!.Child!.Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public int CurrentReadDepth4()
    {
        return _currentDepth4.Current.GetValueOrDefault();
    }

    // --- Subscribe / dispose churn -----------------------------------------------------------------------

    [GlobalSetup(Target = nameof(SubscribeDisposeChurnDepth4))]
    public void SetupSubscribeDisposeChurnDepth4()
    {
        _context = CreateContext();
        var (root, _) = BuildChain(4);
        _churnRoot = root;
    }

    [Benchmark]
    public void SubscribeDisposeChurnDepth4()
    {
        using var subscription = _churnRoot.SubscribeToPath(
            x => x.Child!.Child!.Child!.Count, _churnCallback, SubjectPathValidation.Full);
    }

    // --- Deep mixed path: reference + collection + dictionary --------------------------------------------

    [GlobalSetup(Target = nameof(PathDeliverMixedDepth))]
    public void SetupPathDeliverMixedDepth()
    {
        _context = CreateContext();
        var root = new PathNode(_context);
        var reference = new PathNode(_context);
        var indexed = new PathNode(_context);
        _mixedLeaf = new PathNode(_context);

        root.Child = reference;
        reference.Children = [new PathNode(_context), indexed];
        indexed.ByName = new Dictionary<string, PathNode> { ["key"] = _mixedLeaf };

        _mixedSubscription = root.SubscribeToPath(
            x => x.Child!.Children[1].ByName["key"].Count, static (in SubjectPathChange<int> _) => { }, SubjectPathValidation.Full);
    }

    [Benchmark]
    public void PathDeliverMixedDepth()
    {
        _mixedLeaf.Count = _toggle ? LeafValueA : LeafValueB;
        _toggle = !_toggle;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pathDeliverDepth1?.Dispose();
        _bareDeliverDepth1?.Dispose();
        _pathDeliverDepth4?.Dispose();
        _bareDeliverDepth4?.Dispose();
        _pathDeliverLeafOnlyDepth4?.Dispose();
        _suppressedSubscription?.Dispose();
        _stringSubscription?.Dispose();
        _immutableSubscription?.Dispose();
        _structSubscription?.Dispose();
        _retrackSubscription?.Dispose();
        _currentDepth1?.Dispose();
        _currentDepth4?.Dispose();
        _mixedSubscription?.Dispose();
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithPropertyChangeSubscriptions();
    }

    // Builds a linear reference chain of nodeCount nodes (root.Child.Child...), returning the root and the
    // innermost leaf-holding node. A path from the root through nodeCount-1 Child segments plus a leaf
    // segment has nodeCount segments.
    private (PathNode Root, PathNode Leaf) BuildChain(int nodeCount)
    {
        var root = new PathNode(_context);
        var leaf = root;
        for (var index = 1; index < nodeCount; index++)
        {
            var next = new PathNode(_context);
            leaf.Child = next;
            leaf = next;
        }

        return (root, leaf);
    }
}
