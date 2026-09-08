using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.InMemory.Tests;

/// <summary>
/// Root test subject with a recordable [State] numeric property and a child subject reference.
/// Used by the recording-glue test to drive the store against a real graph and the real resolver.
/// </summary>
[InterceptorSubject]
public partial class TestRoot
{
    [State]
    public partial double Temperature { get; set; }

    public partial TestChild? Child { get; set; }

    /// <summary>
    /// Second child-reference slot. Used by the move-detection test to reparent the same child subject
    /// (from <see cref="Child"/> to here) so its canonical path changes from /Child to /SecondChild.
    /// </summary>
    public partial TestChild? SecondChild { get; set; }
}

/// <summary>
/// Child test subject with two recordable [State] numeric properties, each recorded under its own
/// canonical path. The second property lets the move-detection test verify that a subject rename is
/// detected per property, so a sibling property keeps its pre-move history.
/// </summary>
[InterceptorSubject]
public partial class TestChild
{
    [State]
    public partial double Pressure { get; set; }

    [State]
    public partial double Humidity { get; set; }
}
