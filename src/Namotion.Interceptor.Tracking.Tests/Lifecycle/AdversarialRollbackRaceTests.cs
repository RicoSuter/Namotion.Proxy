using System.Collections;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The exact scenario AttachResidueTests covers (a concurrent write installs a foreign-context child
/// after the discovery scan, so seeding rejects the attach), with one everyday addition: the
/// component contains a back reference to the root. Nobody violates a contract here.
/// </summary>
public class AdversarialRollbackRaceTests
{
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenSeedingRejectsAChildInstalledAfterDiscoveryAndTheComponentHasABackEdge_ThenTheAttachLeavesNoResidue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var otherContext = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignChild = new Person { FirstName = "F" };
        ((IInterceptorSubject)foreignChild).AttachToContext(otherContext);

        var discoveryReachedTheValue = new ManualResetEventSlim(false);
        var mutationApplied = new ManualResetEventSlim(false);
        var mutationObserved = false;
        var rootWasUnattachedAtScan = false;

        BackEdgeHolder? holder = null;
        var companion = new BackEdgeChild();
        holder = new BackEdgeHolder
        {
            Companion = companion,
            Children = new ParkingEnumerable(() =>
            {
                rootWasUnattachedAtScan = ((IInterceptorSubject)holder!).TryGetContext() is null;
                discoveryReachedTheValue.Set();
                mutationObserved = mutationApplied.Wait(TimeSpan.FromSeconds(30));
            })
        };
        companion.Parent = holder; // the everyday back reference

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            if (!discoveryReachedTheValue.Wait(TimeSpan.FromSeconds(30)))
            {
                return;
            }

            mutationException = Record.Exception(() => holder!.Children = new List<Person> { foreignChild });
            mutationApplied.Set();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)holder).AttachToContext(context));
        var mutatorCompleted = mutator.Join(TimeSpan.FromSeconds(30));

        // Assert: the race really happened
        Assert.True(mutatorCompleted, "the mutating thread never finished");
        Assert.True(mutationObserved, "the discovery scan never observed the concurrent mutation");
        Assert.True(rootWasUnattachedAtScan, "the scan ran after the root was already claimed");
        Assert.Null(mutationException);
        Assert.NotNull(attachException);

        // The rejected attach must leave nothing behind.
        var graph = ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!).Graph;
        var rootSubject = (IInterceptorSubject)holder;
        Assert.False(graph.IsOwned(holder),
            $"the rejected attach left an ownership record for the root " +
            $"(anchor={rootSubject.Executor.AttachmentAnchor}, refCount={rootSubject.GetReferenceCount()}); " +
            $"attach failed with {attachException.GetType().Name}: {attachException.Message}");
        Assert.True(rootSubject.TryGetContext() is null,
            "the rejected attach left the root claimed by the context it was rejected from");
        Assert.True(((IInterceptorSubject)companion).TryGetContext() is null,
            "the rejected attach left the companion claimed");
    }
}
