# Effective Ownership-Route State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a permanent internal ownership-route relationship to Core context state without changing public or production behavior.

**Architecture:** Store an immutable target and ownership-domain descriptor only in a derived routed context state, leaving route-free state layout unchanged. Publish route changes with the existing context mutation lock, traverse the route after public fallbacks, and maintain the reverse dependency as the union of both relationship kinds.

**Tech Stack:** C# 13, .NET Standard 2.0 Core library, xUnit, immutable copy-on-write context state, PublicApiGenerator and Verify.

## Global Constraints

- Work in `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor/.claude/worktrees/single-context-stack-roadmap` on `design/single-context-stack-roadmap`.
- The pull request base is exact `master` commit `868a4d109d53b24805c9ee180efbf5029ee12c1a`.
- Follow `AGENTS.md`: correctness first, then allocations and CPU, then style.
- Use strict TDD. No production code is written before the covering tests are observed failing for the missing ownership-route capability.
- Tests use `When<Condition>_Then<ExpectedBehavior>` names and explicit `// Arrange`, `// Act`, and `// Assert` sections.
- Deterministic concurrency uses `Barrier`, `ManualResetEventSlim`, or `AsyncTestHelpers.WaitUntilAsync`; never `Task.Delay` or `Thread.Sleep`.
- Add no public type or member. The Core Public API snapshot must remain byte-for-byte unchanged.
- Do not change `IInterceptorSubjectContext`, `InterceptorExecutor`, source generation, Tracking, Registry, Hosting, connectors, OPC UA, or HomeBlaze.
- Existing fallback lifecycle behavior remains unchanged because no production path installs an ownership route in this pull request.
- Route-free `InterceptorSubjectContext` and base `ContextState` instance fields and object sizes remain unchanged.
- Steady-state intercepted reads, writes, method invocations, delegation, and cached service resolution add no route lookup and no allocation.
- The internal service order is local services, public fallbacks in insertion order, then the ownership route. Ordering attributes still override otherwise unconstrained route order.
- Reverse using-context registration is the union of public fallback and ownership-route relationships to one target.
- A stale operation can change a route only when its exact expected descriptor instance is still current.
- Use no em dash in documentation, comments, commit messages, or pull request text.
- Local benchmark timings are diagnostic only. Do static hot-path and allocation analysis locally, then ask the maintainer before handing the exact comparison to the stable benchmark machine.
- A nonzero build, test, or pack exit is not a passing gate. Record an infrastructure timeout accurately and obtain a clean successful run before the pull request is finalized.

---

## File Map

- Modify `src/Namotion.Interceptor/InterceptorSubjectContext.cs`: route descriptor, routed immutable state, exact route transition, traversal order, delegation, and reverse invalidation ownership.
- Create `src/Namotion.Interceptor.Tests/Context/ContextOwnershipRouteTests.cs`: deterministic route behavior, ABA, invalidation, cycle, and concurrency contracts.
- Modify `src/Namotion.Interceptor.Tests/Context/ContextServiceWalkOrderTests.cs`: extend the recursive differential oracle with one ownership route per node.
- Modify `src/Namotion.Interceptor.Tests/Context/ContextDeepGraphTests.cs`: prove route traversal and invalidation remain iterative at graph depth.
- Create `docs/design/context-resolution.md`: permanent internal context-resolution terms, order, publication, and invalidation rules.

Before Task 1 is dispatched, the controller commits this plan together with the approved spec and
roadmap, then verifies `git status --short` is empty. The implementation task therefore begins from
a clean, recoverable planning commit.

## Task 1: Implement the Internal Ownership Route Test-First

**Files:**
- Modify: `src/Namotion.Interceptor/InterceptorSubjectContext.cs`
- Create: `src/Namotion.Interceptor.Tests/Context/ContextOwnershipRouteTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextServiceWalkOrderTests.cs`
- Modify: `src/Namotion.Interceptor.Tests/Context/ContextDeepGraphTests.cs`

**Interfaces:**
- Consumes: existing `InterceptorSubjectContext` immutable state, `_mutationLock`, `_usedByContexts`, delegation cache, service-walk visited set, and invalidation worklist.
- Produces: nested internal `InterceptorSubjectContext.ContextOwnershipRoute` and `InterceptorSubjectContext.TryChangeOwnershipRoute(ContextOwnershipRoute? expected, ContextOwnershipRoute? replacement)` for PR 2.

- [ ] **Step 1: Create the deterministic ownership-route tests**

Create `ContextOwnershipRouteTests.cs` with the following real-behavior tests. The exact descriptor identity is exercised through transition results and resolved services, not through reflection or a mock.

