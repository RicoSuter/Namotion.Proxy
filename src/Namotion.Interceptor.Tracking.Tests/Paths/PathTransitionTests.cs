using System;
using System.Collections.Generic;
using System.Reflection;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathTransitionTests
{
    public PathTransitionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSubscribedToResolvedPath_ThenOneListenerPerSegmentIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };

        // Act
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Assert: two segments (Father, FirstName) => two per-property listeners.
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenIntermediateIsNull_ThenOnlyResolvablePrefixIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Father is null: the chain build stops after subscribing to Father.

        // Act
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        // Assert: only the resolvable prefix is installed (Father subscribed, FirstName not reached).
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenLeafWritten_ThenValueChangeDeliveredWithChainedValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };
        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act
        father.FirstName = "Jack";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Joe", change.OldState.GetValueOrDefault());
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());
        Assert.Equal(nameof(Person.FirstName), change.Cause.Property.Name);
    }

    [Fact]
    public void WhenSingleSegmentLeafWritten_ThenValueChangeDelivered()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { FirstName = "Joe" };
        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act
        person.FirstName = "Jack";

        // Assert: a single-segment path degenerates to a plain leaf watch.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Joe", change.OldState.GetValueOrDefault());
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenNullIntermediateAssigned_ThenHealDeliversPathChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context); // Father null
        SubjectPathChange<string?>? last = null;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => last = c, SubjectPathValidation.Full);

        // Act
        person.Father = new Person { FirstName = "Joe" };

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.False(last.Value.OldState.IsResolved);
        Assert.True(last.Value.NewState.IsResolved);
        Assert.Equal("Joe", last.Value.NewState.GetValueOrDefault());
        Assert.Equal(nameof(Person.Father), last.Value.Cause.Property.Name);
    }

    [Fact]
    public void WhenResolvedIntermediateNulled_ThenBreakDeliversPathChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        SubjectPathChange<string?>? last = null;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => last = c, SubjectPathValidation.Full);

        // Act
        person.Father = null;

        // Assert: resolved -> unresolved, with the last observed value as Old.
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.True(last.Value.OldState.IsResolved);
        Assert.Equal("Joe", last.Value.OldState.GetValueOrDefault());
        Assert.False(last.Value.NewState.IsResolved);
        Assert.Equal(nameof(Person.Father), last.Value.Cause.Property.Name);
    }

    [Fact]
    public void WhenIntermediateReassignedToDifferentSubject_ThenPathChangeWithNewLeaf()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        SubjectPathChange<string?>? last = null;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => last = c, SubjectPathValidation.Full);

        // Act
        person.Father = new Person { FirstName = "Jack" };

        // Assert: a reassigned intermediate diverges at the child position, so the leaf below is retracked.
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.Equal("Joe", last.Value.OldState.GetValueOrDefault());
        Assert.Equal("Jack", last.Value.NewState.GetValueOrDefault());
        Assert.Equal(nameof(Person.Father), last.Value.Cause.Property.Name);
    }

    [Fact]
    public void WhenIntermediateReassignedToSubjectWithEqualLeaf_ThenSuppressedButChainStaysLive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: divergent retrack, but the retracked leaf value equals the last observed one.
        var newFather = new Person { FirstName = "Joe" };
        person.Father = newFather;

        // Assert: nothing delivered for the equal-value transition.
        Assert.Empty(events);

        // Act: a real write to the retracked leaf must still deliver, proving the chain rebuilt live.
        newFather.FirstName = "Jack";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Joe", change.OldState.GetValueOrDefault());
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenSuppressedRetrackToDistinctButEqualLeaf_ThenBaselineAdvancesToNewInstance()
    {
        // A retrack whose new leaf value is a DISTINCT instance that is Equals-equal to the last observed
        // one is suppressed (nothing delivered), but the baseline still advances to the fresh instance so it
        // neither pins the departed instance nor later surfaces it as a stale OldState.
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context) { Father = new Person { FirstName = "Joe" } };
        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: retrack to a father whose leaf is a distinct string instance equal to the departed "Joe".
        var distinctButEqual = new string("Joe".ToCharArray());
        Assert.False(ReferenceEquals("Joe", distinctButEqual)); // guard: a genuinely distinct instance
        var newFather = new Person { FirstName = distinctButEqual };
        person.Father = newFather;

        // Assert: the equal-value transition delivers nothing.
        Assert.Empty(events);

        // Act: a real change on the retracked leaf delivers.
        newFather.FirstName = "Jack";

        // Assert: OldState is the retracked (newFather's) instance, not the departed original "Joe"; the
        // baseline advanced on the suppressed retrack.
        var change = Assert.Single(events);
        Assert.Equal("Jack", change.NewState.GetValueOrDefault());
        Assert.Same(distinctButEqual, change.OldState.GetValueOrDefault());
    }

    [Fact]
    public void WhenCollectionReplacedMovingDifferentSubjectToIndex_ThenPathChangeWithBothValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childA = new Person { FirstName = "Amy" };
        var childB = new Person { FirstName = "Bob" };
        var person = new Person(context) { Children = [childA] };
        SubjectPathChange<string?>? last = null;
        using var subscription = person.SubscribeToPath(x => x.Children[0].FirstName,
            (in SubjectPathChange<string?> c) => last = c, SubjectPathValidation.Full);

        // Act
        person.Children = [childB];

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.Equal("Amy", last.Value.OldState.GetValueOrDefault());
        Assert.Equal("Bob", last.Value.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenCollectionReplacedLeavingSameSubjectAndEqualLeaf_ThenSuppressedButChainStaysLive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childA = new Person { FirstName = "Amy" };
        var person = new Person(context) { Children = [childA] };
        var events = new List<SubjectPathChange<string?>>();
        using var subscription = person.SubscribeToPath(x => x.Children[0].FirstName,
            (in SubjectPathChange<string?> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: a fresh array holding the same child at the same index; the watched value is unchanged.
        person.Children = new[] { childA };

        // Assert: nothing delivered for the equal-value transition.
        Assert.Empty(events);

        // Act: a real write to the watched leaf must still deliver, proving the chain stayed live.
        childA.FirstName = "Ann";

        // Assert
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal("Amy", change.OldState.GetValueOrDefault());
        Assert.Equal("Ann", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDictionaryKeyAdded_ThenHealDeliversPathChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new Node(context); // ByName empty: "key" unresolved.
        SubjectPathChange<string>? last = null;
        using var subscription = node.SubscribeToPath(x => x.ByName["key"].Name,
            (in SubjectPathChange<string> c) => last = c, SubjectPathValidation.Full);

        // Act
        node.ByName = new Dictionary<string, Node> { ["key"] = new Node { Name = "Value" } };

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.False(last.Value.OldState.IsResolved);
        Assert.True(last.Value.NewState.IsResolved);
        Assert.Equal("Value", last.Value.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDictionaryKeyReplaced_ThenPathChangeWithBothValues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new Node(context) { ByName = new Dictionary<string, Node> { ["key"] = new Node { Name = "First" } } };
        SubjectPathChange<string>? last = null;
        using var subscription = node.SubscribeToPath(x => x.ByName["key"].Name,
            (in SubjectPathChange<string> c) => last = c, SubjectPathValidation.Full);

        // Act
        node.ByName = new Dictionary<string, Node> { ["key"] = new Node { Name = "Second" } };

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.Equal("First", last.Value.OldState.GetValueOrDefault());
        Assert.Equal("Second", last.Value.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenDictionaryKeyRemoved_ThenBreakDeliversPathChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new Node(context) { ByName = new Dictionary<string, Node> { ["key"] = new Node { Name = "First" } } };
        SubjectPathChange<string>? last = null;
        using var subscription = node.SubscribeToPath(x => x.ByName["key"].Name,
            (in SubjectPathChange<string> c) => last = c, SubjectPathValidation.Full);

        // Act
        node.ByName = new Dictionary<string, Node>(); // key gone

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.True(last.Value.OldState.IsResolved);
        Assert.Equal("First", last.Value.OldState.GetValueOrDefault());
        Assert.False(last.Value.NewState.IsResolved);
    }

    [Fact]
    public void WhenPresentDictionaryKeyValueBecomesNull_ThenBreakDeliversPathChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new Node(context) { ByName = new Dictionary<string, Node> { ["key"] = new Node { Name = "First" } } };
        SubjectPathChange<string>? last = null;
        using var subscription = node.SubscribeToPath(x => x.ByName["key"].Name,
            (in SubjectPathChange<string> c) => last = c, SubjectPathValidation.Full);

        // Act: the key stays present but its value is null, so the leaf is unreachable.
        node.ByName = new Dictionary<string, Node> { ["key"] = null! };

        // Assert
        Assert.NotNull(last);
        Assert.Equal(SubjectPathChangeKind.PathChange, last!.Value.Kind);
        Assert.True(last.Value.OldState.IsResolved);
        Assert.False(last.Value.NewState.IsResolved);
    }

    [Fact]
    public void WhenCollectionRetracked_ThenOnlySuffixBelowChangeIsRebuilt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childA = new Person { FirstName = "Amy" };
        var childB = new Person { FirstName = "Bob" };
        var person = new Person(context) { Children = [childA] };
        using var subscription = person.SubscribeToPath(x => x.Children[0].FirstName, (in SubjectPathChange<string?> _) => { }, SubjectPathValidation.Full);

        var chain = typeof(SubjectPathSubscription<string?>)
            .GetField("_chain", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(subscription)!;
        var handlesField = chain.GetType()
            .GetField("_segmentHandles", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handles = (IDisposable?[])handlesField.GetValue(chain)!;
        var upperBefore = handles[0];
        var leafBefore = handles[1];

        // Act: the divergence is at the leaf's subject, so only the leaf listener is torn down and rebuilt.
        person.Children = [childB];

        // Assert
        var handlesAfter = (IDisposable?[])handlesField.GetValue(chain)!;
        Assert.Same(upperBefore, handlesAfter[0]);   // the segment above the change is untouched
        Assert.NotSame(leafBefore, handlesAfter[1]); // the leaf below the change was rebuilt
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount()); // net listener count preserved
    }

    [Fact]
    public void WhenBacklogStrandedByThrow_ThenSuppressedWriteStillDrainsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var father = new Person { FirstName = "Joe" };
        var person = new Person(context) { Father = father };

        var delivered = new List<SubjectPathChange<string?>>();
        var nestedWriteDone = false;
        using var subscription = person.SubscribeToPath(x => x.Father!.FirstName, (in SubjectPathChange<string?> change) =>
        {
            delivered.Add(change);
            if (change.NewState.GetValueOrDefault() == "Jack")
            {
                if (!nestedWriteDone)
                {
                    nestedWriteDone = true;
                    father.FirstName = "Zed"; // enqueues Jack->Zed into the drain
                }

                throw new InvalidOperationException("boom");
            }
        }, SubjectPathValidation.Full);

        // Act: the throwing callback strands the queued Jack->Zed event (drain abandoned, drainer flag reset).
        Assert.Throws<InvalidOperationException>(() => father.FirstName = "Jack");
        Assert.Single(delivered); // Joe->Jack delivered; Jack->Zed stranded

        // Act: a write whose observed transition is SUPPRESSED (Old == New) must still drain the backlog. A
        // new father carrying the same leaf value "Zed" retracks but the observed value equals the baseline.
        person.Father = new Person { FirstName = "Zed" };

        // Assert: the suppressed write delivered nothing of its own but recovered the stranded backlog.
        Assert.Equal(2, delivered.Count);
        Assert.Equal("Jack", delivered[1].OldState.GetValueOrDefault());
        Assert.Equal("Zed", delivered[1].NewState.GetValueOrDefault());
    }
}
