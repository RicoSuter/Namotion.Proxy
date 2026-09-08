using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

internal static class SupportContractAssertions
{
    public static void Settled(IInterceptorSubjectContext context, IInterceptorSubject[] roots, params IInterceptorSubject[] subjects)
    {
        var errors = CompareStorageGraph(context, roots, subjects, "settled");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        foreach (var root in roots)
            Assert.NotEqual(SubjectAttachmentAnchorKind.None, root.Executor.AttachmentAnchor);
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
        var storedValues = new Dictionary<PropertyReference, object?>(PropertyReference.Comparer);
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
                storedValues.Add(property, value);
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
        foreach (var entry in storedValues)
        {
            if (!graph.HasBaseline(entry.Key) || !Equals(entry.Value, graph.GetBaseline(entry.Key)))
                errors.Add($"{phase}: {Edge(entry.Key, null)} baseline differs from storage");
        }
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