```csharp
using Namotion.Interceptor.Testing;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

public class ContextOwnershipRouteTests
{
    [Fact]
    public void WhenOwnershipRouteIsInstalled_ThenServicesResolveAfterFallbacks()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var routeTarget = InterceptorSubjectContext.Create();
        routeTarget.AddService<IRouteService>(new RouteService("route"));

        var fallback = InterceptorSubjectContext.Create();
        fallback.AddService<IRouteService>(new RouteService("fallback"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        context.AddFallbackContext(fallback);

        var route = new InterceptorSubjectContext.ContextOwnershipRoute(routeTarget, ownershipDomain);

        // Act
        var installed = context.TryChangeOwnershipRoute(null, route);
        var names = context.GetServices<IRouteService>().Select(service => service.Name).ToArray();

        // Assert
        Assert.True(installed);
        Assert.Equal(["local", "fallback", "route"], names);
    }

    [Fact]
    public void WhenFallbackAndOwnershipRouteShareTarget_ThenTargetServicesResolveOnce()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        var service = new RouteService("target");
        target.AddService<IRouteService>(service);

        var context = InterceptorSubjectContext.Create();
        context.AddFallbackContext(target);
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);

        // Act
        Assert.True(context.TryChangeOwnershipRoute(null, route));
        var services = context.GetServices<IRouteService>();

        // Assert
        Assert.Single(services);
        Assert.Same(service, services[0]);
    }

    [Fact]
    public void WhenOldDescriptorClearsSameTargetGeneration_ThenNewGenerationRemains()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target"));
        var context = InterceptorSubjectContext.Create();

        var first = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        var second = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, first));
        Assert.True(context.TryChangeOwnershipRoute(first, second));

        // Act
        var staleClear = context.TryChangeOwnershipRoute(first, null);

        // Assert
        Assert.False(staleClear);
        Assert.Single(context.GetServices<IRouteService>());
        Assert.True(context.TryChangeOwnershipRoute(second, null));
        Assert.Empty(context.GetServices<IRouteService>());
    }

    [Fact]
    public void WhenServicesAreAddedReentrantlyAfterRouteInstall_ThenRouteSurvivesEveryStatePublication()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("route"));

        var context = InterceptorSubjectContext.Create();
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, route));

        // Act
        var added = context.TryAddService<IRouteService>(
            () =>
            {
                context.AddService<IRouteService>(new RouteService("reentrant"));
                return new RouteService("added");
            },
            _ => false);

        var names = context.GetServices<IRouteService>().Select(service => service.Name).ToArray();

        // Assert
        Assert.True(added);
        Assert.Equal(["reentrant", "added", "route"], names);
    }

    [Fact]
    public void WhenRouteTargetMutatesAfterResolution_ThenInvalidatedStateRetainsRoute()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target-1"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, route));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        // Act
        target.AddService<IRouteService>(new RouteService("target-2"));

        // Assert
        Assert.Equal(3, context.GetServices<IRouteService>().Length);
    }

    [Fact]
    public void WhenRouteTransfersToDifferentTarget_ThenReverseDependenciesFollowPublishedRelationships()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var targetA = InterceptorSubjectContext.Create();
        var targetB = InterceptorSubjectContext.Create();
        targetA.AddService<IRouteService>(new RouteService("a-1"));
        targetB.AddService<IRouteService>(new RouteService("b-1"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        var routeA = new InterceptorSubjectContext.ContextOwnershipRoute(targetA, ownershipDomain);
        var routeB = new InterceptorSubjectContext.ContextOwnershipRoute(targetB, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, routeA));
        Assert.True(context.AddFallbackContext(targetA));
        Assert.Equal(["local", "a-1"],
            context.GetServices<IRouteService>().Select(service => service.Name).ToArray());

        // Act: transfer the route away from A while the fallback still depends on A.
        Assert.True(context.TryChangeOwnershipRoute(routeA, routeB));

        // Assert
        Assert.Equal(["local", "a-1", "b-1"],
            context.GetServices<IRouteService>().Select(service => service.Name).ToArray());

        var stateWithA = GetState(context);
        targetA.AddService<IRouteService>(new RouteService("a-2"));

        // Assert
        Assert.NotSame(stateWithA, GetState(context));
        Assert.Equal(4, context.GetServices<IRouteService>().Length);

        // Act: removing the fallback ends the final relationship to A.
        Assert.True(context.RemoveFallbackContext(targetA));
        Assert.Equal(["local", "b-1"],
            context.GetServices<IRouteService>().Select(service => service.Name).ToArray());

        var stateWithoutA = GetState(context);
        targetA.AddService<IRouteService>(new RouteService("a-3"));

        // Assert
        Assert.Same(stateWithoutA, GetState(context));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        var stateWithB = GetState(context);
        targetB.AddService<IRouteService>(new RouteService("b-2"));
        Assert.NotSame(stateWithB, GetState(context));
        Assert.Equal(3, context.GetServices<IRouteService>().Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenFallbackAndRouteShareTarget_ThenRemovingOneKeepsInvalidationThroughTheOther(
        bool removeFallbackFirst)
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target-1"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        context.AddFallbackContext(target);
        var route = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, route));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        // Act
        if (removeFallbackFirst)
        {
            Assert.True(context.RemoveFallbackContext(target));
        }
        else
        {
            Assert.True(context.TryChangeOwnershipRoute(route, null));
        }

        target.AddService<IRouteService>(new RouteService("target-2"));

        // Assert
        Assert.Equal(3, context.GetServices<IRouteService>().Length);

        // Act
        if (removeFallbackFirst)
        {
            Assert.True(context.TryChangeOwnershipRoute(route, null));
        }
        else
        {
            Assert.True(context.RemoveFallbackContext(target));
        }

        Assert.Single(context.GetServices<IRouteService>());
        var stateAfterFinalRemoval = GetState(context);
        target.AddService<IRouteService>(new RouteService("target-3"));

        // Assert
        Assert.Same(stateAfterFinalRemoval, GetState(context));
        Assert.Single(context.GetServices<IRouteService>());
    }

    [Fact]
    public void WhenOwnershipRoutesFormDelegationCycle_ThenResolutionThrows()
    {
        // Arrange
        var ownershipDomain = InterceptorSubjectContext.Create();
        var contextA = InterceptorSubjectContext.Create();
        var contextB = InterceptorSubjectContext.Create();
        var routeA = new InterceptorSubjectContext.ContextOwnershipRoute(contextB, ownershipDomain);
        var routeB = new InterceptorSubjectContext.ContextOwnershipRoute(contextA, ownershipDomain);
        Assert.True(contextA.TryChangeOwnershipRoute(null, routeA));
        Assert.True(contextB.TryChangeOwnershipRoute(null, routeB));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => contextA.GetServices<IRouteService>());

        // Assert
        Assert.Contains("delegation cycle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTwoThreadsInstallFirstRoute_ThenExactlyOneDescriptorWins()
    {
        const int attempts = 500;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Arrange
            var ownershipDomain = InterceptorSubjectContext.Create();
            var firstTarget = InterceptorSubjectContext.Create();
            var secondTarget = InterceptorSubjectContext.Create();
            var firstService = new RouteService("first");
            var secondService = new RouteService("second");
            firstTarget.AddService<IRouteService>(firstService);
            secondTarget.AddService<IRouteService>(secondService);

            var context = InterceptorSubjectContext.Create();
            var firstRoute = new InterceptorSubjectContext.ContextOwnershipRoute(firstTarget, ownershipDomain);
            var secondRoute = new InterceptorSubjectContext.ContextOwnershipRoute(secondTarget, ownershipDomain);
            using var start = new Barrier(2);
            var results = new bool[2];

            var installers = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryChangeOwnershipRoute(null, firstRoute);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryChangeOwnershipRoute(null, secondRoute);
                }, TaskCreationOptions.LongRunning)
            };

            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => installers.All(installer => installer.IsCompleted),
                message: $"Concurrent ownership-route installation did not complete on attempt {attempt}");
            await Task.WhenAll(installers);

            // Assert
            Assert.NotEqual(results[0], results[1]);
            var service = Assert.Single(context.GetServices<IRouteService>());
            Assert.Same(results[0] ? firstService : secondService, service);
        }
    }

    [Fact]
    public async Task WhenTwoThreadsTransferSameRoute_ThenExactlyOneReplacementWins()
    {
        const int attempts = 500;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Arrange
            var ownershipDomain = InterceptorSubjectContext.Create();
            var initialTarget = InterceptorSubjectContext.Create();
            var firstTarget = InterceptorSubjectContext.Create();
            var secondTarget = InterceptorSubjectContext.Create();
            var firstService = new RouteService("first");
            var secondService = new RouteService("second");
            firstTarget.AddService<IRouteService>(firstService);
            secondTarget.AddService<IRouteService>(secondService);

            var context = InterceptorSubjectContext.Create();
            var initialRoute = new InterceptorSubjectContext.ContextOwnershipRoute(initialTarget, ownershipDomain);
            var firstRoute = new InterceptorSubjectContext.ContextOwnershipRoute(firstTarget, ownershipDomain);
            var secondRoute = new InterceptorSubjectContext.ContextOwnershipRoute(secondTarget, ownershipDomain);
            Assert.True(context.TryChangeOwnershipRoute(null, initialRoute));

            using var start = new Barrier(2);
            var results = new bool[2];
            var transfers = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryChangeOwnershipRoute(initialRoute, firstRoute);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryChangeOwnershipRoute(initialRoute, secondRoute);
                }, TaskCreationOptions.LongRunning)
            };

            // Act
            await AsyncTestHelpers.WaitUntilAsync(
                () => transfers.All(transfer => transfer.IsCompleted),
                message: $"Concurrent ownership-route transfer did not complete on attempt {attempt}");
            await Task.WhenAll(transfers);

            // Assert
            Assert.NotEqual(results[0], results[1]);
            var service = Assert.Single(context.GetServices<IRouteService>());
            Assert.Same(results[0] ? firstService : secondService, service);
        }
    }

    [Fact]
    public async Task WhenRouteAndTargetMutateConcurrently_ThenQuiescentResolutionSeesAllTargetServices()
    {
        // Arrange
        const int mutations = 200;
        var ownershipDomain = InterceptorSubjectContext.Create();
        var target = InterceptorSubjectContext.Create();
        target.AddService<IRouteService>(new RouteService("target-initial"));

        var context = InterceptorSubjectContext.Create();
        context.AddService<IRouteService>(new RouteService("local"));
        var initialRoute = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
        Assert.True(context.TryChangeOwnershipRoute(null, initialRoute));
        Assert.Equal(2, context.GetServices<IRouteService>().Length);

        using var start = new Barrier(3);
        using var routeMidpoint = new ManualResetEventSlim(false);
        using var readerObservedMidpoint = new ManualResetEventSlim(false);
        var activeWriters = 2;

        var routeWriter = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                var current = initialRoute;
                for (var index = 0; index < mutations; index++)
                {
                    Assert.True(context.TryChangeOwnershipRoute(current, null));
                    current = new InterceptorSubjectContext.ContextOwnershipRoute(target, ownershipDomain);
                    Assert.True(context.TryChangeOwnershipRoute(null, current));

                    if (index == mutations / 2)
                    {
                        routeMidpoint.Set();
                        readerObservedMidpoint.Wait();
                    }
                }
            }
            finally
            {
                routeMidpoint.Set();
                Interlocked.Decrement(ref activeWriters);
            }
        }, TaskCreationOptions.LongRunning);

        var serviceWriter = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                for (var index = 0; index < mutations; index++)
                {
                    target.AddService<IRouteService>(new RouteService($"target-{index}"));
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeWriters);
            }
        }, TaskCreationOptions.LongRunning);

        var reader = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            try
            {
                while (!routeMidpoint.IsSet)
                {
                    _ = context.GetServices<IRouteService>();
                }

                _ = context.GetServices<IRouteService>();
            }
            finally
            {
                readerObservedMidpoint.Set();
            }

            while (Volatile.Read(ref activeWriters) != 0)
            {
                _ = context.GetServices<IRouteService>();
            }
        }, TaskCreationOptions.LongRunning);

        // Act
        var workers = new[] { routeWriter, serviceWriter, reader };
        await AsyncTestHelpers.WaitUntilAsync(
            () => workers.All(worker => worker.IsCompleted),
            message: "Concurrent route, target, and resolution work did not complete");
        await Task.WhenAll(workers);

        // Assert
        Assert.Equal(mutations + 2, context.GetServices<IRouteService>().Length);
    }

    private interface IRouteService
    {
        string Name { get; }
    }

    private sealed class RouteService(string name) : IRouteService
    {
        public string Name { get; } = name;
    }
}
```

