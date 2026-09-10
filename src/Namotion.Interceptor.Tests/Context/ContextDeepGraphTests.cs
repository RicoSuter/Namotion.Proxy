using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// A subject graph of depth N produces a context graph of depth N, because every attached child
/// inherits the context of its parent as a fallback context. Every walk over that graph therefore
/// has to be iterative: a recursive one dies on an uncatchable <see cref="StackOverflowException"/>
/// long before a legitimate graph runs out of memory, and no handler can save the process from it.
/// </summary>
public class ContextDeepGraphTests
{
    // Deep enough that the recursion this replaced dies on it. That version overflowed at roughly
    // 75,000 frames on an 8 MB stack and far earlier on the 1 MB stacks some hosts use, so a few
    // hundred levels would pass either way and prove nothing.
    private const int ChainLength = 100_000;

    /// <summary>
    /// Isolates the invalidation walk: every context on the chain delegates, so none of them holds
    /// a cache of its own and the service walk never leaves the root. The only walk that goes deep
    /// here is the one from the root up through 100,000 using sets.
    ///
    /// What this guards is therefore that the walk finishes rather than that it invalidates
    /// anything: the assertions below would pass even if it did nothing at all. A context that
    /// delegates records where its chain ends, but that record names the terminal context and not
    /// its state, so every query re-reads the state of the root and sees the new service either
    /// way. Staleness across this depth is covered by the test after it, which asserts it on the
    /// cache of the context at the far end of the chain.
    /// </summary>
    [Fact]
    public void WhenServiceIsAddedAtRootOfVeryDeepChain_ThenTheMutationCompletes()
    {
        // Arrange: one context per level, each one using the level below it as its only fallback
        // context, which puts it into the using set of that level.
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());

        InterceptorSubjectContext? middleContext = null;
        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;

