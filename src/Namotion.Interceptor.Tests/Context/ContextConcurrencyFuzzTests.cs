using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

using static Namotion.Interceptor.Tests.Context.ContextStateReflection;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// Randomized, model based concurrency fuzzing for <see cref="InterceptorSubjectContext"/>.
///
/// Each round builds a random fallback graph (including cycles, self references and multi parent
/// shapes), hammers it from several threads with topology mutations, service registrations,
/// service queries and intercepted property and method access, and then checks quiescent
/// consistency: once every worker joined, what each context resolves must equal what a
/// single threaded walk of the final graph says it should resolve. A mismatch means a cache
/// survived a topology change, which is the defect class that silently drops an interceptor from
/// a compiled chain.
///
/// The subject executors take part in the graph as ordinary nodes, so the caches of
/// <see cref="InterceptorExecutor"/> are fuzzed the same way as the caches of a plain context.
///
/// Chains of contexts without services take part as well, both the acyclic ones that a deep
/// subject graph produces and the pure delegation cycles that resolve to an exception rather than
/// to a service set. The oracle models both outcomes, so a round asserts not only what a topology
/// resolves but also which topology is rejected. Whether a given context sits on such a cycle
/// changes constantly while the workers run, which is why the workers tolerate that one exception
/// and only the final topology decides.
/// </summary>
public class ContextConcurrencyFuzzTests
{
    // Bounded for CI: many short rounds rather than few long ones, because each round is a fresh
    // random topology and topology diversity finds more than depth within one graph.
    //
    // The deep sweep this was validated with is Rounds = 1000, WorkerCount = 16 and
    // OperationsPerWorker = 600, which is 8000 topologies and roughly 77 million operations in
    // about nine minutes. Raise the three constants to re-run it; nothing else has to change.
    private const int Rounds = 30;
    private const int WorkerCount = 8;
    private const int OperationsPerWorker = 200;

    private const int MaxContextCount = 12;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The oracle that catches a permanently stale cache: after quiescence every context has to
    /// resolve exactly the services the final fallback graph contains, and a write, a read and a
    /// method call on a subject bound to it have to reach exactly the interceptors of that same
    /// graph. Service caches and compiled chain caches are separate, so both are asserted.
    /// </summary>
    [Theory]
    [InlineData(13)]
    [InlineData(1729)]
    [InlineData(65537)]
    [InlineData(271828)]
    [InlineData(-999999)]
    [InlineData(20260731)]
    [InlineData(-2147483647)]
    [InlineData(7654321)]
    public async Task WhenTopologyAndServicesAreMutatedConcurrently_ThenQuiescentResolutionMatchesFinalTopology(int seed)
    {
        var deepChains = 0;
        var rejectedQueries = 0;
        var maximumDepth = 0;
        var checkedCaches = 0;

        for (var round = 0; round < Rounds; round++)
        {
            // Arrange: the round seed fully determines the topology and every worker operation, so
            // a reported failure can be replayed by running this seed alone.
            var roundSeed = unchecked(seed * 7919 + round);
            var topology = BuildTopology(new Random(roundSeed));

            using var start = new ManualResetEventSlim(false);
            var workers = Enumerable
                .Range(0, WorkerCount)
                .Select(workerIndex => Task.Factory.StartNew(
                    () => RunWorker(topology, workerIndex, new Random(unchecked(roundSeed * 31 + workerIndex)), start),
                    TaskCreationOptions.LongRunning))
                .ToArray();

            // Act
            start.Set();
            await AsyncTestHelpers.WaitUntilAsync(
                () => workers.All(worker => worker.IsCompleted),
                WorkerTimeout,
                TimeSpan.FromMilliseconds(2),
                $"Concurrent context mutations did not finish. {topology.Describe(roundSeed)}");
            await Task.WhenAll(workers);

            // Assert
            AssertResolutionMatchesTopology(topology, roundSeed);
            AssertInterceptionMatchesTopology(topology, roundSeed);
            checkedCaches += AssertResolvedTerminalsMatchTopology(topology, roundSeed);

            foreach (var node in topology.Nodes)
            {
                var depth = topology.DelegationDepth(node);
                if (depth < 0)
                {
                    rejectedQueries++;
                }
                else if (depth >= 3)
                {
                    deepChains++;
                }

                maximumDepth = Math.Max(maximumDepth, depth);
            }
        }

        // Assert: the corpus has to contain the shapes this test claims to cover, otherwise it
        // passes by never building them. Both of these were absent before delegation chains
        // between contexts without services were allowed: the deepest chain the generator could
        // produce was two hops and a chain that resolves nothing could not occur at all.
        Assert.True(deepChains > 0,
            $"No final topology contained a delegation chain of three or more hops, so the corpus does not " +
            $"cover the shape a deep subject graph produces. Deepest chain seen: {maximumDepth}.");

        Assert.True(rejectedQueries > 0,
            "No final topology contained a context whose delegation chain is a cycle, so the corpus does not " +
            "cover the resolution that raises instead of returning services.");

        // The cache oracle skips a context whose chain was never walked, so without this it would
        // pass a run in which it never compared anything at all.
        Assert.True(checkedCaches > 0,
            "No context ended a round with a resolved delegation chain in its cache, so the oracle for that " +
            "cache compared nothing.");
    }