- [ ] **Step 2: Extend the recursive service-order oracle before implementation**

Modify `ContextServiceWalkOrderTests.cs` so its independent model includes an optional route after fallbacks.

Add route creation at the end of `BuildGraph`:

```csharp
var ownershipDomain = InterceptorSubjectContext.Create();
foreach (var source in nodes)
{
    if (random.Next(2) == 0)
    {
        continue;
    }

    var target = nodes[random.Next(nodes.Length)];
    source.OwnershipRoute = target;
    var route = new InterceptorSubjectContext.ContextOwnershipRoute(target.Context, ownershipDomain);
    Assert.True(source.Context.TryChangeOwnershipRoute(null, route));
}
```

Update the recursive reference to traverse the route after fallbacks:

```csharp
foreach (var fallback in node.Fallbacks)
{
    collected.AddRange(CollectWithReference(fallback, visited));
}

if (node.OwnershipRoute is not null)
{
    collected.AddRange(CollectWithReference(node.OwnershipRoute, visited));
}
```

Add these exact model members to `Node`:

```csharp
internal Node? OwnershipRoute { get; set; }

internal IEnumerable<Node> Relationships =>
    OwnershipRoute is null ? Fallbacks : Fallbacks.Append(OwnershipRoute!);

internal Node? DelegationTarget
{
    get
    {
        if (Services.Count != 0)
        {
            return null;
        }

        if (OwnershipRoute is null)
        {
            return Fallbacks.Count == 1 ? Fallbacks[0] : null;
        }

        if (Fallbacks.Count == 0)
        {
            return OwnershipRoute;
        }

        return Fallbacks.Count == 1 && ReferenceEquals(Fallbacks[0], OwnershipRoute)
            ? OwnershipRoute
            : null;
    }
}
```

