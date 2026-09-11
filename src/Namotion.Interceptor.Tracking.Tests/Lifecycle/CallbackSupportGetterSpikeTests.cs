using System.Collections.Concurrent;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackSupportGetterSpikeTests
{
    [Fact]
    public void WhenBoxedStructGetterWritesStableValue_ThenAttachTerminatesAndEdgesAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var target = new CallbackSpikeNode();
        root.Payload = new CallbackSpikeNode { Child = target };
        var stable = new ArraySegment<CallbackSpikeNode>([target]);
        var wrapper = new CallbackSpikeSegmentWrapper { Children = stable };
        var reads = 0;
        wrapper.OnRead = () =>
        {
            if (ReferenceEquals(root.Payload, wrapper) && wrapper.GetReferenceCount() == 1)
            {
                if (++reads > 16) throw new InvalidOperationException("unbounded getter retry");
                wrapper.Children = stable;
            }
        };

        // Act
        var exception = Record.Exception(() => root.Payload = wrapper);
        wrapper.OnRead = null;

        // Assert
        Assert.Null(exception);
        Assert.True(reads > 0);
        Assert.Same(wrapper, root.Payload);
        Assert.Same(context, wrapper.TryGetContext());
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, target.GetReferenceCount());

        root.Payload = null;
        Assert.Null(wrapper.TryGetContext());
        Assert.Null(target.TryGetContext());
        Assert.Equal(0, wrapper.GetReferenceCount());
        Assert.Equal(0, target.GetReferenceCount());
    }

    [Fact]
    public void WhenGetterReplacesOwnerPropertyDuringAttach_ThenFinalGraphMatchesTheLatestWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode(context);
        var target = new CallbackSpikeNode();
        var wrapper = new CallbackSpikeSegmentWrapper { Children = new([target]) };
        var replacement = new CallbackSpikeNode();
        wrapper.OnRead = () =>
        {
            if (ReferenceEquals(root.Payload, wrapper))
            {
                wrapper.OnRead = null;
                root.Payload = replacement;
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(wrapper.TryGetContext());
        Assert.Null(target.TryGetContext());
    }
}

[InterceptorSubject]
public partial class CallbackSpikeNode
{
    public partial CallbackSpikeNode? Child { get; set; }
    public partial object? Payload { get; set; }
}

public sealed class CallbackSpikeSegmentWrapper : IInterceptorSubject
{
    private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
        new Dictionary<string, SubjectPropertyMetadata>
        {
            [nameof(Children)] = new(nameof(Children), typeof(ArraySegment<CallbackSpikeNode>), [],
                static subject => ((CallbackSpikeSegmentWrapper)subject).ReadChildren(),
                static (subject, value) => ((CallbackSpikeSegmentWrapper)subject).Children = (ArraySegment<CallbackSpikeNode>)value!,
                isIntercepted: true, isDynamic: false)
        };
    private IInterceptorExecutor? _executor;
    private ArraySegment<CallbackSpikeNode> _children = new([]);
    public Action? OnRead { get; set; }
    public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
    public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
    public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;
    public ArraySegment<CallbackSpikeNode> Children
    {
        get => Executor.GetPropertyValue(nameof(Children), static subject => ((CallbackSpikeSegmentWrapper)subject).ReadChildren());
        set => Executor.SetPropertyValue(nameof(Children), value, _children,
            static (subject, newValue) => ((CallbackSpikeSegmentWrapper)subject)._children = newValue);
    }
    private ArraySegment<CallbackSpikeNode> ReadChildren() { OnRead?.Invoke(); return _children; }
    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
}
