using System.Collections.Generic;
using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// A non-<see cref="System.IEquatable{T}"/> value-type leaf (8 bytes, stored inline by
/// <c>SubjectPropertyChange</c>). Its path suppression comparison goes through
/// <c>EqualityComparer&lt;PlainPoint&gt;.Default</c>, which boxes both operands, so delivery through it is
/// measured but deliberately not allocation-gated.
/// </summary>
public struct PlainPoint
{
    public PlainPoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>
/// One self-referential node carrying a reference intermediate (<see cref="Child"/>), a subject collection
/// (<see cref="Children"/>), a subject dictionary (<see cref="ByName"/>) and a leaf of every gated and
/// measured shape. Reused for the linear reference chain (through <see cref="Child"/>), the deep mixed path,
/// and the single-segment leaf cases.
/// </summary>
[InterceptorSubject]
public partial class PathNode
{
    public PathNode()
    {
        Text = string.Empty;
        Children = [];
        ByName = new Dictionary<string, PathNode>();
    }

    public partial PathNode? Child { get; set; }

    public partial PathNode[] Children { get; set; }

    public partial Dictionary<string, PathNode> ByName { get; set; }

    public partial int Count { get; set; }

    public partial string Text { get; set; }

    public partial PlainPoint Point { get; set; }
}

/// <summary>
/// A holder whose only intermediate is a value-typed <see cref="ImmutableArray{T}"/> segment. The walk reads
/// it through the read-and-index compiled accessor rather than the boxing metadata getter, so a path through
/// it must stay allocation-free just like a reference intermediate.
/// </summary>
[InterceptorSubject]
public partial class ImmutableArrayHolder
{
    public ImmutableArrayHolder()
    {
        Items = [];
    }

    public partial ImmutableArray<PathNode> Items { get; set; }
}
