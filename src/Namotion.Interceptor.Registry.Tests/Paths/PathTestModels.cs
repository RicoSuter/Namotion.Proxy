using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Paths;

[InterceptorSubject]
public partial class TestContainer
{
    public partial string Name { get; set; }
    public partial Dictionary<string, TestItem> Items { get; set; }

    public TestContainer()
    {
        Items = new Dictionary<string, TestItem>();
    }
}

[InterceptorSubject]
public partial class TestItem
{
    public partial string Value { get; set; }
    public partial Dictionary<string, TestItem> Children { get; set; }

    public TestItem()
    {
        Children = new Dictionary<string, TestItem>();
    }
}

/// <summary>
/// Test model with [InlinePaths] — dictionary keys become direct path segments.
/// </summary>
[InterceptorSubject]
public partial class TestInlineContainer
{
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestInlineContainer> Children { get; set; }

    public TestInlineContainer()
    {
        Children = new Dictionary<string, TestInlineContainer>();
    }
}

/// <summary>
/// Test model whose dictionary property is declared broadly enough to hold any dictionary
/// implementation, including <see cref="System.Collections.Immutable.ImmutableDictionary{TKey,TValue}"/>.
/// </summary>
[InterceptorSubject]
public partial class TestBroadContainer
{
    public partial string Name { get; set; }
    public partial IReadOnlyDictionary<string, TestItem> Items { get; set; }

    public TestBroadContainer()
    {
        Items = new Dictionary<string, TestItem>();
    }
}
