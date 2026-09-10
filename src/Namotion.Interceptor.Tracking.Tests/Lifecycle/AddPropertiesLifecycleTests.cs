using System.Collections;
using System.Collections.Concurrent;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests for the lifecycle-aware <see cref="IInterceptorSubject.AddProperties"/> admission: one
/// call publishes metadata, initial ownership edges and property callbacks atomically, and a
/// rejected batch publishes nothing.
/// </summary>
public class AddPropertiesLifecycleTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static SubjectPropertyMetadata CreateScalarProperty(string name)
    {
        return new SubjectPropertyMetadata(
            name, typeof(string), [], _ => "value", null, isIntercepted: true, isDynamic: true);
    }

    private static SubjectPropertyMetadata CreateStructuralProperty(
        string name, Func<IInterceptorSubject, object?> getValue, params Attribute[] attributes)
    {
        return new SubjectPropertyMetadata(
            name, typeof(Person), attributes, getValue, null, isIntercepted: true, isDynamic: true);
    }

    [Fact]
    public void WhenPropertiesAreAddedToAnOwnedSubject_ThenTheInputSequenceIsEnumeratedExactlyOnce()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A"), CreateScalarProperty("B")]);

        // Act
        ((IInterceptorSubject)root).AddProperties(sequence);

        // Assert
        Assert.Equal(1, sequence.EnumerationCount);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("A"));
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("B"));
    }

    [Fact]
    public void WhenPropertiesAreAddedToAnUnattachedSubject_ThenTheInputSequenceIsEnumeratedExactlyOnce()
    {
        // Arrange
        var root = new Person { FirstName = "R" };
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A")]);

        // Act
        ((IInterceptorSubject)root).AddProperties(sequence);

        // Assert
        Assert.Equal(1, sequence.EnumerationCount);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("A"));
    }

    [Fact]
    public void WhenABatchNameCollidesWithAnExistingProperty_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var propertyCountBefore = subject.Properties.Count;
        var getterCalls = 0;
        var child = new Person { FirstName = "C" };
        var batch = new[]
        {
            CreateStructuralProperty("New", _ => { getterCalls++; return child; }),
            CreateScalarProperty("FirstName")
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // The whole batch is rejected before classification: no metadata, no getter call, no edge.
        Assert.Equal(propertyCountBefore, subject.Properties.Count);
        Assert.False(subject.Properties.ContainsKey("New"));
        Assert.Equal(0, getterCalls);
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenABatchContainsTheSameNameTwice_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => subject.AddProperties(CreateScalarProperty("X"), CreateScalarProperty("X")));

        // Assert
        Assert.False(subject.Properties.ContainsKey("X"));
    }

    [Fact]
    public void WhenAStructuralPropertyIsAdmitted_ThenItsGetterIsInvokedExactlyOnceAndTheChildAttaches()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        var getterCalls = 0;

        // Act
        ((IInterceptorSubject)root).AddProperties(
            CreateStructuralProperty("Extra", _ => { getterCalls++; return child; }));

        // Assert
        Assert.Equal(1, getterCalls);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenABatchMixesScalarAndStructuralProperties_ThenAllPublishTogetherAndHandlersRunInInputOrder()
    {
        // Arrange
        var recorder = new RecordingPropertyHandler();
        var context = CreateContext().WithService(() => recorder, _ => false);
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        recorder.Target = subject;
        var child = new Person { FirstName = "C" };

        // Act
        subject.AddProperties(
            CreateScalarProperty("Scalar1"),
            CreateStructuralProperty("Struct1", _ => child),
            CreateScalarProperty("Scalar2"));

        // Assert
        Assert.True(subject.Properties.ContainsKey("Scalar1"));
        Assert.True(subject.Properties.ContainsKey("Struct1"));
        Assert.True(subject.Properties.ContainsKey("Scalar2"));
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(["Scalar1", "Struct1", "Scalar2"], recorder.AttachedProperties);
    }

    [Fact]
    public void WhenACapturedValueContainsAForeignSubject_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var foreign = new Person(foreignContext) { FirstName = "F" };
        var innocent = new Person { FirstName = "I" };
        var batch = new[]
        {
            CreateStructuralProperty("Innocent", _ => innocent),
            CreateStructuralProperty("Foreign", _ => foreign)
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // The batch fails as one unit: neither property publishes and the innocent subject stays
        // unattached even though its own subgraph was valid.
        Assert.False(subject.Properties.ContainsKey("Innocent"));
        Assert.False(subject.Properties.ContainsKey("Foreign"));
        Assert.Null(innocent.TryGetContext());
        Assert.Same(foreignContext, foreign.TryGetContext());
    }

    [Fact]
    public void WhenACompetingContextClaimsADiscoveredSubject_ThenProvisionalClaimsAreReleased()
    {
        // Arrange: the trap subject simulates the competing claim deterministically. Its structural
        // getter runs while the admission walks the prospective component, which is exactly the
        // window between validation and claiming, and attaches the trap to another context there.
        var context = CreateContext();
        var foreignContext = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var innocent = new Person { FirstName = "I" };
        var trap = new ClaimTrapSubject();
        var competingClaimCommitted = false;
        trap.ChildGetter = _ =>
        {
            trap.ChildGetter = null;

            // The competing claim runs on its own thread, which is the shape it has in production:
            // a thread runs at most one topology transaction, so the admitting thread cannot enter
            // the foreign context's gate itself. Joining keeps the race deterministic, the claim is
            // committed before discovery walks on.
            var competitor = new Thread(() => trap.AttachToContext(foreignContext)) { IsBackground = true };
            competitor.Start();
            competingClaimCommitted = competitor.Join(TimeSpan.FromSeconds(30));
            return null;
        };

        var batch = new[]
        {
            new SubjectPropertyMetadata(
                "Trapped", typeof(IInterceptorSubject[]), [],
                _ => new IInterceptorSubject[] { innocent, trap },
                null, isIntercepted: true, isDynamic: true)
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // The innocent subject was provisionally claimed before the trap lost the race, so the
        // failed batch must hand its claim back; the trap keeps its competing attachment.
        Assert.True(competingClaimCommitted);
        Assert.False(subject.Properties.ContainsKey("Trapped"));
        Assert.Null(innocent.TryGetContext());
        Assert.Same(foreignContext, ((IInterceptorSubject)trap).TryGetContext());
    }

    [Fact]
    public void WhenAHandlerAddsPropertiesDuringItsOwnAttachCallback_ThenTheBatchIsAdmitted()
    {
        // Arrange
        var child = new Person { FirstName = "C" };
        var added = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach && !added)
                {
                    added = true;
                    change.Subject.AddProperties(CreateStructuralProperty("Extra", _ => child));
                }
            }), _ => false);

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Extra"));
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenACallbackAddsPropertiesToAnUnattachedSubject_ThenOnlyMetadataIsPublished()
    {
        // Arrange
        var unattached = new Person { FirstName = "U" };
        var getterCalls = 0;
        var added = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach && !added)
                {
                    added = true;
                    ((IInterceptorSubject)unattached).AddProperties(
                        CreateStructuralProperty("Extra", _ => { getterCalls++; return new Person(); }));
                }
            }), _ => false);

        // Act
        _ = new Person(context) { FirstName = "R" };

        // Assert: metadata only, no ownership work and no getter classification.
        Assert.True(((IInterceptorSubject)unattached).Properties.ContainsKey("Extra"));
        Assert.Null(unattached.TryGetContext());
        Assert.Equal(0, getterCalls);
    }

    [Fact]
    public void WhenACallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration()
    {
        // Arrange
        var contextB = CreateContext();
        var other = new Person(contextB) { FirstName = "B" };
        var otherPropertyCount = ((IInterceptorSubject)other).Properties.Count;
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A")]);
        var contextA = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach)
                {
                    ((IInterceptorSubject)other).AddProperties(sequence);
                }
            }), _ => false);

        // Act & Assert
        Assert.Throws<LifecycleContractViolationException>(() => new Person(contextA));

        // Assert: rejected before the input was enumerated and before anything published.
        Assert.Equal(0, sequence.EnumerationCount);
        Assert.Equal(otherPropertyCount, ((IInterceptorSubject)other).Properties.Count);
    }

    [Fact]
    public void WhenAPropertyCallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration()
    {
        // Arrange: property lifecycle callbacks are published under the topology gate like every
        // other lifecycle callback, so a cross-context AddProperties from one must be rejected
        // before it can block on the foreign topology gate.
        var contextB = CreateContext();
        var other = new Person(contextB) { FirstName = "B" };
        var otherPropertyCount = ((IInterceptorSubject)other).Properties.Count;
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A")]);
        var fired = false;
        var contextA = CreateContext()
            .WithService(() => new DelegatePropertyAttachHandler(_ =>
            {
                if (!fired)
                {
                    fired = true;
                    ((IInterceptorSubject)other).AddProperties(sequence);
                }
            }), _ => false);

        // Act & Assert
        Assert.Throws<LifecycleContractViolationException>(() => new Person(contextA));

        // Assert: rejected before the input was enumerated and before anything published.
        Assert.True(fired);
        Assert.Equal(0, sequence.EnumerationCount);
        Assert.Equal(otherPropertyCount, ((IInterceptorSubject)other).Properties.Count);
    }

    [Fact]
    public void WhenACallbackAddsPropertiesToTheSeededRootMidDescent_ThenTheNewEdgeIsEstablished()
    {
        // Arrange: an explicit attach seeds the root's baselines before owning it, then attaches
        // its children; a callback fired for a child that adds properties to the root therefore
        // lands on a claimed, seeded, not-yet-owned subject, and the new property's edge must
        // still be established.
        Person? root = null;
        Person? father = null;
        var newChild = new Person { FirstName = "N" };
        var added = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach && ReferenceEquals(change.Subject, father) && !added)
                {
                    added = true;
                    ((IInterceptorSubject)root!).AddProperties(
                        CreateStructuralProperty("Extra", _ => newChild));
                }
            }), _ => false);

        root = new Person { FirstName = "R" };
        father = new Person { FirstName = "F" };
        root.Father = father;

        // Act
        root.AttachToContext(context);

        // Assert: the batch published and its edge exists.
        Assert.True(added);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Extra"));
        Assert.Same(context, newChild.TryGetContext());
        Assert.Equal(1, newChild.GetReferenceCount());
        Assert.Equal(1, father.GetReferenceCount());

        // Act: a second parent edge re-enters the descent for the root, which re-checks the
        // seeded-baseline invariant; a subject whose new property had no baseline would be
        // re-seeded here and every edge would double.
        var parent2 = new Person(context) { FirstName = "P" };
        parent2.Children = [root];

        // Assert
        Assert.Equal(1, newChild.GetReferenceCount());
        Assert.Equal(1, father.GetReferenceCount());
    }

    [Fact]
    public void WhenAPropertyHandlerThrowsDuringAdmissionFanOut_ThenMetadataStaysPublishedAndClaimsAreReleased()
    {
        // Arrange: callbacks after the metadata swap are exception-free by contract; a violating
        // handler propagates with no rollback, so the metadata stays published while the claims
        // that never became ownership are handed back.
        var context = CreateContext()
            .WithService(() => new DelegatePropertyAttachHandler(change =>
            {
                if (change.Property.Name == "Poison")
                {
                    throw new InvalidOperationException("violating handler");
                }
            }), _ => false);
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var child = new Person { FirstName = "C" };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(
            () => subject.AddProperties(CreateStructuralProperty("Poison", _ => child)));

        // Assert
        Assert.Equal("violating handler", exception.Message);
        Assert.True(subject.Properties.ContainsKey("Poison"));
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAGetterAddsANonCollidingProperty_ThenBothBatchesPublish()
    {
        // Arrange: a getter mutating metadata violates the admission contract, but the
        // non-colliding shape settles deterministically (the nested batch publishes first) and is
        // pinned because it is the shape the reentrant-duplicate rejection is defined against.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var child = new Person { FirstName = "C" };

        // Act
        subject.AddProperties(CreateStructuralProperty("Outer", _ =>
        {
            subject.AddProperties(CreateScalarProperty("Nested"));
            return child;
        }));

        // Assert
        Assert.True(subject.Properties.ContainsKey("Outer"));
        Assert.True(subject.Properties.ContainsKey("Nested"));
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAGetterAddsACollidingProperty_ThenTheOuterBatchIsRejected()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var child = new Person { FirstName = "C" };
        var batch = new[]
        {
            CreateStructuralProperty("Outer", _ =>
            {
                subject.AddProperties(CreateScalarProperty("X"));
                return child;
            }),
            CreateScalarProperty("X")
        };

        // Act & Assert: the nested batch published "X" between materialization and publication,
        // so the outer batch is rejected at its publication recheck and publishes nothing.
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // Assert
        Assert.True(subject.Properties.ContainsKey("X"));
        Assert.False(subject.Properties.ContainsKey("Outer"));
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenAGetterWritesAScalarPropertyOfItsSubject_ThenTheBatchIsAdmitted()
    {
        // Arrange: scalar writes stay allowed everywhere, including from an admission getter.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var child = new Person { FirstName = "C" };

        // Act
        subject.AddProperties(CreateStructuralProperty("Extra", _ =>
        {
            root.LastName = "Mut";
            return child;
        }));

        // Assert
        Assert.Equal("Mut", root.LastName);
        Assert.True(subject.Properties.ContainsKey("Extra"));
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenADerivedStructuralPropertyIsAdmitted_ThenNoEdgeIsEstablishedAndTheGetterIsNotInvoked()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        var getterCalls = 0;

        // Act
        ((IInterceptorSubject)root).AddProperties(
            CreateStructuralProperty("Extra", _ => { getterCalls++; return child; }, new DerivedAttribute()));

        // Assert: the metadata publishes, but a derived property never establishes ownership.
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Extra"));
        Assert.Equal(0, getterCalls);
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenPropertiesAreAddedToAnUnattachedSubject_ThenALaterAttachDiscoversThem()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        var getterCalls = 0;
        ((IInterceptorSubject)root).AddProperties(
            CreateStructuralProperty("Extra", _ => { getterCalls++; return child; }));

        // The unattached publication performs no ownership work.
        Assert.Equal(0, getterCalls);
        Assert.Null(child.TryGetContext());

        // Act
        root.AttachToContext(context);

        // Assert: the attach discovers the then-current structural property through its getter.
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenABatchIsAdmitted_ThenThePublisherIsInvokedExactlyOnce()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var publisherCalls = 0;
        var registration = new SubjectPropertyRegistration(
            subject, [CreateScalarProperty("A")], _ => publisherCalls++);

        // Act
        subject.Executor.AddProperties(registration);

        // Assert
        Assert.Equal(1, publisherCalls);
    }

    [Fact]
    public void WhenABatchIsRejected_ThenThePublisherIsNeverInvoked()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var foreign = new Person(foreignContext) { FirstName = "F" };
        var publisherCalls = 0;

        var duplicateRegistration = new SubjectPropertyRegistration(
            subject, [CreateScalarProperty("FirstName")], _ => publisherCalls++);
        var foreignRegistration = new SubjectPropertyRegistration(
            subject, [CreateStructuralProperty("Foreign", _ => foreign)], _ => publisherCalls++);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.Executor.AddProperties(duplicateRegistration));
        Assert.Throws<InvalidOperationException>(() => subject.Executor.AddProperties(foreignRegistration));

        // Assert
        Assert.Equal(0, publisherCalls);
    }

    [Fact]
    public void WhenACapturedCollectionReleasesTheAdmittingSubjectMidCommit_ThenNoBaselineEntrySurvives()
    {
        // Arrange: the trap collection enumerates benignly during component discovery and releases
        // the admitting subject during the reconcile's occurrence collection, which is depth-zero
        // user code holding the gate reentrantly. The reconcile then bails on its ownership check,
        // and the batch's next captured value, a null, is committed by the admission directly.
        var context = CreateContext();
        var person = new Person { FirstName = "P" };
        person.AttachToContext(context);
        var subject = (IInterceptorSubject)person;
        var child = new Person { FirstName = "C" };
        var trap = new TrapEnumerable([child]);
        trap.OnSecondEnumeration = () => person.DetachFromContext(context);

        var batch = new[]
        {
            new SubjectPropertyMetadata(
                "Trap", typeof(IEnumerable<Person>), [], _ => trap, null,
                isIntercepted: true, isDynamic: true),
            CreateStructuralProperty("Extra", _ => null)
        };

        // Act
        subject.AddProperties(batch);

        // Assert: no baseline entry survives for the released subject. GetBaseline cannot
        // distinguish a committed null from no entry, so presence is asserted directly.
        Assert.Equal(2, trap.EnumerationCount);
        Assert.Null(subject.TryGetContext());
        Assert.Null(child.TryGetContext());
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        Assert.False(lifecycle.Graph.HasBaseline(new PropertyReference(subject, "Trap")));
        Assert.False(lifecycle.Graph.HasBaseline(new PropertyReference(subject, "Extra")));
    }

    /// <summary>
    /// A captured collection whose enumeration runs arbitrary code. The admission enumerates it
    /// once during component discovery and a second time inside the reconcile's occurrence
    /// collection; the release must fire on the second, because a release during discovery
    /// detaches the subject before the property attach callbacks and fails the whole admission.
    /// </summary>
    private sealed class TrapEnumerable(IReadOnlyList<Person> items) : IEnumerable<Person>
    {
        public int EnumerationCount { get; private set; }

        public Action? OnSecondEnumeration { get; set; }

        public IEnumerator<Person> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount == 2)
            {
                OnSecondEnumeration?.Invoke();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingMetadataSequence(IReadOnlyList<SubjectPropertyMetadata> inner)
        : IEnumerable<SubjectPropertyMetadata>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<SubjectPropertyMetadata> GetEnumerator()
        {
            EnumerationCount++;
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingPropertyHandler : IPropertyLifecycleHandler
    {
        public IInterceptorSubject? Target { get; set; }

        public List<string> AttachedProperties { get; } = [];

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, Target))
            {
                AttachedProperties.Add(change.Property.Name);
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }
    }

    /// <summary>
    /// A minimal hand-written subject whose structural getter can run arbitrary code, used to
    /// interleave a competing context claim deterministically inside the admission walk.
    /// </summary>
    private sealed class ClaimTrapSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private readonly Dictionary<string, SubjectPropertyMetadata> _properties;

        public Func<IInterceptorSubject, object?>? ChildGetter { get; set; }

        public ClaimTrapSubject()
        {
            _properties = new Dictionary<string, SubjectPropertyMetadata>
            {
                ["Child"] = new SubjectPropertyMetadata(
                    "Child", typeof(IInterceptorSubject), [],
                    subject => ((ClaimTrapSubject)subject).ChildGetter?.Invoke(subject),
                    null, isIntercepted: true, isDynamic: true)
            };
        }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => _properties;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
            => throw new NotSupportedException();
    }
}
