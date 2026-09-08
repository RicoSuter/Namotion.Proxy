using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackGraphOracleTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void WhenAChildGetterReassignsThePublishingProperty_ThenStorageAndAllIncomingOccurrencesAgree(
        bool retainPendingOccurrences, bool hasIndependentSupport, bool throwAfterNestedWrite)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new CallbackSpikeNode();
        root.AttachToContext(context);
        var pending = new CallbackSpikeNode();
        var shared = new CallbackSpikeNode { Child = pending };
        pending.Child = shared;
        if (hasIndependentSupport) root.Child = shared;
        var trigger = new CallbackSpikeSegmentWrapper();
        var branch = new CallbackSpikeNode { Payload = trigger };
        var replacement = new CallbackSpikeNode();
        object finalValue = retainPendingOccurrences ? new object[] { pending, pending, shared } : replacement;
        var failure = new InvalidOperationException("getter failed after the nested write committed");
        var reentered = false;
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            reentered = true;
            root.Payload = finalValue;
            if (throwAfterNestedWrite) throw failure;
        };
        IInterceptorSubject[] subjects = [root, pending, shared, trigger, branch, replacement];

        // Act
        var exception = Record.Exception(() => root.Payload = new object[] { branch, pending, pending });
        trigger.OnRead = null;

        // Assert
        Assert.True(reentered);
        Assert.Same(throwAfterNestedWrite ? failure : null, exception);
        Assert.Same(finalValue, root.Payload);
        var errors = CompareStorageGraph(context, [root], subjects, "after replacement");

        // Act
        root.DetachFromContext(context);

        // Assert
        errors.AddRange(CompareStorageGraph(context, [], subjects, "after teardown"));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenADetachCallbackReassignsTheSameProperty_ThenSharedCyclesAndReattachmentEpochsSettle(
        bool reattachOldSubject, bool throwAfterNestedWrite)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var shared = new CallbackSpikeNode();
        var cycleFirst = new CallbackSpikeNode();
        var cycleSecond = new CallbackSpikeNode { Child = cycleFirst };
        cycleFirst.Child = cycleSecond;
        var old = new CallbackSpikeNode { Payload = new object[] { shared, cycleFirst, shared } };
        var intermediate = new CallbackSpikeNode { Payload = new object[] { shared, cycleSecond } };
        var replacement = new CallbackSpikeNode { Payload = new object[] { shared, shared, cycleFirst } };
        var final = reattachOldSubject ? old : replacement;
        var root = new CallbackSpikeNode { Child = shared, Payload = old };
        root.AttachToContext(context);
        var failure = new InvalidOperationException("detach callback failed after the nested write committed");
        var reentered = false;
        var transitions = new List<(IInterceptorSubject Subject, bool Attached)>();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        lifecycle.SubjectAttached += change => transitions.Add((change.Subject, true));
        lifecycle.SubjectDetaching += change =>
        {
            transitions.Add((change.Subject, false));
            if (reentered || !ReferenceEquals(change.Subject, old)) return;
            reentered = true;
            root.Payload = final;
            if (throwAfterNestedWrite) throw failure;
        };
        IInterceptorSubject[] subjects = [root, old, intermediate, replacement, shared, cycleFirst, cycleSecond];

        // Act
        var exception = Record.Exception(() => root.Payload = intermediate);

        // Assert
        Assert.True(reentered);
        Assert.Same(throwAfterNestedWrite ? failure : null, exception);
        Assert.Same(final, root.Payload);
        var replacementTransitions = transitions.Where(change =>
            ReferenceEquals(change.Subject, old) || ReferenceEquals(change.Subject, intermediate) ||
            ReferenceEquals(change.Subject, replacement)).ToArray();
        Assert.Equal(new (IInterceptorSubject Subject, bool Attached)[]
        {
            (old, false), (intermediate, true), (intermediate, false), (final, true)
        }, replacementTransitions);
        var errors = CompareStorageGraph(context, [root], subjects, "after callback replacement");

        // Act
        root.DetachFromContext(context);

        // Assert
        errors.AddRange(CompareStorageGraph(context, [], subjects, "after teardown"));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static List<string> CompareStorageGraph(
        IInterceptorSubjectContext context,
        IInterceptorSubject[] roots,
        IInterceptorSubject[] universe,
        string phase)
    {
        var errors = new List<string>();
        var reachable = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var incoming = new Dictionary<IInterceptorSubject, List<string>>(ReferenceEqualityComparer.Instance);
        var outgoing = new Dictionary<PropertyReference, List<string>>(PropertyReference.Comparer);
        var pending = new Stack<IInterceptorSubject>(roots);
        string Identity(IInterceptorSubject subject)
        {
            var index = Array.FindIndex(universe, candidate => ReferenceEquals(candidate, subject));
            return index < 0 ? "unexpected subject" : $"subject[{index}]";
        }
        string Edge(PropertyReference property, object? index) => $"{Identity(property.Subject)}.{property.Name}[{index ?? "-"}]";
        string Child(IInterceptorSubject subject, object? index) => $"{Identity(subject)}[{index ?? "-"}]";
        void Collect(PropertyReference property, IInterceptorSubject child, object? index)
        {
            if (!incoming.TryGetValue(child, out var parents)) incoming.Add(child, parents = []);
            parents.Add(Edge(property, index));
            outgoing[property].Add(Child(child, index));
            pending.Push(child);
        }
        while (pending.TryPop(out var subject))
        {
            if (!reachable.Add(subject)) continue;
            foreach (var entry in subject.Properties)
            {
                if (!entry.Value.IsIntercepted || !entry.Value.Type.CanContainSubjects()) continue;
                var property = new PropertyReference(subject, entry.Key);
                outgoing[property] = [];
                var value = entry.Value.GetValue?.Invoke(subject);
                if (value is IInterceptorSubject child) Collect(property, child, null);
                else if (value is IEnumerable sequence && value is not string)
                {
                    var index = 0;
                    foreach (var item in sequence)
                    {
                        if (item is IInterceptorSubject element) Collect(property, element, index);
                        index++;
                    }
                }
            }
        }
        var graph = context.TryGetLifecycleInterceptor()!.Graph;
        var registry = context.GetService<ISubjectRegistry>();
        void EqualEdges(IEnumerable<string> expected, IEnumerable<string> actual, string label)
        {
            var expectedEdges = expected.Order().ToArray();
            var actualEdges = actual.Order().ToArray();
            if (!expectedEdges.SequenceEqual(actualEdges))
                errors.Add($"{phase}: {label}; expected [{string.Join(", ", expectedEdges)}], actual [{string.Join(", ", actualEdges)}]");
        }
        foreach (var subject in universe)
        {
            var shouldBeOwned = reachable.Contains(subject);
            if (graph.IsOwned(subject) != shouldBeOwned)
                errors.Add($"{phase}: {Identity(subject)} ownership should be {shouldBeOwned}");
            if (ReferenceEquals(subject.TryGetContext(), context) != shouldBeOwned)
                errors.Add($"{phase}: {Identity(subject)} context attachment should be {shouldBeOwned}");
            var expectedParents = incoming.GetValueOrDefault(subject) ?? [];
            if (subject.GetReferenceCount() != expectedParents.Count)
                errors.Add($"{phase}: {Identity(subject)} expected {expectedParents.Count} references, actual {subject.GetReferenceCount()}");
            EqualEdges(expectedParents, subject.GetParents().Select(parent => Edge(parent.Property, parent.Index)), Identity(subject) + " parents");
            var registered = registry.TryGetRegisteredSubject(subject);
            if ((registered is not null) != shouldBeOwned)
                errors.Add($"{phase}: {Identity(subject)} Registry membership should be {shouldBeOwned}");
            if (registered is not null)
            {
                EqualEdges(expectedParents, registered.Parents.Select(parent => Edge(parent.Property.Reference, parent.Index)), Identity(subject) + " Registry parents");
                foreach (var property in registered.Properties)
                {
                    if (!property.CanContainSubjects) continue;
                    EqualEdges(outgoing.GetValueOrDefault(property.Reference) ?? [],
                        property.Children.Select(child => Child(child.Subject, child.Index)), Edge(property.Reference, null) + " Registry children");
                }
            }
            if (!shouldBeOwned)
            {
                foreach (var name in subject.Properties.Keys)
                    if (graph.HasBaseline(new PropertyReference(subject, name)))
                        errors.Add($"{phase}: {Identity(subject)} has a released baseline for {name}");
                if (subject.Executor.AttachmentAnchor != SubjectAttachmentAnchorKind.None || graph.IsReleasing(subject))
                    errors.Add($"{phase}: {Identity(subject)} retains an anchor or release marker");
            }
        }
        EqualEdges(reachable.Select(Identity), registry.KnownSubjects.Keys.Select(Identity), "complete Registry membership");
        return errors;
    }
}
