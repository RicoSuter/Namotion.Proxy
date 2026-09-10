using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[InterceptorSubject]
public partial class LifecycleNode
{
    public partial int Value { get; set; }

    public partial LifecycleNode? Child { get; set; }

    public partial LifecycleNode? Partner { get; set; }

    public partial LifecycleNode[]? Items { get; set; }
}

/// <summary>
/// Measures the lifecycle cost of structural property writes on attached subjects.
/// The structural rows toggle between prebuilt values so repeated invocation is steady
/// state: the graph never grows and no per-iteration setup distorts the short paths.
/// Each row uses its own context and root so rows do not share attach state.
/// The class setup builds subjects, so no row in this class is insulated from lifecycle
/// changes; use a ServiceOrderResolverBenchmark row as the in-run noise reference.
/// </summary>
[MemoryDiagnoser]
public class LifecycleOwnershipBenchmark
{
    private const int ScalarOperationsPerInvoke = 1024;
    private const int LargeContextBulkNodeCount = 500;
    private const int SharedEdgeBatchSize = 8;

    private LifecycleNode _unattachedNode;
    private int _unattachedValue;

    private LifecycleNode _unattachedStructuralNode;
    private LifecycleNode _unattachedStructuralChildA;
    private LifecycleNode _unattachedStructuralChildB;

    private LifecycleNode _attachedNode;
    private int _attachedValue;

    private LifecycleNode _singleReferenceRoot;
    private LifecycleNode _singleReferenceChildA;
    private LifecycleNode _singleReferenceChildB;
    private int _singleReferenceToggle;

    private LifecycleNode _uniqueCollectionRoot;
    private LifecycleNode[] _uniqueCollectionA;
    private LifecycleNode[] _uniqueCollectionB;
    private int _uniqueCollectionToggle;

    private LifecycleNode _duplicateCollectionRoot;
    private LifecycleNode[] _duplicateCollectionA;
    private LifecycleNode[] _duplicateCollectionB;
    private int _duplicateCollectionToggle;

    private LifecycleNode _reorderRoot;
    private LifecycleNode[] _reorderForward;
    private LifecycleNode[] _reorderReversed;
    private int _reorderToggle;

    private LifecycleNode _cyclicRoot;
    private LifecycleNode _cyclicEntryA;
    private LifecycleNode _cyclicEntryB;
    private int _cyclicToggle;

    private LifecycleNode _subtreeRoot;
    private LifecycleNode _subtree;

    private LifecycleNode _largeContextRoot;
    private LifecycleNode _largeContextChildA;
    private LifecycleNode _largeContextChildB;
    private int _largeContextToggle;

    private LifecycleNode _sharedChildRoot;
    private LifecycleNode _sharedChildSecondParent;
    private LifecycleNode _sharedChild;

    private LifecycleNode _sharedChildLargeContextRoot;
    private LifecycleNode _sharedChildLargeContextSecondParent;
    private LifecycleNode _sharedChildInLargeContext;

    private LifecycleNode _orphanedCycleRoot;
    private LifecycleNode _orphanedCycleEntry;

    private LifecycleNode _sharedEdgeBatchRoot;
    private LifecycleNode _sharedEdgeBatchParent;
    private LifecycleNode[] _sharedEdgeBatchChildren;