Replace the corresponding coverage and description logic with:

```csharp
Assert.True(coverage.OwnershipRoutes > 0, "No graph contained an ownership route.");
Assert.True(coverage.RelationshipCycles > 0, "No graph contained a relationship cycle.");

var ownershipRoute = node.OwnershipRoute;
builder.AppendLine(
    $"  c{node.Index} services [{string.Join(", ", node.Services)}] " +
    $"fallbacks [{string.Join(", ", node.Fallbacks.Select(fallback => $"c{fallback.Index}"))}] " +
    $"route [{(ownershipRoute is null ? string.Empty : $"c{ownershipRoute.Index}")}]");

internal int OwnershipRoutes;
internal int RelationshipCycles;

if (node.OwnershipRoute is not null)
{
    OwnershipRoutes++;
}

if (nodes.SelectMany(other => other.Relationships).Count(target => ReferenceEquals(target, node)) > 1)
{
    SharedNodes++;
}

if (ReachesItself(node))
{
    RelationshipCycles++;
}

private static bool ReachesItself(Node node)
{
    var visited = new HashSet<Node>();
    var pending = new Stack<Node>(node.Relationships);
    while (pending.Count != 0)
    {
        var current = pending.Pop();
        if (ReferenceEquals(current, node))
        {
            return true;
        }

        if (!visited.Add(current))
        {
            continue;
        }

        foreach (var relationship in current.Relationships)
        {
            pending.Push(relationship);
        }
    }

    return false;
}
```

