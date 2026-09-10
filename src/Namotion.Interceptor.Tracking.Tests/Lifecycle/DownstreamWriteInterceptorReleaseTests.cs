using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A third-party write interceptor with no ordering attributes registered after WithLifecycle runs
/// downstream of LifecycleInterceptor, inside its next call, at callback depth zero and holding the
/// lifecycle gate reentrantly. From there it can release the writing parent before the reconcile of
/// the write it sits under is entered. These tests pin that such a release leaves nothing behind:
/// no subject stays attached through an edge from the released parent, and no committed baseline
/// survives for it.
/// </summary>
public class DownstreamWriteInterceptorReleaseTests
{
    [Fact]
    public void WhenADownstreamInterceptorRemovesTheWritingParentsLastSupport_ThenNothingSurvivesTheRelease()
    {
        // Arrange
        var interceptor = new ReleasingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        context.AddService<IWriteInterceptor>(interceptor);

        var root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        root.Father = parent;

        var child = new Person { FirstName = "C" };
        interceptor.Arm(parent, () => root.Father = null);

        // Act: the interceptor removes the parent's last support before calling next, so the
        // lifecycle's reconcile of this write starts on an already released parent.
        parent.Father = child;

        // Assert
        Assert.Same(context, root.TryGetContext());
        Assert.Null(parent.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Empty(child.GetParents());
        AssertNoBaseline(context, parent);
    }

    [Fact]
    public void WhenADownstreamInterceptorDetachesTheWritingParentExplicitly_ThenNothingSurvivesTheRelease()
    {
        // Arrange
        var interceptor = new ReleasingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        context.AddService<IWriteInterceptor>(interceptor);

        var parent = new Person { FirstName = "P" };
        parent.AttachToContext(context);

        var child = new Person { FirstName = "C" };
        interceptor.Arm(parent, () => parent.DetachFromContext(context));

        // Act
        parent.Father = child;

        // Assert
        Assert.Null(parent.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Empty(child.GetParents());
        AssertNoBaseline(context, parent);
    }

    private static void AssertNoBaseline(IInterceptorSubjectContext context, Person parent)
    {
        // The committed baseline is the released parent's outgoing edge record. Baselines are
        // removed exactly once, by the parent's own release, so an entry recreated after that
        // release would survive forever and keep validating edges from a dead owner.
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        Assert.Null(lifecycle.Graph.GetBaseline(new PropertyReference(parent, nameof(Person.Father))));
    }

    /// <summary>
    /// Stands in for a third-party interceptor: no ordering attributes, so registration order
    /// places it downstream of the lifecycle in the resolved write chain.
    /// </summary>
    private sealed class ReleasingWriteInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _target;
        private Action? _release;

        public void Arm(IInterceptorSubject target, Action release)
        {
            _target = target;
            _release = release;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _target))
            {
                // Disarm before acting: the release is itself a structural write that re-enters
                // this interceptor.
                _target = null;
                _release!();
            }

            next(ref context);
        }
    }
}