    [GlobalSetup]
    public void Setup()
    {
        _unattachedNode = new LifecycleNode();

        _unattachedStructuralNode = new LifecycleNode();
        _unattachedStructuralChildA = new LifecycleNode();
        _unattachedStructuralChildB = new LifecycleNode();

        _attachedNode = new LifecycleNode(CreateContext());

        _singleReferenceRoot = new LifecycleNode(CreateContext());
        _singleReferenceChildA = new LifecycleNode();
        _singleReferenceChildB = new LifecycleNode();

        _uniqueCollectionRoot = new LifecycleNode(CreateContext());
        _uniqueCollectionA = [new LifecycleNode(), new LifecycleNode(), new LifecycleNode(), new LifecycleNode()];
        _uniqueCollectionB = [new LifecycleNode(), new LifecycleNode(), new LifecycleNode(), new LifecycleNode()];

        _duplicateCollectionRoot = new LifecycleNode(CreateContext());
        var duplicatedChildA = new LifecycleNode();
        var duplicatedChildB = new LifecycleNode();
        _duplicateCollectionA = [duplicatedChildA, duplicatedChildA, new LifecycleNode()];
        _duplicateCollectionB = [duplicatedChildB, duplicatedChildB, new LifecycleNode()];

        _reorderRoot = new LifecycleNode(CreateContext());
        _reorderForward = [new LifecycleNode(), new LifecycleNode(), new LifecycleNode(), new LifecycleNode()];
        _reorderReversed = [_reorderForward[3], _reorderForward[2], _reorderForward[1], _reorderForward[0]];

        _cyclicRoot = new LifecycleNode(CreateContext());
        _cyclicEntryA = CreateCyclicPair();
        _cyclicEntryB = CreateCyclicPair();

        _subtreeRoot = new LifecycleNode(CreateContext());
        _subtree = CreateSubtree(3);

        _largeContextRoot = new LifecycleNode(CreateContext());
        _largeContextRoot.Items = CreateBulkGraph();
        _largeContextChildA = new LifecycleNode();
        _largeContextChildB = new LifecycleNode();

        _sharedChildRoot = new LifecycleNode(CreateContext());
        _sharedChild = new LifecycleNode();
        _sharedChildSecondParent = new LifecycleNode();
        var sharedChildFirstParent = new LifecycleNode();
        _sharedChildRoot.Child = sharedChildFirstParent;
        _sharedChildRoot.Partner = _sharedChildSecondParent;
        sharedChildFirstParent.Child = _sharedChild;
        _sharedChildSecondParent.Child = _sharedChild;

        _sharedChildLargeContextRoot = new LifecycleNode(CreateContext());
        _sharedChildLargeContextRoot.Items = CreateBulkGraph();
        _sharedChildInLargeContext = new LifecycleNode();
        _sharedChildLargeContextSecondParent = new LifecycleNode();
        var sharedChildLargeContextFirstParent = new LifecycleNode();
        _sharedChildLargeContextRoot.Child = sharedChildLargeContextFirstParent;
        _sharedChildLargeContextRoot.Partner = _sharedChildLargeContextSecondParent;
        sharedChildLargeContextFirstParent.Child = _sharedChildInLargeContext;
        _sharedChildLargeContextSecondParent.Child = _sharedChildInLargeContext;

        _orphanedCycleRoot = new LifecycleNode(CreateContext());
        _orphanedCycleEntry = CreateCyclicRing();
        _orphanedCycleRoot.Child = _orphanedCycleEntry;

        _sharedEdgeBatchRoot = new LifecycleNode(CreateContext());
        _sharedEdgeBatchChildren = new LifecycleNode[SharedEdgeBatchSize];
        var sharedEdgeBatchRetainingParents = new LifecycleNode[SharedEdgeBatchSize];
        for (var i = 0; i < SharedEdgeBatchSize; i++)
        {
            _sharedEdgeBatchChildren[i] = new LifecycleNode();
            sharedEdgeBatchRetainingParents[i] = new LifecycleNode();
            sharedEdgeBatchRetainingParents[i].Child = _sharedEdgeBatchChildren[i];
        }

        _sharedEdgeBatchRoot.Items = sharedEdgeBatchRetainingParents;
        _sharedEdgeBatchParent = new LifecycleNode();
        _sharedEdgeBatchRoot.Child = _sharedEdgeBatchParent;
        _sharedEdgeBatchParent.Items = _sharedEdgeBatchChildren;
    }

