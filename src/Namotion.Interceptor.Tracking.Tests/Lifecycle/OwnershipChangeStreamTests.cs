using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Characterization tests for the ordered lifecycle change stream. They record what the current
/// implementation publishes, in order, for the graph shapes whose outcome depends on
/// <c>OwnershipGraph.CommitsEdgeTo</c>. Every one of these shapes converges to the same final graph
/// state whether or not that predicate discriminates, so only the intermediate stream distinguishes
/// them and only an ordered assertion can catch a change.
///
/// The mechanism they surround: a reconcile commits the property's new value as the baseline before
/// it updates any incoming edge record, so during the removal pass a recorded incoming edge can name
/// a parent whose committed value no longer contains the subject. The reachability walk rejects such
/// an edge, which is what decides whether a subject is released on the removal that orphans it or
/// several removals later.
///
/// Both callers of the walk are covered. Release asks whether a subject that lost an edge is still
/// held; anchor adoption asks whether a new edge's parent is supported independently of the
/// subject's own provisional anchor. The adoption case reaches the same window only through a
/// nested operation, and there the predicate decides the final graph state rather than only the
/// order in which it is announced.
///
/// Notification order is contract, so these tests exist to make a change to it a deliberate decision
/// rather than an accident. A failure here is not automatically a defect: it means the published
/// order moved, and the new order has to be reviewed and the expectation updated on purpose.
/// </summary>
public class OwnershipChangeStreamTests
{
    /// <summary>Records every lifecycle change in publication order as one readable line each.</summary>
    private sealed class LifecycleChangeStreamRecorder : ILifecycleHandler
    {
        private readonly List<string> _changes = [];

        public IReadOnlyList<string> Changes => _changes;

        /// <summary>
        /// Runs after the change has been recorded, so anything a nested operation publishes is
        /// appended behind the change that triggered it rather than in front of it.
        /// </summary>
        public Action<SubjectLifecycleChange>? OnChange { get; set; }

        public void Clear() => _changes.Clear();

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            var transitions = new List<string>(2);
            if (change.IsContextAttach)
            {
                transitions.Add("attached");
            }

            if (change.IsPropertyReferenceAdded)
            {
                transitions.Add("edge added");
            }

            if (change.IsPropertyReferenceRemoved)
            {
                transitions.Add("edge removed");
            }

            if (change.IsContextDetach)
            {
                transitions.Add("detached");
            }