    /// <summary>
    /// The oracle for the resolved chain cache itself, rather than for what it produces. The two
    /// oracles above only see a wrong cached context when it happens to resolve different services,
    /// so a chain cached against a context that carries the same services would pass them while
    /// being stale. This compares the cache of every context against the final topology directly.
    ///
    /// A cache that is still empty is always legal, since nothing forces a chain to be walked. A
    /// cache that holds something has to agree exactly: the context the chain ends on, or the mark
    /// that it runs in a circle. It may hold something only if the state carrying it was installed
    /// after the last change below it, because a change replaces the state of every context above
    /// it, so the walk that filled it saw the final topology.
    /// </summary>
    private static int AssertResolvedTerminalsMatchTopology(Topology topology, int roundSeed)
    {
        var checkedCaches = 0;
        foreach (var node in topology.Nodes)
        {
            var cached = ResolvedTerminalField.GetValue(StateField.GetValue(node.Context));
            if (cached is null)
            {
                continue;
            }

            checkedCaches++;

            var depth = topology.DelegationDepth(node);
            if (depth < 0)
            {
                Assert.True(ReferenceEquals(cached, CyclicDelegationMarker),
                    $"Context {node.Name} resolves through a delegation cycle in the final topology but its cache " +
                    $"holds a context to resolve through. {topology.Describe(roundSeed)}");
                continue;
            }

            Assert.False(ReferenceEquals(cached, CyclicDelegationMarker),
                $"Context {node.Name} is marked as running in a circle but its chain ends after {depth} hops in " +
                $"the final topology. {topology.Describe(roundSeed)}");

            Assert.Same(topology.ResolveTerminal(node).Context, cached);
        }

        return checkedCaches;
    }

    private static void AssertResolutionMatchesTopology(Topology topology, int roundSeed)
    {
        foreach (var node in topology.Nodes)
        {
            if (topology.RejectsQuery(node))
            {
                var exception = Assert.Throws<InvalidOperationException>(() => node.Context.GetServices<MarkerService>());
                Assert.True(IsDelegationCycle(exception),
                    $"Context {node.Name} resolves through a delegation cycle in the final topology but raised " +
                    $"'{exception.Message}'. {topology.Describe(roundSeed)}");

                Assert.Throws<InvalidOperationException>(() => node.Context.GetServices<IWriteInterceptor>());
                Assert.Throws<InvalidOperationException>(() => node.Context.TryGetService<SingletonProbeService>());
                continue;
            }

            var reachableNodes = topology.ComputeReachableNodes(node);

            // Every service instance is unique and services are never removed, so the expected
            // count is simply the sum over the reachable part of the final graph.
            var expectedMarkerCount = reachableNodes.Sum(reachableNode => reachableNode.MarkerCount);
            var actualMarkers = node.Context.GetServices<MarkerService>();
            Assert.True(actualMarkers.Length == expectedMarkerCount,
                $"Context {node.Name} resolved {actualMarkers.Length} marker services but the final topology " +
                $"contains {expectedMarkerCount}. {topology.Describe(roundSeed)}");

            var expectedInterceptors = ExpectedInterceptors(reachableNodes);
            var actualInterceptors = node.Context.GetServices<IWriteInterceptor>();
            Assert.True(
                actualInterceptors.Length == expectedInterceptors.Count &&
                actualInterceptors.All(interceptor => expectedInterceptors.Contains((RecordingInterceptor)interceptor)),
                $"Context {node.Name} resolved the write interceptors {Format(actualInterceptors)} but the final " +
                $"topology contains {Format(expectedInterceptors)}. {topology.Describe(roundSeed)}");

            var expectsProbe = reachableNodes.Contains(topology.ProbeNode);
            var actualProbe = node.Context.TryGetService<SingletonProbeService>();
            Assert.True(expectsProbe == (actualProbe is not null),
                $"Context {node.Name} {(actualProbe is null ? "did not resolve" : "resolved")} the probe service " +
                $"but the final topology says it is {(expectsProbe ? "reachable" : "unreachable")}. " +
                topology.Describe(roundSeed));
        }
    }