            if (index == ChainLength / 2)
            {
                middleContext = context;
            }
        }

        Assert.Single(deepestContext.GetServices<MarkerService>());

        // Act
        rootContext.AddService(new OtherMarkerService());

        // Assert
        Assert.Single(rootContext.GetServices<OtherMarkerService>());
        Assert.Single(middleContext!.GetServices<OtherMarkerService>());
        Assert.Single(deepestContext.GetServices<OtherMarkerService>());
    }

    /// <summary>
    /// Exercises the service walk over the same depth: a context with two fallback contexts is not
    /// a delegation target, so the walk cannot collapse the chain into a single hop and descends
    /// every one of the 100,000 levels. The mutation at the end then also has to reach the cache
    /// that the resolution filled at the far side of it.
    /// </summary>
    [Fact]
    public void WhenVeryDeepChainHasMultiFallbackNodes_ThenServicesResolveThroughAllOfThem()
    {
        // Arrange: the same chain, but every 10,000th level carries a second fallback context with
        // an own service, the last level included.
        const int branchInterval = 10_000;
        const int branchCount = ChainLength / branchInterval;

        var rootContext = InterceptorSubjectContext.Create();
        var rootService = new MarkerService();
        rootContext.AddService(rootService);

        var branchServices = new List<MarkerService>();
        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);

            if ((index + 1) % branchInterval == 0)
            {
                var branchContext = InterceptorSubjectContext.Create();
                var branchService = new MarkerService();
                branchContext.AddService(branchService);
                branchServices.Add(branchService);
                context.AddFallbackContext(branchContext);
            }

            deepestContext = context;
        }

        // Act
        var services = deepestContext.GetServices<MarkerService>();

        // Assert
        Assert.Equal(branchCount + 1, services.Length);
        Assert.Contains(rootService, services);
        Assert.All(branchServices, branchService => Assert.Contains(branchService, services));

        // Act: the deepest context has two fallback contexts and therefore keeps the cache that the
        // resolution above just filled, 100,000 levels away from the context being mutated.
        rootContext.AddService(new MarkerService());

        // Assert: a cache that the invalidation did not reach would still answer with the old count.
        Assert.Equal(branchCount + 2, deepestContext.GetServices<MarkerService>().Length);
    }

    /// <summary>
    /// The same for the visited set of the service walk, which is a third thread static and the one
    /// the two tests below do not grow. A pure chain is collapsed onto a single frame, so only a
    /// context that is not a delegation target makes the walk descend and mark every level.
    /// </summary>
    [Fact]
    public void WhenVeryDeepChainWasCollected_ThenTheServiceWalkBufferIsNotRetained()
    {
        // Arrange: a pure chain whose head carries a second fallback context, which is what stops
        // the head from delegating and makes the collecting walk descend all 100,000 levels.
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());

        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;
        }

        var branchContext = InterceptorSubjectContext.Create();
        branchContext.AddService(new OtherMarkerService());
        deepestContext.AddFallbackContext(branchContext);

        // Act: one cold resolution, which marks every level visited.
        Assert.Single(deepestContext.GetServices<MarkerService>());

        // Assert
        Assert.Null(GetThreadStaticBuffer("_serviceQueryVisited"));
    }

    /// <summary>
    /// The walk down a delegation chain records every context it passes so that it can note the end
    /// of the chain on each of them. Those buffers are thread statics, and clearing a collection
    /// keeps its capacity, so a thread that once walked a chain this deep would hold an entry per
    /// level for the rest of the process. It costs nothing to notice while a chain of 100,000 is a
    /// test fixture, and megabytes per thread in a host whose graph is that deep.
    /// </summary>
    [Fact]
    public void WhenVeryDeepChainWasWalked_ThenTheWalkBuffersAreNotRetained()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());

        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;
        }

        // Act: one cold resolution, which walks all 100,000 levels.
        Assert.Single(deepestContext.GetServices<MarkerService>());

        // Assert: the buffers belong to this thread, so they are read from it.
        Assert.Null(GetThreadStaticBuffer("_delegationCyclePath"));
        Assert.Null(GetThreadStaticBuffer("_delegationCycleVisited"));
    }

    /// <summary>
    /// The same for the buffers of the invalidation walk, which are separate thread statics and
    /// were missed when the walk down the chain got this treatment: the using graph of a chain
    /// queues one context per step, so its worklist never grows while its visited set takes an
    /// entry per level, and keying the check on the worklist never dropped anything.
    /// </summary>
    [Fact]
    public void WhenVeryDeepChainWasInvalidated_ThenTheInvalidationBuffersAreNotRetained()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext.Create();

        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;
        }

        // Act: one mutation at the root, whose walk climbs all 100,000 levels.
        rootContext.AddService(new MarkerService());

        // Assert
        Assert.Null(GetThreadStaticBuffer("_invalidationVisited"));
        Assert.Null(GetThreadStaticBuffer("_invalidationPending"));
    }

    /// <summary>
    /// Pins the invariant everything else is derived from: a state is installed exactly once, so a
    /// state object still in place has been in place since it was pinned. The cycle confirmation
    /// proves a loop existed at one instant from exactly that, and a recorded chain end is only
    /// discarded by a change because invalidation installs a different object. An invalidation that
    /// kept the same object when it carried no caches would pass every other test here.
    /// </summary>
    [Fact]
    public void WhenContextIsInvalidated_ThenItsStateObjectIsReplaced()
    {
        // Arrange: no query, so the state carries no caches and is the one an invalidation could be
        // tempted to keep.
        var rootContext = InterceptorSubjectContext.Create();
        var usingContext = InterceptorSubjectContext.Create();
        usingContext.AddFallbackContext(rootContext);

        var stateBefore = GetState(usingContext);

        // Act
        rootContext.AddService(new MarkerService());

        // Assert
        Assert.NotSame(stateBefore, GetState(usingContext));
    }

    [Fact]
    public void WhenOwnershipRouteChainIsVeryDeep_ThenResolutionAndInvalidationRemainIterative()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());

        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            var route = new InterceptorSubjectContext.ContextOwnershipRoute(deepestContext, ownershipDomain);
            Assert.True(context.TryChangeOwnershipRoute(null, route));
            deepestContext = context;
        }

        Assert.Single(deepestContext.GetServices<MarkerService>());

        // Act
        rootContext.AddService(new MarkerService());

        // Assert
        Assert.Equal(2, deepestContext.GetServices<MarkerService>().Length);
        Assert.Null(GetThreadStaticBuffer("_delegationCyclePath"));
        Assert.Null(GetThreadStaticBuffer("_delegationCycleVisited"));
        Assert.Null(GetThreadStaticBuffer("_invalidationVisited"));
        Assert.Null(GetThreadStaticBuffer("_invalidationPending"));
    }

    private sealed class MarkerService;

    private sealed class OtherMarkerService;
}
