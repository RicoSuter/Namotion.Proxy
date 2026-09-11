using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackActiveSeedGetterSpikeTests
{
    [Fact]
    public void WhenSeedingGetterWritesANormalizedSubset_ThenItsReturnedValueDeterminesOwnership()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var stale = new CallbackSpikeNode();
        var accepted = new CallbackSpikeNode();
        var excluded = new CallbackSpikeNode();
        var root = new ActiveSeedGetterRoot { Children = new([stale]) };
        root.Normalize = value => new ArraySegment<CallbackSpikeNode>(value.Array!, value.Offset, 1);
        var nestedReferenceCount = -1;
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null)
            {
                root.OnRead = null;
                root.Children = new([accepted, excluded]);
                nestedReferenceCount = accepted.GetReferenceCount();
            }
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(0, nestedReferenceCount);
        Assert.Same(accepted, Assert.Single(root.Children));
        Assert.Same(context, accepted.TryGetContext());
        Assert.Equal(1, accepted.GetReferenceCount());
        Assert.Null(stale.TryGetContext());
        Assert.Null(excluded.TryGetContext());
    }

    [Fact]
    public void WhenSeedingGetterThrowsAfterWriting_ThenLaterWritesAreNotDeferred()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var initial = new CallbackSpikeNode();
        var deferred = new CallbackSpikeNode();
        var later = new CallbackSpikeNode();
        var root = new ActiveSeedGetterRoot { Children = new([initial]) };
        var failure = new InvalidOperationException("getter failure");
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null)
            {
                root.OnRead = null;
                root.Children = new([deferred]);
                throw failure;
            }
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));
        var detachedAfterFailure = root.TryGetContext() is null && deferred.TryGetContext() is null;
        root.AttachToContext(context);
        root.Children = new([later]);

        // Assert
        Assert.Same(failure, exception);
        Assert.True(detachedAfterFailure);
        Assert.Null(initial.TryGetContext());
        Assert.Null(deferred.TryGetContext());
        Assert.Same(context, later.TryGetContext());
        Assert.Equal(1, later.GetReferenceCount());
    }

    [Fact]
    public void WhenSeedingGetterWritesAnotherProperty_ThenThatSetterReconcilesBeforeReturning()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var child = new CallbackSpikeNode();
        var other = new CallbackSpikeNode();
        var root = new ActiveSeedGetterRoot { Children = new([child]) };
        var nestedReferenceCount = -1;
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null)
            {
                root.OnRead = null;
                root.Other = other;
                nestedReferenceCount = other.GetReferenceCount();
            }
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(1, nestedReferenceCount);
        Assert.Same(other, root.Other);
        Assert.Same(context, other.TryGetContext());
        Assert.Equal(1, other.GetReferenceCount());
        Assert.Equal(1, child.GetReferenceCount());
    }

    private sealed class ActiveSeedGetterRoot : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Children)] = new(nameof(Children), typeof(ArraySegment<CallbackSpikeNode>), [],
                    static subject => ((ActiveSeedGetterRoot)subject).ReadChildren(),
                    static (subject, value) => ((ActiveSeedGetterRoot)subject).Children = (ArraySegment<CallbackSpikeNode>)value!,
                    isIntercepted: true, isDynamic: false),
                [nameof(Other)] = new(nameof(Other), typeof(CallbackSpikeNode), [],
                    static subject => ((ActiveSeedGetterRoot)subject)._other,
                    static (subject, value) => ((ActiveSeedGetterRoot)subject).Other = (CallbackSpikeNode?)value,
                    isIntercepted: true, isDynamic: false)
            };
        private IInterceptorExecutor? _executor;
        private ArraySegment<CallbackSpikeNode> _children = new([]);
        private CallbackSpikeNode? _other;
        public Action? OnRead { get; set; }
        public Func<ArraySegment<CallbackSpikeNode>, ArraySegment<CallbackSpikeNode>>? Normalize { get; set; }
        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);
        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;
        public ArraySegment<CallbackSpikeNode> Children
        {
            get => Executor.GetPropertyValue(nameof(Children), static subject => ((ActiveSeedGetterRoot)subject).ReadChildren());
            set => Executor.SetPropertyValue(nameof(Children), value, _children, static (subject, newValue) =>
            {
                var root = (ActiveSeedGetterRoot)subject;
                root._children = root.Normalize?.Invoke(newValue) ?? newValue;
            });
        }
        public CallbackSpikeNode? Other
        {
            get => Executor.GetPropertyValue(nameof(Other), static subject => ((ActiveSeedGetterRoot)subject)._other);
            set => Executor.SetPropertyValue(nameof(Other), value, _other,
                static (subject, newValue) => ((ActiveSeedGetterRoot)subject)._other = newValue);
        }
        private ArraySegment<CallbackSpikeNode> ReadChildren() { OnRead?.Invoke(); return _children; }
        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }
}