Rename the existing `FallbackCycles` counter and assertion to `RelationshipCycles`; do not retain both counters. Keep the model independent: it derives expected order from its own `Fallbacks` and `OwnershipRoute`, never from production state.

- [ ] **Step 3: Add the deep ownership-route test before implementation**

Add this test to `ContextDeepGraphTests.cs` using the existing `ChainLength` constant:

```csharp
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
```

- [ ] **Step 4: Run the new tests and verify RED**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~ContextOwnershipRouteTests|FullyQualifiedName~ContextServiceWalkOrderTests|FullyQualifiedName~WhenOwnershipRouteChainIsVeryDeep"
```

Expected: build fails with `CS0426` for missing `InterceptorSubjectContext.ContextOwnershipRoute` and `CS1061` for missing `TryChangeOwnershipRoute`. This is the expected RED because the internal route capability does not exist on `master`.

- [ ] **Step 5: Add the immutable descriptor and routed state**

In `InterceptorSubjectContext.cs`, add the nested immutable descriptor:

```csharp
internal sealed class ContextOwnershipRoute
{
    internal ContextOwnershipRoute(
        InterceptorSubjectContext target,
        InterceptorSubjectContext ownershipDomain)
    {
        Target = target;
        OwnershipDomain = ownershipDomain;
    }

    internal InterceptorSubjectContext Target { get; }

    internal InterceptorSubjectContext OwnershipDomain { get; }
}
```

Change `ContextState` from sealed to an inheritable private class without adding instance fields. Add:

```csharp
private sealed class RoutedContextState : ContextState
{
    internal RoutedContextState(
        ImmutableArray<object> services,
        ImmutableArray<InterceptorSubjectContext> fallbackContexts,
        ContextOwnershipRoute ownershipRoute)
        : base(services, fallbackContexts, ownershipRoute.Target)
    {
        OwnershipRoute = ownershipRoute;
    }

    internal readonly ContextOwnershipRoute OwnershipRoute;
}

private static ContextOwnershipRoute? GetOwnershipRoute(ContextState state)
{
    return state is RoutedContextState routedState ? routedState.OwnershipRoute : null;
}

private static ContextState CreateContextState(
    ImmutableArray<object> services,
    ImmutableArray<InterceptorSubjectContext> fallbackContexts,
    ContextOwnershipRoute? ownershipRoute)
{
    return ownershipRoute is null
        ? new ContextState(services, fallbackContexts)
        : new RoutedContextState(services, fallbackContexts, ownershipRoute);
}
```

Give `ContextState`'s constructor an optional `ownershipRouteTarget` parameter and derive delegation exactly as follows:

```csharp
internal ContextState(
    ImmutableArray<object> services,
    ImmutableArray<InterceptorSubjectContext> fallbackContexts,
    InterceptorSubjectContext? ownershipRouteTarget = null)
{
    Services = services;
    FallbackContexts = fallbackContexts;

    if (!services.IsEmpty)
    {
        DelegationTarget = null;
    }
    else if (fallbackContexts.IsEmpty)
    {
        DelegationTarget = ownershipRouteTarget;
    }
    else if (fallbackContexts.Length == 1 &&
             (ownershipRouteTarget is null || ReferenceEquals(fallbackContexts[0], ownershipRouteTarget)))
    {
        DelegationTarget = fallbackContexts[0];
    }
}

internal bool IsEmpty => Services.IsEmpty && FallbackContexts.IsEmpty;
```

Keep the existing `IsEmpty` expression unchanged. Add a comment at its use in
`GetServicesFromState`: a route-only source always has `DelegationTarget` and every caller resolves
that delegation before reaching the empty-state check. This preserves the route-free hot path.

`WithoutCaches()` returns `CreateContextState(Services, FallbackContexts, GetOwnershipRoute(this))`. Use these exact replacement-state expressions so an installed route survives every unrelated mutation:

```csharp
// AddFallbackContext
var replacementState = CreateContextState(
    state.Services,
    state.FallbackContexts.Add(contextImpl),
    GetOwnershipRoute(state));