    private static void AssertInterceptionMatchesTopology(Topology topology, int roundSeed)
    {
        var writtenValue = 0;
        foreach (var node in topology.SubjectNodes)
        {
            var subject = node.Subject!;
            if (topology.RejectsQuery(node))
            {
                // Nothing resolves for this executor, so no compiled chain exists to run and every
                // intercepted operation on its subject raises instead.
                var exception = Assert.Throws<InvalidOperationException>(() => subject.Value = ++writtenValue);
                Assert.True(IsDelegationCycle(exception),
                    $"An intercepted write on the subject of {node.Name} raised '{exception.Message}' but its " +
                    $"delegation chain is a cycle in the final topology. {topology.Describe(roundSeed)}");

                Assert.Throws<InvalidOperationException>(() => _ = subject.Value);
                Assert.Throws<InvalidOperationException>(() => subject.Echo(1));
                continue;
            }

            var expectedInterceptors = ExpectedInterceptors(topology.ComputeReachableNodes(node));

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "int write",
                interceptor => interceptor.WriteCount, () => subject.Value = ++writtenValue);

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "string write",
                interceptor => interceptor.WriteCount, () => subject.Text = roundSeed.ToString());

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "read",
                interceptor => interceptor.ReadCount, () => _ = subject.Value);

            AssertInterceptorsAreCalledOnce(topology, node, expectedInterceptors, roundSeed, "method",
                interceptor => interceptor.MethodCount, () => subject.Echo(1));
        }
    }

    /// <summary>
    /// Runs one intercepted operation on the subject of the given executor and asserts that exactly
    /// the interceptors of the final topology observed it, each of them exactly once.
    /// </summary>
    private static void AssertInterceptorsAreCalledOnce(
        Topology topology,
        ContextNode node,
        HashSet<RecordingInterceptor> expectedInterceptors,
        int roundSeed,
        string operation,
        Func<RecordingInterceptor, int> counter,
        Action trigger)
    {
        var countsBefore = topology.Interceptors.ToDictionary(interceptor => interceptor, counter);

        trigger();

        foreach (var interceptor in topology.Interceptors)
        {
            var expectedCalls = expectedInterceptors.Contains(interceptor) ? 1 : 0;
            var actualCalls = counter(interceptor) - countsBefore[interceptor];
            Assert.True(actualCalls == expectedCalls,
                $"An intercepted {operation} on the subject of {node.Name} reached the interceptor of context " +
                $"{interceptor.Index} {actualCalls} times instead of {expectedCalls}, so its compiled chain " +
                $"disagrees with the final topology. {topology.Describe(roundSeed)}");
        }
    }

    private static HashSet<RecordingInterceptor> ExpectedInterceptors(HashSet<ContextNode> reachableNodes)
    {
        return reachableNodes
            .Select(reachableNode => reachableNode.Interceptor)
            .OfType<RecordingInterceptor>()
            .ToHashSet();
    }

    private static string Format(IEnumerable<object> interceptors)
    {
        var indices = interceptors.OfType<RecordingInterceptor>().Select(interceptor => interceptor.Index).Order();
        return $"[{string.Join(", ", indices)}]";
    }

    private static Topology BuildTopology(Random random)
    {
        var contextCount = random.Next(2, MaxContextCount + 1);
        var nodes = new List<ContextNode>();

        for (var index = 0; index < contextCount; index++)
        {
            // A context that keeps a service of its own never becomes a delegation target, which
            // is what keeps cycles in the generated graph legal (see the class comment).
            var hasOwnService = index == 0 || random.Next(4) != 0;
            var context = InterceptorSubjectContext.Create();

            RecordingInterceptor? interceptor = null;
            if (hasOwnService)
            {
                interceptor = new RecordingInterceptor(index);
                context.AddService(interceptor);
            }

            if (index == 0)
            {
                // One instance, reachable from every context that resolves through this one, so
                // TryGetService exercises the arity path against a service the walk reaches over
                // many routes at once. It does NOT detect a service admitted twice: the walk
                // deduplicates by reference before returning, so one instance registered any
                // number of times still resolves to one. Detecting that needs two instances that
                // compare equal, which no worker here creates.
                context.AddService(new SingletonProbeService());
            }

            nodes.Add(new ContextNode($"c{index}", context, interceptor, hasOwnService, null));
        }

        // The executor of a subject is a context too, so it joins the graph as a node whose
        // fallbacks are fuzzed like any other.
        var contextNodes = nodes.ToArray();
        var subjectCount = random.Next(1, contextCount + 1);
        for (var index = 0; index < subjectCount; index++)
        {
            var subject = new ContextProbeSubject();
            var executor = (InterceptorSubjectContext)((IInterceptorSubject)subject).Context;
            nodes.Add(new ContextNode($"s{index}", executor, null, false, subject));
        }

        // Contexts that never receive a service, so they keep delegating for the whole round. They
        // are chained head to tail below, which is what puts a delegation chain of more than one
        // hop in the corpus: one proxy per level is exactly what an attached subject graph builds.
        var proxyNodes = new List<ContextNode>();
        var proxyCount = random.Next(0, 7);
        for (var index = 0; index < proxyCount; index++)
        {
            var proxy = new ContextNode($"p{index}", InterceptorSubjectContext.Create(), null, false, null, isProxy: true);
            nodes.Add(proxy);
            proxyNodes.Add(proxy);
        }

        // Candidate and binding edges may point at a proxy too, which is what lets a chain close
        // into the pure delegation cycle that resolves nothing at all.
        var targetNodes = contextNodes.Concat(proxyNodes).ToArray();

        var edges = new List<Edge>();
        var declaredEdges = new HashSet<(ContextNode Source, ContextNode Target)>();

        void DeclareEdge(ContextNode source, ContextNode target, bool isPresent)
        {
            if (declaredEdges.Add((source, target)))
            {
                edges.Add(new Edge(source, target, isPresent));
            }
        }

        // Force at least one back reference so that every round exercises a cycle, which is the
        // shape the registry produces for parent links.
        var servingNodes = contextNodes.Where(node => node.HasOwnService).ToArray();
        if (servingNodes.Length >= 2)
        {
            var first = servingNodes[random.Next(servingNodes.Length)];
            var second = servingNodes[random.Next(servingNodes.Length)];
            if (first != second)
            {
                DeclareEdge(first, second, true);
                DeclareEdge(second, first, true);
            }
        }

        // The proxy chain, head to tail. The tail is left open on purpose: whether it reaches a
        // context with services, runs back into the chain as a pure cycle, or gains a second
        // fallback and stops delegating is left to the candidate edges and to the workers.
        for (var index = 0; index + 1 < proxyNodes.Count; index++)
        {
            DeclareEdge(proxyNodes[index], proxyNodes[index + 1], true);
        }

        if (proxyNodes.Count != 0)
        {
            // In some rounds the tail points back into the chain, which closes it into the pure
            // delegation cycle that resolves nothing; in the rest it ends on a context that can
            // answer. Whether the edge starts out present is left to chance, and a worker owning
            // it then breaks and reforms the cycle underneath the queries of the other workers.
            var target = random.Next(3) == 0
                ? proxyNodes[random.Next(proxyNodes.Count)]
                : contextNodes[random.Next(contextNodes.Length)];

            DeclareEdge(proxyNodes[^1], target, random.Next(5) != 0);

            // One proxy gains and loses a second fallback context while the workers run, so it
            // swings between delegating and not. That is what opens the window in which a context
            // is invalidated out of the order of its own chain: removing a fallback context
            // publishes before it unregisters, so for a moment the context both delegates and still
            // sits in the using set it is leaving, and an invalidation can arrive over that entry
            // before it reaches the contexts further down.
            var swinging = proxyNodes[random.Next(proxyNodes.Count)];
            DeclareEdge(swinging, targetNodes[random.Next(targetNodes.Length)], false);
        }

        // Every subject starts out bound to one context, like a subject constructed with a context.
        // Binding to the head of the proxy chain is what makes an intercepted read, write or method
        // call resolve through the whole chain, which is the hot path a deep subject graph takes.
        for (var index = contextNodes.Length; index < nodes.Count; index++)
        {
            if (nodes[index].Subject is null)
            {
                continue;
            }

            var target = proxyNodes.Count != 0 && random.Next(2) == 0
                ? proxyNodes[0]
                : contextNodes[random.Next(contextNodes.Length)];

            DeclareEdge(nodes[index], target, true);
        }

        var candidateCount = random.Next(nodes.Count, nodes.Count * 2 + 1);
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var source = nodes[random.Next(nodes.Count)];
            var target = targetNodes[random.Next(targetNodes.Length)];

            // A proxy keeps exactly the one fallback context declared above, because a second one
            // stops it from delegating and would cut every chain back to a single hop. Pointing at
            // a proxy stays allowed, so chains still gain branches from above.
            if (source.IsProxy)
            {
                continue;
            }

            if (source == target && random.Next(10) != 0)
            {
                continue;
            }

            DeclareEdge(source, target, random.Next(5) != 0);
        }

        // Each edge belongs to at most one worker, so no two threads toggle the same edge and the
        // final edge set stays exactly known without weakening the concurrency.
        foreach (var edge in edges)
        {
            edge.Owner = random.Next(10) < 7 ? random.Next(WorkerCount) : -1;
            if (!edge.IsPresent)
            {
                continue;
            }

            try
            {
                edge.Source.Context.AddFallbackContext(edge.Target.Context);
            }
            catch (InvalidOperationException exception) when (IsDelegationCycle(exception))
            {
                // The executor resolves the lifecycle interceptors before publishing anything, so
                // a raise leaves no edge behind and the model has to record its absence.
                edge.IsPresent = false;
            }
        }

        return new Topology(nodes.ToArray(), edges, contextNodes[0]);
    }

    private static void RunWorker(Topology topology, int workerIndex, Random random, ManualResetEventSlim start)
    {
        var ownedEdges = topology.Edges.Where(edge => edge.Owner == workerIndex).ToArray();
        var nodes = topology.Nodes;
        var subjectNodes = topology.SubjectNodes;

        start.Wait();

        for (var operation = 0; operation < OperationsPerWorker; operation++)
        {
            var node = nodes[random.Next(nodes.Length)];
            var subject = subjectNodes[random.Next(subjectNodes.Length)].Subject!;
            var choice = random.Next(100);

            try
            {
                RunOperation(ownedEdges, node, subject, choice, operation, random);
            }
            catch (InvalidOperationException exception) when (IsDelegationCycle(exception))
            {
                // The one outcome a legal topology produces that is not a value: a context whose
                // delegation chain is a circle at this instant resolves nothing. Which contexts
                // those are changes with every edge the workers toggle, so it cannot be predicted
                // here; the oracles decide it against the final topology once everyone joined.
                // Every other InvalidOperationException still fails the round, in particular the
                // arity check of TryGetService reporting more than one match.
            }
        }
    }

    private static bool IsDelegationCycle(InvalidOperationException exception)
    {
        return exception.Message.Contains("delegation cycle", StringComparison.Ordinal);
    }

    private static void RunOperation(
        Edge[] ownedEdges,
        ContextNode node,
        ContextProbeSubject subject,
        int choice,
        int operation,
        Random random)
    {
        if (choice < 18 && ownedEdges.Length != 0)
        {
            var edge = ownedEdges[random.Next(ownedEdges.Length)];
            if (random.Next(2) == 0)
            {
                // Recorded after the call: the executor resolves the lifecycle interceptors before
                // publishing, so an add that raises on a circular chain leaves no edge. A raise
                // therefore always means the model was already false, because an add over an
                // existing edge short circuits before it can reach the resolve.
                edge.Source.Context.AddFallbackContext(edge.Target.Context);
                edge.IsPresent = true;
            }
            else
            {
                // Recorded before the call, the other way round: removal keeps the edge visible
                // for the detach callbacks and drops it from a finally, so a raise there still
                // means the edge is gone. Recording it afterwards would lose exactly that case.
                edge.IsPresent = false;
                edge.Source.Context.RemoveFallbackContext(edge.Target.Context);
            }
        }
        else if (choice < 30)
        {
            // A proxy keeps delegating for the whole round, so it is queried instead. Handing it a
            // service would end its chain, and with nearly every context receiving one over a few
            // hundred operations no chain would survive to the final topology at all.
            if (node.IsProxy)
            {
                _ = node.Context.GetServices<MarkerService>();
            }
            else
            {
                node.Context.AddService(new MarkerService());
                Interlocked.Increment(ref node.MarkerCount);
            }
        }
        else if (choice < 38)
        {
            // Whether this adds anything depends on the topology at that instant, so the return
            // value is what the model records. The service type is one the oracles ignore, but
            // adding one stops a context from delegating, which decides whether it resolves or
            // raises.
            if (node.IsProxy)
            {
                _ = node.Context.TryGetService<SingletonProbeService>();
            }
            else if (node.Context.TryAddService(() => new TransientProbeService(), _ => true))
            {
                Interlocked.Increment(ref node.TransientServiceCount);
            }
        }
        else if (choice < 55)
        {
            _ = node.Context.GetServices<MarkerService>();
        }
        else if (choice < 68)
        {
            _ = node.Context.GetServices<IWriteInterceptor>();
        }
        else if (choice < 74)
        {
            _ = node.Context.TryGetService<SingletonProbeService>();
        }
        else if (choice < 84)
        {
            subject.Value = operation;
        }
        else if (choice < 90)
        {
            subject.Text = null;
        }
        else if (choice < 96)
        {
            _ = subject.Value;
        }
        else
        {
            _ = subject.Echo(operation);
        }
    }

    private sealed class Topology(ContextNode[] nodes, List<Edge> edges, ContextNode probeNode)
    {
        internal ContextNode[] Nodes { get; } = nodes;

        internal ContextNode[] SubjectNodes { get; } = nodes.Where(node => node.Subject is not null).ToArray();

        internal List<Edge> Edges { get; } = edges;

        /// <summary>The one context holding the singleton probe service.</summary>
        internal ContextNode ProbeNode { get; } = probeNode;

        internal RecordingInterceptor[] Interceptors { get; } = nodes
            .Select(node => node.Interceptor)
            .OfType<RecordingInterceptor>()
            .ToArray();

        /// <summary>
        /// The single threaded model of the resolution semantics: the services a context resolves
        /// are the own services of every context reachable over the present fallback edges. A
        /// delegating context contributes nothing of its own, so modeling delegation separately
        /// would produce the same set and is left out.
        /// </summary>
        internal HashSet<ContextNode> ComputeReachableNodes(ContextNode node)
        {
            var reachableNodes = new HashSet<ContextNode>();
            var pending = new Stack<ContextNode>();
            pending.Push(node);

            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (!reachableNodes.Add(current))
                {
                    continue;
                }

                foreach (var edge in Edges)
                {
                    if (edge.IsPresent && edge.Source == current)
                    {
                        pending.Push(edge.Target);
                    }
                }
            }

            return reachableNodes;
        }

        /// <summary>
        /// Models <c>ContextState.DelegationTarget</c>: a context without own services and with
        /// exactly one fallback context contributes nothing itself and resolves everything through
        /// that one context.
        /// </summary>
        private ContextNode? DelegationTarget(ContextNode node)
        {
            if (node.HasAnyService)
            {
                return null;
            }

            ContextNode? target = null;
            foreach (var edge in Edges)
            {
                if (!edge.IsPresent || edge.Source != node)
                {
                    continue;
                }

                if (target is not null)
                {
                    return null;
                }

                target = edge.Target;
            }

            return target;
        }

        /// <summary>
        /// The number of delegation hops from the given context to the one that resolves for it,
        /// or -1 when following them runs in a circle, in which case nothing resolves at all.
        /// </summary>
        internal int DelegationDepth(ContextNode node)
        {
            var visited = new HashSet<ContextNode>();
            var current = node;
            var depth = 0;

            while (true)
            {
                var target = DelegationTarget(current);
                if (target is null)
                {
                    return depth;
                }

                if (!visited.Add(current))
                {
                    return -1;
                }

                current = target;
                depth++;
            }
        }

        /// <summary>
        /// Whether querying this context raises instead of resolving, which happens exactly when
        /// its own delegation chain closes into a circle: every context on it resolves through the
        /// next one and none of them ever answers.
        ///
        /// A circle that is merely reachable as one of several fallback contexts does not count.
        /// The collecting walk cuts it at the first context it has already visited and it
        /// contributes nothing, which is the same result it would contribute anyway: every context
        /// on such a circle is without services by construction.
        /// </summary>
        internal bool RejectsQuery(ContextNode node)
        {
            return DelegationDepth(node) < 0;
        }

        /// <summary>
        /// The context that answers for the given one, which is the end of its delegation chain.
        /// Only defined for a chain that does not run in a circle.
        /// </summary>
        internal ContextNode ResolveTerminal(ContextNode node)
        {
            var current = node;
            while (DelegationTarget(current) is { } target)
            {
                current = target;
            }

            return current;
        }

        internal string Describe(int roundSeed)
        {
            var shape = string.Join("; ", Nodes.Select(node =>
                $"{node.Name}{(node.HasOwnService ? "" : "*")}+{node.MarkerCount}" +
                $"{(DelegationDepth(node) is var depth && depth < 0 ? "!" : depth == 0 ? "" : $"~{depth}")}->" +
                $"[{string.Join(",", Edges.Where(edge => edge.IsPresent && edge.Source == node).Select(edge => edge.Target.Name))}]"));

            return $"Seed {roundSeed}, final topology ('c' is a context, 's' a subject executor, 'p' a proxy that " +
                   $"never receives services, '*' a node seeded without services, '+n' its added marker services, " +
                   $"'~n' the length of its delegation chain, '!' a chain that is a cycle): {shape}";
        }
    }

    private sealed class ContextNode(
        string name,
        InterceptorSubjectContext context,
        RecordingInterceptor? interceptor,
        bool hasOwnService,
        ContextProbeSubject? subject,
        bool isProxy = false)
    {
        /// <summary>
        /// Set for a context that never receives a service, so it keeps delegating for the whole
        /// round. Without those the workers hand a service to nearly every context long before
        /// they finish, and the final topology of a round contains no delegation at all, which is
        /// the shape a graph of attached subjects consists almost entirely of.
        /// </summary>
        internal bool IsProxy { get; } = isProxy;


        internal int MarkerCount;

        /// <summary>
        /// Set once <see cref="IInterceptorSubjectContext.TryAddService{TService}"/> reported that
        /// it added the transient probe service to this context. Its presence is not part of any
        /// service oracle, but it decides whether this context still delegates, so an unrecorded
        /// one would make the model disagree about which contexts resolve.
        /// </summary>
        internal int TransientServiceCount;

        internal bool HasAnyService => HasOwnService || MarkerCount != 0 || TransientServiceCount != 0;

        internal string Name { get; } = name;

        internal InterceptorSubjectContext Context { get; } = context;

        internal RecordingInterceptor? Interceptor { get; } = interceptor;

        internal bool HasOwnService { get; } = hasOwnService;

        /// <summary>Set when this node is the executor of a subject, otherwise <c>null</c>.</summary>
        internal ContextProbeSubject? Subject { get; } = subject;
    }

    private sealed class Edge(ContextNode source, ContextNode target, bool isPresent)
    {
        internal ContextNode Source { get; } = source;

        internal ContextNode Target { get; } = target;

        /// <summary>Written only by the owning worker and read after it joined.</summary>
        internal bool IsPresent { get; set; } = isPresent;

        /// <summary>Index of the worker that may toggle this edge, or -1 when it stays fixed.</summary>
        internal int Owner { get; set; } = -1;
    }

    private sealed class RecordingInterceptor(int index) : IReadInterceptor, IWriteInterceptor, IMethodInterceptor
    {
        private int _readCount;
        private int _writeCount;
        private int _methodCount;

        internal int Index { get; } = index;

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal int MethodCount => Volatile.Read(ref _methodCount);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _readCount);
            return next(ref context);
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _writeCount);
            next(ref context);
        }

        public object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next)
        {
            Interlocked.Increment(ref _methodCount);
            return next(ref context);
        }
    }

    private sealed class MarkerService;

    private sealed class SingletonProbeService;

    private sealed class TransientProbeService;
}

[InterceptorSubject]
public partial class ContextProbeSubject
{
    public partial int Value { get; set; }

    public partial string? Text { get; set; }

    protected int EchoWithoutInterceptor(int input) => input;
}
