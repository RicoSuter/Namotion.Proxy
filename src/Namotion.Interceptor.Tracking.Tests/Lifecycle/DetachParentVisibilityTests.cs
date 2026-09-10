using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// What a detach handler can still see of a subject's parents.
/// </summary>
/// <remarks>
/// Authoritative parent state is published before the first detach handler, so a subject leaving the
/// graph reports no parents to any of them. That is not a loss of information: the edge it is
/// leaving through is on the change itself, and a subject that survives an edge removal still
/// reports the edges that remain, which is the state a source-scope walk actually resolves against.
///
/// Handlers ordered behind the lifecycle descent, which is where <c>SourceMonitor</c> sits, saw
/// the same empty result before this change: the previous parent writer removed the entry for the
/// removed edge before they ran, and a final release was by definition the removal of the last edge.
/// </remarks>
public class DetachParentVisibilityTests
{
    [Fact]
    public void WhenASubjectIsFinallyReleased_ThenHandlersAfterParentTrackingSeeNoParents()
    {
        // Arrange
        var observed = new List<int>();
        // The probe is registered ahead of the lifecycle on purpose: only its [RunsAfter]
        // constraint moves it behind the descent, so the slot it occupies is pinned by the
        // attribute rather than by registration order.
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new LateParentProbe(observed), _ => false)
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };
        root.Father = child;

        // Act
        root.Father = null;

        // Assert
        Assert.Equal([0], observed);
        Assert.Empty(((IInterceptorSubject)child).GetParents());
    }

    [Fact]
    public void WhenASubjectSurvivesAnEdgeRemoval_ThenHandlersStillSeeTheRemainingParents()
    {
        // Arrange: the case a source-scope walk depends on. The subject stays in the graph, so its
        // remaining ancestry has to stay resolvable while the removal is being published.
        var observed = new List<int>();
        // The probe is registered ahead of the lifecycle on purpose: only its [RunsAfter]
        // constraint moves it behind the descent, so the slot it occupies is pinned by the
        // attribute rather than by registration order.
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new LateParentProbe(observed), _ => false)
            .WithLifecycle();

        var root = new Person(context) { FirstName = "Root" };
        var shared = new Person { FirstName = "Shared" };
        root.Father = shared;
        root.Mother = shared;

        // Act
        root.Father = null;

        // Assert
        Assert.Equal([1], observed);
        Assert.Single(((IInterceptorSubject)shared).GetParents());
    }

    /// <summary>
    /// Occupies the same ordering position as <c>SourceMonitor</c>, which is the only first-party
    /// consumer that walks parents from inside a lifecycle callback.
    /// </summary>
    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class LateParentProbe(List<int> observed) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsPropertyReferenceRemoved)
            {
                observed.Add(change.Subject.GetParents().Length);
            }
        }
    }
}
