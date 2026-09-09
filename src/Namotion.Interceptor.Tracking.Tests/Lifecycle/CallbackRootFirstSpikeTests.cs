using System.Collections;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackRootFirstSpikeTests
{
    [Fact]
    public void WhenRootEnumerableWritesDuringAttach_ThenOwnershipMatchesTheStoredValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode();
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var reentered = false;
        root.Payload = new HookEnumerable([stale], () =>
        {
            if (!reentered && root.TryGetContext() is not null)
            {
                reentered = true;
                root.Payload = replacement;
            }
        });

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.True(reentered);
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenExplicitRootHasABackEdge_ThenRootAndChildAttachExactlyOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new CallbackSpikeNode();
        var child = new CallbackSpikeNode { Child = root };
        root.Child = child;
        var attached = new List<IInterceptorSubject>();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        lifecycle.SubjectAttached += change => attached.Add(change.Subject);

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal(2, attached.Count);
        Assert.Single(attached, subject => ReferenceEquals(subject, root));
        Assert.Single(attached, subject => ReferenceEquals(subject, child));
        Assert.Equal(1, root.GetReferenceCount());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenExplicitRootCallbackReplacesAChild_ThenTheNestedWriteSettles()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var stale = new CallbackSpikeNode();
        var replacement = new CallbackSpikeNode();
        var root = new CallbackSpikeNode { Child = stale };
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, root)) root.Child = replacement;
        };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Same(replacement, root.Child);
        Assert.Null(stale.TryGetContext());
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
    }

    [Fact]
    public void WhenRootSeedThrows_ThenItsAnchorAndClaimsAreRolledBack()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var target = new CallbackSpikeNode();
        var root = new CallbackSpikeSegmentWrapper { Children = new([target]) };
        var failure = new InvalidOperationException("seed failure");
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null)
            {
                root.OnRead = null;
                throw failure;
            }
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Null(root.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        Assert.Null(target.TryGetContext());
        Assert.Equal(0, target.GetReferenceCount());
    }

    [Fact]
    public void WhenExplicitRootAttaches_ThenTheBeforeHandlerReceivesRootBeforeDescendants()
    {
        // Arrange
        var probe = new RootBeforeProbe();
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        context.AddService<ILifecycleHandler>(probe);
        var leaf = new CallbackSpikeNode();
        var child = new CallbackSpikeNode { Child = leaf };
        var root = new CallbackSpikeNode { Child = child };

        // Act
        root.AttachToContext(context);

        // Assert
        Assert.Equal([root, child, leaf], probe.Attached);
    }

    [RunsBefore(typeof(LifecycleInterceptor))]
    private sealed class RootBeforeProbe : ILifecycleHandler
    {
        public List<IInterceptorSubject> Attached { get; } = [];
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach) Attached.Add(change.Subject);
        }
    }

    private sealed class HookEnumerable(IEnumerable<CallbackSpikeNode> children, Action onEnumeration) : IEnumerable<CallbackSpikeNode>
    {
        public IEnumerator<CallbackSpikeNode> GetEnumerator()
        {
            onEnumeration();
            return children.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