// RemoveFallbackContext
var replacementState = CreateContextState(
    state.Services,
    state.FallbackContexts.RemoveAt(index),
    GetOwnershipRoute(state));

// TryAddService and AddService
var replacementState = CreateContextState(
    state.Services.Add(service!),
    state.FallbackContexts,
    GetOwnershipRoute(state));

// ContextState.WithoutCaches
return CreateContextState(Services, FallbackContexts, GetOwnershipRoute(this));
```

- [ ] **Step 6: Add the exact route transition and reverse dependency union**

Add this internal operation beside the fallback mutators:

```csharp
internal bool TryChangeOwnershipRoute(
    ContextOwnershipRoute? expected,
    ContextOwnershipRoute? replacement)
{
    var changed = false;

    lock (_mutationLock)
    {
        var state = Volatile.Read(ref _state);
        var current = GetOwnershipRoute(state);
        if (!ReferenceEquals(current, expected))
        {
            return false;
        }

        if (ReferenceEquals(current, replacement))
        {
            return true;
        }

        var replacementState = CreateContextState(state.Services, state.FallbackContexts, replacement);

        if (replacement is not null &&
            !ReferenceEquals(current?.Target, replacement.Target))
        {
            RegisterUsingContext(replacement.Target);
        }

        PublishState(replacementState);

        if (current is not null &&
            !ReferenceEquals(current.Target, replacement?.Target) &&
            !UsesTarget(replacementState, current.Target))
        {
            UnregisterUsingContext(current.Target);
        }

        changed = true;
    }

    if (changed)
    {
        InvalidateUsingContexts();
    }

    return true;
}
```

Extract the existing reverse-set add and remove bodies into private helpers that never call another context mutation method:

```csharp
private void RegisterUsingContext(InterceptorSubjectContext target)
{
    var usedByContexts = target.GetOrCreateUsedByContexts();
    lock (usedByContexts)
    {
        usedByContexts.Add(this);
    }
}

private void UnregisterUsingContext(InterceptorSubjectContext target)
{
    var usedByContexts = Volatile.Read(ref target._usedByContexts);
    if (usedByContexts is not null)
    {
        lock (usedByContexts)
        {
            usedByContexts.Remove(this);
        }
    }
}

private static bool UsesTarget(ContextState state, InterceptorSubjectContext target)
{
    return state.FallbackContexts.Contains(target) ||
           ReferenceEquals(GetOwnershipRoute(state)?.Target, target);
}
```

Construct `replacementState` before reverse registration. An allocation failure must leave no extra using-set entry.

Use `RegisterUsingContext` in `AddFallbackContext`. In `RemoveFallbackContext`, publish the replacement state first and call `UnregisterUsingContext` only when `UsesTarget(replacementState, contextImpl)` is false. This is the load-bearing union rule for a fallback and ownership route that share a target.

In `AddFallbackContext`, construct `replacementState` before `RegisterUsingContext(contextImpl)`,
then register and publish in that order. This preserves register-before-publish while ensuring an
allocation failure cannot leave an extra reverse entry.

- [ ] **Step 7: Traverse the ownership route after public fallbacks**

Add `OwnershipRouteEntered` to `ServiceWalkFrame`. After its fallback cursor is exhausted but before `ReduceFrame`, enter the route once:

```csharp
if (!frame.OwnershipRouteEntered)
{
    frame.OwnershipRouteEntered = true;
    frames[frameIndex] = frame;

    var ownershipRoute = GetOwnershipRoute(frame.State);
    if (ownershipRoute is not null &&
        TryEnterContext(
            ownershipRoute.Target,
            Volatile.Read(ref ownershipRoute.Target._state),
            visited,
            out var ownershipRouteState))
    {
        PushFrame(frames, collected, type, ownershipRouteState);
        continue;
    }
}
```

The existing visited set makes fallback plus same-target route resolve once. `ContextState.DelegationTarget` handles route-only delegation and same-target fallback plus route. Do not add any route check to `GetServices`, `ExecuteInterceptedRead`, `ExecuteInterceptedWrite`, or `ExecuteInterceptedInvoke`.

Update only Core comments that become inaccurate: the top-level topology snapshot, delegation,
service-walk, reverse using-graph, `ContextState.DelegationTarget`, and `ServiceWalkFrame` comments
must name public fallbacks plus the internal ownership route where both now participate. Keep the
existing delegation-cycle exception message byte-for-byte unchanged in PR 1 because fallback-only
production failures remain behavior-neutral. The internal route-cycle test asserts only the stable
`delegation cycle` phrase.

- [ ] **Step 8: Run focused tests and verify GREEN**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~ContextOwnershipRouteTests|FullyQualifiedName~ContextServiceWalkOrderTests|FullyQualifiedName~ContextDelegationCycleTests|FullyQualifiedName~ContextDeepGraphTests"
```

