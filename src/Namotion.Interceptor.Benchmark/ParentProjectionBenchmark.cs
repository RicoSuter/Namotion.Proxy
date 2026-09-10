using System.Reflection;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

[InterceptorSubject]
public partial class ParentProjectionNode
{
    public partial int Value { get; set; }

    public partial ParentProjectionNode? Child { get; set; }

    public partial ParentProjectionNode[]? Items { get; set; }
}

/// <summary>
/// Prices parent projection maintenance on a structural write, inactive against active, so lazy
/// activation is measured rather than asserted. Both rows toggle one single-reference edge between
/// two children that stay owned throughout via a permanent collection edge, so ownership never
/// changes and only the incoming-edge records and any published parent snapshots move. The active
/// row's setup reads GetParents() once per subject; on the current model that sets the per-subject
/// activation bit, and on the comparison base it is a plain read.
/// </summary>
/// <remarks>
/// This file must stay source-identical across both comparison arms, whose configuration APIs
/// differ: the base arm requires WithParents() for GetParents() to answer, and the other arm does
/// not have WithParents() because parents are intrinsic to the lifecycle. The setup therefore
/// invokes WithParents by reflection when it exists. That runs once per benchmark process, never
/// on a measured path.
/// </remarks>
[MemoryDiagnoser]
public class ParentProjectionBenchmark
{
    private ParentProjectionNode _inactiveRoot;
    private ParentProjectionNode _inactiveChildA;
    private ParentProjectionNode _inactiveChildB;
    private int _inactiveToggle;

    private ParentProjectionNode _activeRoot;
    private ParentProjectionNode _activeChildA;
    private ParentProjectionNode _activeChildB;
    private int _activeToggle;

    [GlobalSetup]
    public void Setup()
    {
        (_inactiveRoot, _inactiveChildA, _inactiveChildB) = CreateGraph();
        (_activeRoot, _activeChildA, _activeChildB) = CreateGraph();

        // Reading parents once is what makes this row "active": on the current model it sets the
        // per-subject activation bit so every later edge change republishes the snapshot; on the
        // base arm the handler maintains parents unconditionally and this is just a read.
        _activeRoot.GetParents();
        _activeChildA.GetParents();
        _activeChildB.GetParents();
    }

    private static (ParentProjectionNode Root, ParentProjectionNode A, ParentProjectionNode B) CreateGraph()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        // See the class remarks: present on the base arm only, where GetParents() needs it.
        // Fully qualified: inside this file's namespace, the unqualified name binds to Core's
        // class of the same name through the enclosing-namespace rule, and the lookup silently
        // misses.
        typeof(Tracking.InterceptorSubjectContextExtensions)
            .GetMethod("WithParents", BindingFlags.Public | BindingFlags.Static)?
            .Invoke(null, [context]);

        var root = new ParentProjectionNode(context);
        var childA = new ParentProjectionNode { Value = 1 };
        var childB = new ParentProjectionNode { Value = 2 };

        // The permanent collection edge keeps both children owned while the single-reference edge
        // toggles, so the rows measure edge-record and snapshot maintenance, never attach/release.
        root.Items = [childA, childB];
        root.Child = childA;
        return (root, childA, childB);
    }

    [Benchmark]
    public void EdgeToggleParentsInactive()
    {
        _inactiveToggle ^= 1;
        _inactiveRoot.Child = _inactiveToggle == 0 ? _inactiveChildA : _inactiveChildB;
    }

    [Benchmark]
    public void EdgeToggleParentsActive()
    {
        _activeToggle ^= 1;
        _activeRoot.Child = _activeToggle == 0 ? _activeChildA : _activeChildB;
    }
}