    /// <summary>
    /// Baseline write on a subject without any context; the generated setter takes its
    /// uninstrumented path. Amortized because a single set is far below the timer floor.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ScalarOperationsPerInvoke)]
    public void SetScalarUnattached()
    {
        var node = _unattachedNode;
        var value = _unattachedValue;
        for (var i = 0; i < ScalarOperationsPerInvoke; i++)
        {
            node.Value = value + i;
        }

        _unattachedValue = value + ScalarOperationsPerInvoke;
    }

    /// <summary>
    /// Structural write on a subject without any context, paired with
    /// <see cref="SetScalarUnattached"/> so the delta between the two rows shows what a
    /// subject-typed setter adds over a scalar setter on a never-attached subject.
    /// Amortized with the same operation count as the scalar rows to keep the pair
    /// directly comparable; the writes alternate between two children so every set
    /// changes the value.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ScalarOperationsPerInvoke)]
    public void SetStructuralUnattached()
    {
        var node = _unattachedStructuralNode;
        var childA = _unattachedStructuralChildA;
        var childB = _unattachedStructuralChildB;
        for (var i = 0; i < ScalarOperationsPerInvoke; i++)
        {
            node.Child = (i & 1) == 0 ? childA : childB;
        }
    }

    /// <summary>
    /// Same body as <see cref="SetScalarUnattached"/> on an attached subject, so the delta
    /// between the two rows isolates the interception cost. Values always change so the
    /// equality check never short-circuits the write.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ScalarOperationsPerInvoke)]
    public void SetScalarAttached()
    {
        var node = _attachedNode;
        var value = _attachedValue;
        for (var i = 0; i < ScalarOperationsPerInvoke; i++)
        {
            node.Value = value + i;
        }

        _attachedValue = value + ScalarOperationsPerInvoke;
    }

    [Benchmark]
    public void ReplaceSingleChildReference()
    {
        _singleReferenceRoot.Child = (_singleReferenceToggle++ & 1) == 0
            ? _singleReferenceChildA
            : _singleReferenceChildB;
    }

    [Benchmark]
    public void ReplaceCollectionUniqueChildren()
    {
        _uniqueCollectionRoot.Items = (_uniqueCollectionToggle++ & 1) == 0
            ? _uniqueCollectionA
            : _uniqueCollectionB;
    }

    /// <summary>
    /// The arrays contain the same child twice, so attach and detach must handle a subject
    /// that occurs at several indexes of one collection property.
    /// </summary>
    [Benchmark]
    public void ReplaceCollectionDuplicateChildren()
    {
        _duplicateCollectionRoot.Items = (_duplicateCollectionToggle++ & 1) == 0
            ? _duplicateCollectionA
            : _duplicateCollectionB;
    }

    /// <summary>
    /// Both arrays hold the same children in opposite order, so no subject is attached or
    /// detached and the row measures pure index reconciliation.
    /// </summary>
    [Benchmark]
    public void ReorderCollection()
    {
        _reorderRoot.Items = (_reorderToggle++ & 1) == 0
            ? _reorderForward
            : _reorderReversed;
    }

    /// <summary>
    /// Each prebuilt pair references itself through <see cref="LifecycleNode.Partner"/>, so
    /// the row measures how attach and detach handle a cycle whose only external reference
    /// is the toggled <see cref="LifecycleNode.Child"/> property.
    /// </summary>
    [Benchmark]
    public void ReplaceCyclicChildGraph()
    {
        _cyclicRoot.Child = (_cyclicToggle++ & 1) == 0
            ? _cyclicEntryA
            : _cyclicEntryB;
    }

    /// <summary>
    /// Attaches a prebuilt fifteen-subject tree and releases it again in the same invocation,
    /// measuring a full deep attach plus the removal work for a larger graph.
    /// </summary>
    [Benchmark]
    public void AttachAndReleaseSubtree()
    {
        _subtreeRoot.Child = _subtree;
        _subtreeRoot.Child = null;
    }

