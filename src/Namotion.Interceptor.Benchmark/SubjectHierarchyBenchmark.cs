using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class BenchmarkRoot
{
    public partial string RootValue { get; set; }

    public BenchmarkRoot()
    {
        RootValue = "";
    }
}

[InterceptorSubject]
public partial class BenchmarkMiddle : BenchmarkRoot
{
    public partial string MiddleValue { get; set; }

    public BenchmarkMiddle()
    {
        MiddleValue = "";
    }
}

[InterceptorSubject]
public partial class BenchmarkLeaf : BenchmarkMiddle
{
    public partial string LeafValue { get; set; }

    public BenchmarkLeaf()
    {
        LeafValue = "";
    }
}

[MemoryDiagnoser]
public class SubjectHierarchyBenchmark
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking();

    private BenchmarkRoot _root = null!;
    private BenchmarkLeaf _leaf = null!;
    private IInterceptorSubject[] _hierarchy = null!;
    private PropertyReference _baseDeclaredReference;

    [GlobalSetup]
    public void Setup()
    {
        _root = new BenchmarkRoot(_context);
        _leaf = new BenchmarkLeaf(_context);
        _hierarchy = [_root, new BenchmarkMiddle(_context), _leaf];
        _baseDeclaredReference = new PropertyReference(_leaf, nameof(BenchmarkRoot.RootValue));
    }

    [Benchmark] public string RootOnlyGet() => _root.RootValue;
    [Benchmark] public void RootOnlySet() => _root.RootValue = "x";
    [Benchmark] public string DerivedDeclaredGet() => _leaf.LeafValue;
    [Benchmark] public void DerivedDeclaredSet() => _leaf.LeafValue = "x";
    [Benchmark] public int PropertiesAccess() => ((IInterceptorSubject)_leaf).Properties.Count;
    [Benchmark] public BenchmarkLeaf ConstructThreeLevel() => new(_context);

    // The representative shape for Properties, and the reason the row above is not sufficient on its
    // own: PropertyReference.Metadata is one member in Namotion.Interceptor, so its Subject.Properties
    // read is a single call site that every subject type in the process passes through. Reading one
    // field of one static type lets the JIT devirtualize what production never can.
    [Benchmark]
    public int PropertiesAccessPolymorphic()
    {
        var total = 0;
        foreach (var subject in _hierarchy)
        {
            total += subject.Properties.Count;
        }

        return total;
    }

    // The uncached lookup itself, on a base-declared property reached from the leaf. Every intercepted
    // write pays this, because PropertyReference.Metadata resolves it on each access.
    [Benchmark] public string BaseDeclaredMetadataLookup() => _baseDeclaredReference.Metadata.Name;

    // Not a gate. This row is the one the spec's rejected alternative would change: it is here so
    // the rejection rests on a number rather than on reasoning.
    [Benchmark] public string BaseDeclaredSetThenGet()
    {
        _leaf.RootValue = "x";
        return _leaf.RootValue;
    }
}