Expected: all selected tests pass with zero warnings and zero failures.

- [ ] **Step 9: Run the complete Core test project**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj
```

Expected: exit 0, zero failed tests, and no `.received.txt` file.

- [ ] **Step 10: Perform the task self-review**

Inspect the task diff and verify all of the following:

```bash
git diff --check
git diff --stat HEAD
git diff -U40 HEAD -- src/Namotion.Interceptor/InterceptorSubjectContext.cs src/Namotion.Interceptor.Tests/Context
rg --files src -g '*.received.txt'
```

Confirm the base `ContextState` has no new instance field, route-free hot methods have no new branch,
every state publication preserves the descriptor, replacement state is allocated before reverse
registration, and the new ownership-route transition invokes no callback, factory, or virtual
method under `_mutationLock`.

- [ ] **Step 11: Commit the Core implementation**

```bash
git add src/Namotion.Interceptor/InterceptorSubjectContext.cs src/Namotion.Interceptor.Tests/Context/ContextOwnershipRouteTests.cs src/Namotion.Interceptor.Tests/Context/ContextServiceWalkOrderTests.cs src/Namotion.Interceptor.Tests/Context/ContextDeepGraphTests.cs
git commit -m "Add internal context ownership route"
```

## Task 2: Document the Permanent Context-Resolution Contract

**Files:**
- Create: `docs/design/context-resolution.md`

**Interfaces:**
- Consumes: the implemented internal descriptor, routed context state, exact transition, traversal order, and reverse dependency union from Task 1.
- Produces: the canonical internal mechanics document that PR 2 extends and user-facing documentation can reference without duplicating implementation facts.

- [ ] **Step 1: Create the internal context-resolution document**

Create `docs/design/context-resolution.md` with these exact sections and contracts:

```markdown
# Context Resolution

Namotion.Interceptor contexts resolve interceptors and coordination services through an immutable,
copy-on-write state. This document defines the internal relationship types, traversal order, and
cache invalidation rules used by Core. The ownership route is an internal foundation in the first
pull request and receives its production lifecycle owner in the following attachment pull request.

## Concepts and terms

- **Local services:** Services registered directly on the context being queried.
- **Fallback composition:** Public service composition created by `AddFallbackContext`. Existing
  lifecycle side effects remain until the attachment pull request separates them.
- **Ownership route:** One internal resolution relationship used later by explicit attachment or an
  active parent branch.
- **Ownership domain:** The plain configured context whose reference identity names one lifecycle
  domain.
- **Using context:** A context whose resolution depends on another context and must be invalidated
  when that dependency changes.

## Contract at a glance

- A query pins one immutable state and takes no context mutation lock.
- Resolution visits local services, public fallbacks in insertion order, then one ownership route.
- Ordering attributes override route order only where they declare a dependency.
- Repeated contexts and service instances are returned once according to the existing visited and
  distinct rules.
- Services, fallbacks, the ownership route, delegation, and caches belong to one published state.
- A route change compares the exact previous descriptor instance before it publishes.
- Reverse dependency registration remains while either a fallback or ownership route uses a target.
- Topology changes publish a cache-free state and invalidate every upstream using context.
- Traversal and invalidation are iterative and terminate on cyclic graphs.

## Immutable state and publication

Route-free contexts use the existing `ContextState`. A context with an ownership route uses a
derived state containing one immutable route descriptor. The descriptor contains the target and
ownership-domain references and also serves as the transition generation token.

Mutators serialize on one context's mutation lock, build the complete replacement state, register a
new reverse dependency before publication, publish once, conditionally remove the old reverse
dependency, and invalidate upstream contexts after releasing the mutation lock. Queries continue to
use one volatile state read.

## Resolution and delegation

The service walk is depth-first. Each entered context contributes local services, then each public
fallback, then its ownership route. The existing visited set cuts cycles and gives the earliest
route to a repeated context precedence.

An empty context delegates directly when it has one distinct target: one fallback, one ownership
route, or both relationships to the same target. Different fallback and ownership targets require
the normal service walk. A pure delegation cycle raises the existing delegation-cycle exception.

## Reverse dependencies and invalidation

`_usedByContexts` represents whether a source depends on a target, not how many relationship kinds
connect them. Removing a fallback must retain the reverse entry while an ownership route still uses
the target. Clearing a route must retain it while a fallback still uses the target.

Registration occurs before publication so the reverse set is always a superset of the true using
set. Conditional removal occurs after publication. An extra entry can cause a harmless invalidation;
a missing entry can preserve a stale compiled chain and is forbidden.

## Performance

Route-free contexts keep the existing base state layout. Cached service queries and steady-state
intercepted reads, writes, and invocations do not inspect an ownership descriptor. A route attempt
allocates its descriptor before the exact comparison; only a successful route publication uses the
derived routed state.