    /// <summary>
    /// Toggles a single child on a root that also retains a large untouched bulk graph:
    /// <see cref="LargeContextBulkNodeCount"/> nodes each holding three children, so
    /// 2000 retained subjects besides the root. Exactly one edge changes per invocation
    /// and the body never touches the bulk, so any lifecycle cost that scales with
    /// context size rather than with the removed value shows up in this row.
    /// </summary>
    [Benchmark]
    public void ReleaseSmallSubtreeFromLargeContext()
    {
        _largeContextRoot.Child = (_largeContextToggle++ & 1) == 0
            ? _largeContextChildA
            : _largeContextChildB;
    }

    /// <summary>
    /// The child is referenced by two attached parents and the body removes one of the two
    /// edges and restores it, so the child always retains an incoming edge and the lifecycle
    /// must prove the child is still held rather than release it. Catches retention-decision
    /// cost that a pure tree removal never pays. Small graph.
    /// </summary>
    [Benchmark]
    public void RemoveOneParentOfSharedChild()
    {
        _sharedChildSecondParent.Child = null;
        _sharedChildSecondParent.Child = _sharedChild;
    }

    /// <summary>
    /// Same shape as <see cref="RemoveOneParentOfSharedChild"/>, but the root additionally
    /// retains the untouched bulk graph of <see cref="LargeContextBulkNodeCount"/> nodes with
    /// three children each, so 2000 retained subjects besides the root. The two rows form a
    /// matched pair: any ratio between them is retention-decision cost that scales with
    /// context size rather than with the removed value.
    /// </summary>
    [Benchmark]
    public void RemoveOneParentOfSharedChildInLargeContext()
    {
        _sharedChildLargeContextSecondParent.Child = null;
        _sharedChildLargeContextSecondParent.Child = _sharedChildInLargeContext;
    }

    /// <summary>
    /// Detaches a three-node Partner ring whose only external reference is the root edge and
    /// re-attaches it. The two lifecycle models do different work here by design: the
    /// reference-count model leaks the orphaned cycle (a documented, snapshot-pinned
    /// limitation) while the reachability model releases and re-attaches all three nodes, so
    /// this row is a semantic comparison rather than a like-for-like one and a slowdown here
    /// is the price of a correctness fix, not a regression.
    /// </summary>
    [Benchmark]
    public void ReleaseOrphanedCycle()
    {
        _orphanedCycleRoot.Child = null;
        _orphanedCycleRoot.Child = _orphanedCycleEntry;
    }

    /// <summary>
    /// One assignment removes <see cref="SharedEdgeBatchSize"/> (8) edges to children that
    /// each keep a second attached parent, so one operation questions eight children that
    /// must all be retained while the batch keeps mutating the graph. Catches retention work
    /// that is recomputed per removed edge instead of once per assignment.
    /// </summary>
    [Benchmark]
    public void RemoveSharedEdgesInBatch()
    {
        _sharedEdgeBatchParent.Items = null;
        _sharedEdgeBatchParent.Items = _sharedEdgeBatchChildren;
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
    }

    private static LifecycleNode CreateCyclicPair()
    {
        var entry = new LifecycleNode();
        var partner = new LifecycleNode();
        entry.Partner = partner;
        partner.Partner = entry;
        return entry;
    }

    private static LifecycleNode CreateCyclicRing()
    {
        var first = new LifecycleNode();
        var second = new LifecycleNode();
        var third = new LifecycleNode();
        first.Partner = second;
        second.Partner = third;
        third.Partner = first;
        return first;
    }

    private static LifecycleNode[] CreateBulkGraph()
    {
        var nodes = new LifecycleNode[LargeContextBulkNodeCount];
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = new LifecycleNode();
            node.Items = [new LifecycleNode(), new LifecycleNode(), new LifecycleNode()];
            nodes[i] = node;
        }

        return nodes;
    }

    private static LifecycleNode CreateSubtree(int depth)
    {
        var node = new LifecycleNode();
        if (depth > 0)
        {
            node.Items = [CreateSubtree(depth - 1), CreateSubtree(depth - 1)];
        }

        return node;
    }
}
