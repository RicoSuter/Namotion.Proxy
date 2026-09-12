using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// Gives a container a default child from the container's own context attach, which is the one shape
/// where a subject enters the graph from inside a lifecycle handler that is itself running for another
/// subject's attach. The assignment runs under the lifecycle lock, so the child's own attach event is
/// raised before this handler returns.
/// </summary>
public sealed class ChildCreatingLifecycleHandler : ILifecycleHandler
{
    private int _created;

    /// <summary>Builds the child. Defaults to a plain counting subject.</summary>
    public Func<CountingHostedSubject> ChildFactory { get; set; } = () => new CountingHostedSubject();

    /// <summary>The children this handler assigned, so a repeated assignment is measurable.</summary>
    public int Created => Volatile.Read(ref _created);

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change is { IsContextAttach: true, Subject: HostedContainer { Child: null } container })
        {
            Interlocked.Increment(ref _created);
            container.Child = ChildFactory();
        }
    }
}