Timing comparisons on a development machine are diagnostic. Final performance acceptance uses the
stable benchmark machine and compares the exact pull request head with its exact base commit.
```

- [ ] **Step 2: Check documentation style and scope**

Run:

```bash
rg -n -P "\x{2014}|TBD|TODO" docs/design/context-resolution.md
git diff --check
```

Expected: `rg` has no matches and `git diff --check` has no output.

- [ ] **Step 3: Commit the documentation**

```bash
git add docs/design/context-resolution.md
git commit -m "Document internal context resolution"
```

## Task 3: Complete the PR 1 Release Gate

**Files:**
- Inspect only: all files changed since `868a4d109d53b24805c9ee180efbf5029ee12c1a`
- Report only: this plan's ignored SDD task report and final controller handoff

**Interfaces:**
- Consumes: Tasks 1 and 2 at a clean committed head.
- Produces: reproducible local verification evidence, exact external benchmark handoff, and a merge-readiness verdict with no uncommitted product changes.

- [ ] **Step 1: Verify worktree and diff hygiene**

Run:

```bash
git status --short
git diff --check 868a4d109d53b24805c9ee180efbf5029ee12c1a..HEAD
git diff --name-only 868a4d109d53b24805c9ee180efbf5029ee12c1a..HEAD
rg --files src -g '*.received.txt'
```

Expected: clean status; no diff-check output; no received snapshots; changed product files limited to Core context state, Core context tests, and internal documentation plus the approved roadmap, spec, and plan.

- [ ] **Step 2: Verify the Core Public API snapshot**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj --filter "FullyQualifiedName~VerifyChecksTests.PublicApi"
```

Expected: exit 0, one passing Public API test, no received file, and no snapshot edit.

- [ ] **Step 3: Run the complete focused Core suite**

Run:

```bash
dotnet test src/Namotion.Interceptor.Tests/Namotion.Interceptor.Tests.csproj
```

Expected: exit 0 and zero failures.

- [ ] **Step 4: Build the complete solution**

Run:

```bash
dotnet build src/Namotion.Interceptor.slnx
```

Expected: exit 0 with zero warnings and zero errors. A timeout or a nonzero exit with zero diagnostics is recorded as an infrastructure problem, not accepted as a successful build.

- [ ] **Step 5: Run the complete non-integration suite**

Run:

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: exit 0 and zero failed tests across every project.

- [ ] **Step 6: Verify package creation**

Run:

```bash
dotnet pack src/Namotion.Interceptor.slnx --no-restore
```

Expected: exit 0 with zero warnings and zero errors.

- [ ] **Step 7: Perform static allocation and hot-path analysis**

Run:

```bash
git diff -U80 868a4d109d53b24805c9ee180efbf5029ee12c1a..HEAD -- src/Namotion.Interceptor/InterceptorSubjectContext.cs
```

Record evidence for each claim:

- `InterceptorSubjectContext` has no new instance field.
- Base `ContextState` has no new instance field.
- Initial and route-free replacement states instantiate the base state.
- `GetServices`, `ExecuteInterceptedRead`, `ExecuteInterceptedWrite`, and `ExecuteInterceptedInvoke` retain their current route-free instruction shape and contain no ownership-route type test.
- The ownership-route type test occurs only during cold service traversal or mutation.
- No route-free production path creates a descriptor or routed state.
- The ownership-route transition executes no callback, factory, or public virtual method while
  `_mutationLock` is held.

Also record that PR 1 is an intentional small net production addition and identify each added block
with its invariant. The combined PR 1 plus PR 2 review must later account for deleted fallback and
lifecycle coupling, with no temporary compatibility bridge retained.

If source inspection cannot settle an instruction-level difference, produce diffable JIT output for both exact commits using the procedure in `docs/benchmarking.md`. Do not infer from timing noise.

- [ ] **Step 8: Record the external stable-machine benchmark handoff**

Record the exact outputs of:

```bash
git rev-parse 868a4d109d53b24805c9ee180efbf5029ee12c1a
git rev-parse HEAD
```

Ask the maintainer before handing off this comparison from a worktree outside the repository:

```powershell
pwsh scripts/benchmark.ps1 -Filter "*ContextDelegationDepthBenchmark*","*SubjectHierarchyBenchmark*","*ServiceOrderResolverBenchmark.LinearChain*" -LaunchCount 3 -BaseBranch 868a4d109d53b24805c9ee180efbf5029ee12c1a
```

`ServiceOrderResolverBenchmark.LinearChain` is the noise reference because its class setup does not create subjects or contexts. Final acceptance is no repeatable timing regression outside contemporaneous control-row noise and no new steady-state allocation. Do not run or trust the final timing gate on this development machine.

- [ ] **Step 9: Recheck final hygiene and report the gate honestly**

Run:

```bash
git status --short
git diff --check 868a4d109d53b24805c9ee180efbf5029ee12c1a..HEAD
rg --files src -g '*.received.txt'
```

Report exact commands, exit codes, test totals, current head, base, static performance findings, and whether the external stable-machine result is pending. Do not call the pull request fully performance-verified until the maintainer supplies that result.
