using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// The chain invariant: after every observed structural change the multiset of installed segment listeners
// equals EXACTLY the current resolved chain's segments, nothing stale left behind. These are white-box
// tests: they read the per-property listener arrays (ni.pcl) via PropertyChangeSubscription.ListenersKey
// and the process-wide PropertyChangeSubscriptions.ReadSubscriptionCount(). No production change is
// expected; a violated invariant would be a real listener leak.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathChainInvariantTests
{
    public PathChainInvariantTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenIntermediateReplaced_ThenReplacedSubjectAndBelowHoldNoListenerAndCountEqualsChainLength()
    {
        // Arrange: a three-segment path root -> mid -> leaf -> Name, so the chain installs one listener on
        // root."Child" (position 0), one on mid."Child" (position 1) and one on leaf."Name" (position 2).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var leaf = new Node { Name = "leaf" };
        var mid = new Node { Name = "mid", Child = leaf };
        var root = new Node(context) { Name = "root", Child = mid };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        Assert.Equal(1, ListenerCount(root, nameof(Node.Child)));
        Assert.Equal(1, ListenerCount(mid, nameof(Node.Child)));
        Assert.Equal(1, ListenerCount(leaf, nameof(Node.Name)));
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Act: replace the intermediate. Divergence is at position 1, so the suffix from position 1 (mid's
        // and leaf's listeners) is torn down and rebuilt onto the new subjects.
        var leaf2 = new Node { Name = "leaf2" };
        var mid2 = new Node { Name = "mid2", Child = leaf2 };
        root.Child = mid2;

        // Assert: the replaced subject and everything below it hold NO listener; the resolvable prefix keeps
        // its listener; the new suffix is installed; the process-wide count equals the current chain length.
        Assert.Equal(0, ListenerCount(mid, nameof(Node.Child)));
        Assert.Equal(0, ListenerCount(leaf, nameof(Node.Name)));
        Assert.Equal(1, ListenerCount(root, nameof(Node.Child)));
        Assert.Equal(1, ListenerCount(mid2, nameof(Node.Child)));
        Assert.Equal(1, ListenerCount(leaf2, nameof(Node.Name)));
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());

        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, change.Kind);
        Assert.Equal("leaf", change.OldState.GetValueOrDefault());
        Assert.Equal("leaf2", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenSubjectMovedOffPath_ThenWriteToItDeliversNothingEvenWhileStillNotifying()
    {
        // Arrange: a two-segment path root -> mid -> Name. A keeper root will re-adopt the moved-off subject
        // so it stays in a notifying context: proving the old chain is truly UNSUBSCRIBED, not merely dormant.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var mid = new Node { Name = "m0" };
        var root = new Node(context) { Name = "root", Child = mid };
        var keeper = new Node(context) { Name = "keeper" };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        Assert.Equal(1, ListenerCount(mid, nameof(Node.Name)));

        // Act: move mid off the path (retrack onto mid2), then re-adopt mid via the keeper so it is notifying.
        var mid2 = new Node { Name = "m2" };
        root.Child = mid2;
        keeper.Child = mid;

        // Assert: mid holds no leaf listener anymore (retracked off it).
        Assert.Equal(0, ListenerCount(mid, nameof(Node.Name)));
        Assert.Equal(1, ListenerCount(mid2, nameof(Node.Name)));
        var afterRetrack = events.Count; // the retrack itself delivered a PathChange (m0 -> m2)

        // Act: write the moved-off subject's leaf. It is notifying (keeper holds it) but has no listener, so
        // the path is delivered nothing: the old chain is genuinely unsubscribed, not filtered on delivery.
        mid.Name = "moved";
        Assert.Equal(afterRetrack, events.Count);

        // Act & Assert: a write to the newly resolved leaf still delivers, proving the new chain is live.
        mid2.Name = "m2b";
        Assert.Equal(afterRetrack + 1, events.Count);
        Assert.Equal("m2b", events[^1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenHealed_ThenNewLeafIsSubscribedAndDelivers()
    {
        // Arrange: an initially unresolved path (Child null), so only the resolvable prefix is installed.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Node(context) { Name = "root" };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount()); // only root."Child" is installed

        // Act: heal by assigning the intermediate.
        var child = new Node { Name = "c0" };
        root.Child = child;

        // Assert: the new leaf is subscribed (chain length two) and the heal delivered a PathChange.
        Assert.Equal(1, ListenerCount(child, nameof(Node.Name)));
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        var heal = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, heal.Kind);
        Assert.Equal("c0", heal.NewState.GetValueOrDefault());

        // Act & Assert: the newly resolved leaf is truly subscribed, so its write delivers.
        child.Name = "c1";
        Assert.Equal(2, events.Count);
        Assert.Equal(SubjectPathChangeKind.ValueChange, events[1].Kind);
        Assert.Equal("c1", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenReResolvedViaCollectionReplacement_ThenNewLeafIsSubscribedAndDelivers()
    {
        // Arrange: a collection segment path root -> Children[0] -> Name.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var a = new Node { Name = "a0" };
        var root = new Node(context) { Name = "root", Children = [a] };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Children[0].Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        Assert.Equal(1, ListenerCount(a, nameof(Node.Name)));

        // Act: replace the collection so index 0 resolves to a different subject.
        var b = new Node { Name = "b0" };
        root.Children = [b];

        // Assert: the old element is unsubscribed, the new one subscribed, and the retrack delivered.
        Assert.Equal(0, ListenerCount(a, nameof(Node.Name)));
        Assert.Equal(1, ListenerCount(b, nameof(Node.Name)));
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal("b0", Assert.Single(events).NewState.GetValueOrDefault());

        // Act & Assert: the newly resolved leaf delivers.
        b.Name = "b1";
        Assert.Equal(2, events.Count);
        Assert.Equal("b1", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenReResolvedViaDictionaryKeyReplacement_ThenNewLeafIsSubscribedAndDelivers()
    {
        // Arrange: a dictionary segment path root -> ByName["key"] -> Name.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var a = new Node { Name = "a0" };
        var root = new Node(context) { Name = "root", ByName = new Dictionary<string, Node> { ["key"] = a } };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.ByName["key"].Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        Assert.Equal(1, ListenerCount(a, nameof(Node.Name)));

        // Act: replace the dictionary so "key" maps to a different subject.
        var b = new Node { Name = "b0" };
        root.ByName = new Dictionary<string, Node> { ["key"] = b };

        // Assert: the old value is unsubscribed, the new one subscribed, and the retrack delivered.
        Assert.Equal(0, ListenerCount(a, nameof(Node.Name)));
        Assert.Equal(1, ListenerCount(b, nameof(Node.Name)));
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal("b0", Assert.Single(events).NewState.GetValueOrDefault());

        // Act & Assert: the newly resolved leaf delivers.
        b.Name = "b1";
        Assert.Equal(2, events.Count);
        Assert.Equal("b1", events[1].NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenIntermediateNulled_ThenEntireSuffixBelowBreakDisposedAndOnlyPrefixKept()
    {
        // Arrange: a three-segment path root -> mid -> leaf -> Name.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var leaf = new Node { Name = "leaf" };
        var mid = new Node { Name = "mid", Child = leaf };
        var root = new Node(context) { Name = "root", Child = mid };

        using var subscription = root.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Act: break the chain by nulling the first intermediate.
        root.Child = null;

        // Assert: the entire suffix below the break is disposed; only the resolvable prefix (root."Child")
        // keeps its listener.
        Assert.Equal(1, ListenerCount(root, nameof(Node.Child)));
        Assert.Equal(0, ListenerCount(mid, nameof(Node.Child)));
        Assert.Equal(0, ListenerCount(leaf, nameof(Node.Name)));
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenIndexGoesOutOfRange_ThenEntireSuffixBelowBreakDisposedAndOnlyPrefixKept()
    {
        // Arrange: root -> Children[1] -> Name, with a two-element collection so index 1 initially resolves.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var a0 = new Node { Name = "a0" };
        var a1 = new Node { Name = "a1" };
        var root = new Node(context) { Name = "root", Children = [a0, a1] };

        using var subscription = root.SubscribeToPath(x => x.Children[1].Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);
        Assert.Equal(1, ListenerCount(a1, nameof(Node.Name)));
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Act: shrink the collection so index 1 is out of range.
        root.Children = [a0];

        // Assert: the suffix (leaf listener) is disposed; only the collection segment on root remains.
        Assert.Equal(0, ListenerCount(a1, nameof(Node.Name)));
        Assert.Equal(1, ListenerCount(root, nameof(Node.Children)));
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenDictionaryKeyRemoved_ThenEntireSuffixBelowBreakDisposedAndOnlyPrefixKept()
    {
        // Arrange: root -> ByName["key"] -> Name.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var a = new Node { Name = "a0" };
        var root = new Node(context) { Name = "root", ByName = new Dictionary<string, Node> { ["key"] = a } };

        using var subscription = root.SubscribeToPath(x => x.ByName["key"].Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);
        Assert.Equal(1, ListenerCount(a, nameof(Node.Name)));
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());

        // Act: remove the key.
        root.ByName = new Dictionary<string, Node>();

        // Assert: the suffix (leaf listener) is disposed; only the dictionary segment on root remains.
        Assert.Equal(0, ListenerCount(a, nameof(Node.Name)));
        Assert.Equal(1, ListenerCount(root, nameof(Node.ByName)));
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenStructuralChurnRepeated_ThenListenerCountNeverExceedsChainLength()
    {
        // Arrange: a three-segment path whose intermediate is churned through resolved and broken states.
        const int iterations = 50;
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Node(context) { Name = "root" };

        using var subscription = root.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        // Act & Assert: repeatedly heal to a full chain (length three) then break to the bare prefix
        // (length one). The count must equal the current chain length after each transition and NEVER grow
        // beyond three: a leak would accumulate stale listeners and push it past the chain length.
        for (var i = 0; i < iterations; i++)
        {
            var leaf = new Node { Name = "l" + i };
            var mid = new Node { Name = "m" + i, Child = leaf };

            root.Child = mid;
            Assert.True(PropertyChangeSubscriptions.ReadSubscriptionCount() <= 3, "listener count exceeded the chain length (heal)");
            Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());

            root.Child = null;
            Assert.True(PropertyChangeSubscriptions.ReadSubscriptionCount() <= 3, "listener count exceeded the chain length (break)");
            Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
        }

        // Assert: settle to a full chain; the count returns to exactly the chain length, no leak accumulated.
        root.Child = new Node { Name = "final", Child = new Node { Name = "finalLeaf" } };
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenCyclicPathSubscribed_ThenDoublyAppearingSubjectHoldsTwoListeners()
    {
        // Arrange: a self-referencing node (node.Child == node) watched through x => x.Child.Child.Name. The
        // decomposed chain reads Child on node at position 0 AND position 1, so the doubly-appearing subject's
        // Child property carries TWO install-order listeners (a multiset, not a set), plus one on Name.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var node = new Node(context) { Name = "N" };
        node.Child = node;

        // Act
        using var subscription = node.SubscribeToPath(x => x.Child!.Child!.Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        // Assert: the multiset invariant. The subject appearing at two positions holds two listeners on Child.
        Assert.Equal(2, ListenerCount(node, nameof(Node.Child)));
        Assert.Equal(1, ListenerCount(node, nameof(Node.Name)));
        Assert.Equal(3, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenRootAndHandleDropped_ThenBothAreGarbageCollected()
    {
        // Arrange & Act: create the root and its fire-and-forget (never disposed) subscription entirely inside
        // a non-inlined helper that returns only weak references, so no strong local survives here to pin them.
        var (weakRoot, weakHandle) = CreateFireAndForgetSubscription();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert: dropping the root AND the handle makes the whole island (root, child, subscription, its
        // installed listeners) collectable. The internal subject<->listener<->subscription cycle holds nothing
        // alive once every external reference is gone.
        Assert.False(weakRoot.IsAlive, "the dropped root must be garbage collected");
        Assert.False(weakHandle.IsAlive, "the dropped subscription handle must be garbage collected");
    }

    [Fact]
    public void WhenIntermediateReplaced_ThenCachedAccessorDoesNotRetainOldSubject()
    {
        // Arrange & Act: keep the root and subscription alive while replacing the tracked child. The
        // returned weak reference is the only remaining reference that may point at the old child.
        var (root, subscription, oldChild) = CreateSubscriptionAndReplaceChild();
        using (subscription)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Assert: suffix disposal clears both listener state and cached segment access state.
            Assert.False(oldChild.IsAlive, "the replaced child must not be retained by the segment cache");
            GC.KeepAlive(root);
            GC.KeepAlive(subscription);
        }
    }

    [Fact]
    public void WhenDisposedHandleRetained_ThenRootAndObserverAreCollectible()
    {
        // Arrange & Act: create a subscription, dispose it, then drop every external reference EXCEPT the
        // disposed handle itself. The helper returns weak references to the graph root and to an object that
        // only the callback closure captures.
        var (subscription, weakRoot, weakCallbackTarget) = CreateAndDisposeRetainedSubscription();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert: dispose nulled the root and observer references (Volatile.Write, matching the primitive), so
        // a still-referenced disposed handle pins neither the graph root nor the observer closure.
        Assert.False(weakRoot.IsAlive, "a disposed subscription must not pin the graph root even when its handle is retained");
        Assert.False(weakCallbackTarget.IsAlive, "a disposed subscription must not pin the observer closure even when its handle is retained");
        GC.KeepAlive(subscription); // the handle is deliberately retained across the assertions
    }

    [Fact]
    public void WhenDisposedHandleRetained_ThenEvaluatedDictionaryKeyIsCollectible()
    {
        // Arrange & Act: a dictionary-key path evaluates and stores the key object on the segment. Dispose,
        // then drop the graph while retaining the disposed handle; only the segment array could still pin the
        // key (keys may be arbitrary reference types, up to the root itself).
        var (subscription, weakKey) = CreateAndDisposeDictionaryKeySubscription();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert: dispose released the segment array (Volatile.Write null), so the retained handle no longer
        // pins the evaluated dictionary key.
        Assert.False(weakKey.IsAlive, "a disposed subscription must not pin an evaluated dictionary key even when its handle is retained");
        GC.KeepAlive(subscription); // the handle is deliberately retained across the assertion
    }

    // Builds a root and an undisposed path subscription in an isolated scope, using a bare notifications
    // context created here (not passed in) so the whole graph is unreachable once the returned weak
    // references are the only thing pointing at it. Bare subscriptions avoid the lifecycle interceptor's
    // attached-subject retention, which would pin any root while its context is alive regardless of the
    // subscription and is orthogonal to what this test pins.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Root, WeakReference Handle) CreateFireAndForgetSubscription()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var root = new Node(context) { Name = "root", Child = new Node { Name = "child" } };
        var handle = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        return (new WeakReference(root), new WeakReference(handle));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Node Root, SubjectPathSubscription<string> Subscription, WeakReference OldChild)
        CreateSubscriptionAndReplaceChild()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var oldChild = new Node { Name = "old" };
        var root = new Node(context) { Child = oldChild };
        var subscription = root.SubscribeToPath(
            x => x.Child!.Name,
            (in SubjectPathChange<string> _) => { }, SubjectPathValidation.Full);

        var weakOldChild = new WeakReference(oldChild);
        root.Child = new Node { Name = "new" };
        return (root, subscription, weakOldChild);
    }

    // Creates a subscription in an isolated scope, disposes it, and returns the retained handle plus weak
    // references to the graph root and to an object captured only by the callback closure. Bare context for
    // the same reason as CreateFireAndForgetSubscription (no lifecycle retention of the root).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubjectPathSubscription<string> Subscription, WeakReference Root, WeakReference CallbackTarget)
        CreateAndDisposeRetainedSubscription()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var root = new Node(context) { Name = "root", Child = new Node { Name = "child" } };
        var callbackTarget = new object();
        var subscription = root.SubscribeToPath(
            x => x.Child!.Name,
            (in SubjectPathChange<string> _) => GC.KeepAlive(callbackTarget),
            SubjectPathValidation.Full);
        subscription.Dispose();
        return (subscription, new WeakReference(root), new WeakReference(callbackTarget));
    }

    // Creates a dictionary-key path subscription whose key is a distinct (non-interned) reference-type
    // instance, disposes it, and returns the retained handle plus a weak reference to the key. The graph
    // (which also holds the key as a dictionary key) is dropped, so after dispose only the segment array
    // could keep the key alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SubjectPathSubscription<string> Subscription, WeakReference Key) CreateAndDisposeDictionaryKeySubscription()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var key = new string("k-e-y".ToCharArray()); // a distinct, non-interned key instance to weak-reference
        var root = new Node(context) { ByName = new Dictionary<string, Node> { [key] = new Node { Name = "value" } } };
        var subscription = root.SubscribeToPath(
            x => x.ByName[key].Name,
            (in SubjectPathChange<string> _) => { },
            SubjectPathValidation.Full);
        subscription.Dispose();
        return (subscription, new WeakReference(key));
    }

    private static int ListenerCount(IInterceptorSubject subject, string propertyName)
        => new PropertyReference(subject, propertyName).TryGetPropertyData(PropertyChangeSubscription.ListenersKey, out var value)
            && value is PropertyChangeSubscription[] listeners
            ? listeners.Length
            : 0;
}