            var edge = change.Property is { } property ? $"{property.Name}[{change.Index ?? "-"}]" : "-";
            _changes.Add($"{change.Subject} {string.Join(", ", transitions)} {edge} references={change.ReferenceCount}");
            OnChange?.Invoke(change);
        }
    }

    private static object? GetCommittedBaseline(
        IInterceptorSubjectContext context, IInterceptorSubject subject, string propertyName)
    {
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        return lifecycle.Graph.GetBaseline(new PropertyReference(subject, propertyName));
    }

    private static IInterceptorSubjectContext CreateContext(LifecycleChangeStreamRecorder recorder)
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => recorder, _ => false);
    }

    /// <summary>
    /// Records existing behavior. Two occurrences of one subject in one collection, both dropped by
    /// one write: the first removal already orphans the subject, because the surviving record names
    /// a property whose committed value no longer contains it. The surplus edge is therefore drained
    /// from inside the release, before the detach, and the detach carries the occurrence that
    /// triggered it rather than the last one.
    /// </summary>
    [Fact]
    public void WhenEveryOrdinalOccurrenceOfOneSubjectIsRemovedInOneWrite_ThenTheSurplusEdgeDrainsBeforeTheDetach()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        root.Children = [child, child];
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "C edge removed Children[0] references=0",
            "C edge removed, detached Children[1] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior. The same shape at depth three, which shows the drained occurrences
    /// arriving in ascending index order with a falling reference count while the detach still
    /// carries the highest index.
    /// </summary>
    [Fact]
    public void WhenThreeOrdinalOccurrencesAreRemovedInOneWrite_ThenBothSurplusEdgesDrainBeforeTheDetach()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        root.Children = [child, child, child];
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "C edge removed Children[0] references=1",
            "C edge removed Children[1] references=0",
            "C edge removed, detached Children[2] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior for the keyed reconcile, which matches occurrences by key rather
    /// than by ordinal and walks the old occurrences in reverse. The subject is therefore orphaned
    /// on the last key in enumeration order, and the first key is drained from inside that release.
    ///
    /// Which key that is rests on <see cref="Dictionary{TKey,TValue}"/> enumerating in insertion
    /// order for a dictionary that has had no removals. That is a framework implementation detail
    /// rather than one of this codebase, so a different dictionary type here would legitimately
    /// swap the two keys below without anything in the lifecycle having changed.
    /// </summary>
    [Fact]
    public void WhenEveryKeyedOccurrenceOfOneSubjectIsRemovedInOneWrite_ThenTheDetachCarriesTheLastEnumeratedKey()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var holder = new KeyedChildrenHolder(context);
        var child = new Person { FirstName = "C" };
        holder.Children = new Dictionary<string, Person> { ["x"] = child, ["y"] = child };
        recorder.Clear();

        // Act
        holder.Children = new Dictionary<string, Person>();

        // Assert
        Assert.Equal(
        [
            "C edge removed Children[x] references=0",
            "C edge removed, detached Children[y] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior. Clearing a collection whose second entry is also held by its first
    /// entry: the walk up from that second entry reaches the sibling, and the sibling's own incoming
    /// edge is the stale one, so the sibling does not count as support. Both subjects therefore
    /// detach within this write, deepest first.
    /// </summary>
    [Fact]
    public void WhenAClearedCollectionLeavesASubjectHeldOnlyThroughASibling_ThenBothDetachInDescentOrder()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        root.Children = [first, second];
        first.Mother = second;
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "B edge removed Mother[-] references=0",
            "B edge removed, detached Children[1] references=0",
            "A edge removed, detached Children[0] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior for a closed cycle that loses its only external support. Each
    /// member is held by the other, so the walk only terminates because the cycle's edges into the
    /// cleared collection no longer count. The second member is released from inside the first
    /// member's descent rather than by the reconcile loop.
    /// </summary>
    [Fact]
    public void WhenAClearedCollectionLeavesACycleUnsupported_ThenBothCycleMembersDetach()
    {
        // Arrange
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person(context) { FirstName = "R" };
        var first = new Person { FirstName = "A" };
        var second = new Person { FirstName = "B" };
        root.Children = [first, second];
        first.Mother = second;
        second.Mother = first;
        recorder.Clear();

        // Act
        root.Children = [];

        // Assert
        Assert.Equal(
        [
            "B edge removed Mother[-] references=0",
            "B edge removed, detached Children[1] references=0",
            "A edge removed Children[0] references=0",
            "A edge removed, detached Mother[-] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior for the walk's second caller, anchor adoption, which reaches the
    /// same stale-edge window only through a nested operation. An outer reconcile is midway through
    /// its removal pass, so the collection's baseline no longer lists the host but the host's
    /// incoming record still exists. A detach callback fired by an earlier removal adds a dynamic
    /// property to that host, which is the supported dynamic-property-initializer case: the thread
    /// already holds the topology gate, so the admission is admitted rather than rejected. The
    /// admission attaches an edge to a subject that carries a provisional anchor, and adoption then
    /// walks up from the host through its dead incoming edge.
    ///
    /// Unlike the release-side cases above, the predicate decides more than the announcement order
    /// here: if that dead edge counted as support, the provisional anchor would be consumed, and the
    /// subject would then be released when the host is released a moment later instead of surviving
    /// as an anchored root.
    /// </summary>
    [Fact]
    public void WhenANestedAdmissionAdoptsAProvisionalRootUnderAStaleAncestorEdge_ThenTheAnchorSurvives()
    {
        // Arrange: the host sits at the lower index so the sibling is released first and its detach
        // callback runs while the host is still owned through an edge the baseline has already dropped.
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person { FirstName = "R" };
        ((IInterceptorSubject)root).AttachToContext(context);
        var host = new Person { FirstName = "H" };
        var sibling = new Person { FirstName = "S" };
        root.Children = [host, sibling];

        var provisionalRoot = new Person(context) { FirstName = "D" };

        var admissionRan = false;
        Exception? admissionException = null;
        var hostReferenceCountAtAdmission = -1;
        object? childrenBaselineAtAdmission = null;
        recorder.OnChange = change =>
        {
            if (!change.IsContextDetach || !ReferenceEquals(change.Subject, sibling))
            {
                return;
            }

            admissionRan = true;
            hostReferenceCountAtAdmission = ((IInterceptorSubject)host).GetReferenceCount();
            childrenBaselineAtAdmission = GetCommittedBaseline(context, root, nameof(Person.Children));
            admissionException = Record.Exception(() => ((IInterceptorSubject)host).AddProperties(
                new SubjectPropertyMetadata(
                    "Adopted", typeof(Person), [], _ => provisionalRoot, null,
                    isIntercepted: true, isDynamic: true)));
        };

        recorder.Clear();

        // Act
        root.Children = [];

        // Assert: the nested admission really ran, was accepted, and ran inside the window it needs.
        // The window is the disagreement itself: the host still carries the incoming edge that the
        // committed value has already stopped listing. Both halves are asserted, because the whole
        // shape depends on the host being released after the sibling rather than before it.
        Assert.True(admissionRan, "the detach callback for the released sibling never ran");
        Assert.Null(admissionException);
        Assert.Equal(1, hostReferenceCountAtAdmission);
        Assert.Empty(Assert.IsType<Person[]>(childrenBaselineAtAdmission));

        // Asserted ahead of the stream because it is the sharper consequence: the adopted subject
        // keeps the anchor it arrived with, so losing its only edge a moment later leaves it an
        // anchored root instead of releasing it. A dead ancestor edge counting as support would
        // change the committed graph, not only the order in which changes are announced.
        Assert.True(((IInterceptorSubject)provisionalRoot).Executor.AttachmentAnchor != SubjectAttachmentAnchorKind.None,
            "the provisional anchor was consumed by an ancestor edge the committed value no longer holds");
        Assert.Same(context, ((IInterceptorSubject)provisionalRoot).TryGetContext());
        Assert.Null(((IInterceptorSubject)host).TryGetContext());

        Assert.Equal(
        [
            "S edge removed, detached Children[1] references=0",
            "D edge added Adopted[-] references=1",
            "H edge removed, detached Children[0] references=0",
            "D edge removed Adopted[-] references=0"
        ], recorder.Changes);
    }

    /// <summary>
    /// Records existing behavior for a reparent onto a subject this context already owns. The target
    /// is held through a different property than the one being written, so the retention rule does
    /// not reach it: it is released by the cascade from the dropped subject and re-attached by the
    /// addition pass, and the committed graph is correct either way.
    ///
    /// Master does the same thing. Measured at 0418410c with <c>WithContextInheritance()</c>, which is
    /// the configuration whose attach descends into the subtree the way <c>WithLifecycle()</c> does
    /// here, master publishes five changes for this shape too, differing only in the detach order:
    /// <c>G detached, C detached, S detached, G attached, C attached</c>, deepest first where the
    /// sequence below is top-down. Master under a bare <c>WithLifecycle()</c> publishes two changes
    /// instead, but that is not a comparable configuration: it never attached the subtree at all, and
    /// the grandchild is left with reference count zero afterwards.
    /// </summary>
    [Fact]
    public void WhenAReparentTargetIsAlreadyOwned_ThenTheWholeChainDetachesAndReattaches()
    {
        // Arrange: a three level chain hanging off the edge that is about to be replaced.
        var recorder = new LifecycleChangeStreamRecorder();
        var context = CreateContext(recorder);
        var root = new Person { FirstName = "R" };
        ((IInterceptorSubject)root).AttachToContext(context);
        var stepchild = new Person { FirstName = "S" };
        var child = new Person { FirstName = "C" };
        var grandchild = new Person { FirstName = "G" };
        root.Mother = stepchild;
        stepchild.Father = child;
        child.Father = grandchild;
        recorder.Clear();

        // Act
        root.Mother = child;

        // Assert
        Assert.Equal(
        [
            "S edge removed, detached Mother[-] references=0",
            "C edge removed, detached Father[-] references=0",
            "G edge removed, detached Father[-] references=0",
            "G attached, edge added Father[-] references=1",
            "C attached, edge added Mother[-] references=1"
        ], recorder.Changes);
    }
}
