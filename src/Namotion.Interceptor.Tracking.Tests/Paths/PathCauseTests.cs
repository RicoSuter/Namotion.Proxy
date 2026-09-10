using System;
using System.Collections.Generic;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

// Cause/provenance of a path event. Cause is the real property write that triggered the event: its Kind, its
// triggering Property (name + subject) and its Origin, passed through VERBATIM. Crucially, Cause.Origin is the
// origin of the TRIGGER, not provenance for New: New always comes from a fresh walk that can supersede the
// triggering write's own payload. No production change is expected.
[Collection(PerPropertySubscriptionCollection.Name)]
public class PathCauseTests
{
    public PathCauseTests() => PropertyChangeSubscriptions.ResetForTests();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void WhenLeafWritten_ThenCauseIsThatLeafWriteWithLocalOrigin()
    {
        // Arrange: a two-segment path root -> child -> Name.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = new Node { Name = "c0" };
        var root = new Node(context) { Name = "root", Child = child };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: a plain leaf write.
        child.Name = "c1";

        // Assert: a ValueChange whose Cause is the leaf write itself, on the leaf subject, with Local origin.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal(nameof(Node.Name), change.Cause.Property.Name);
        Assert.Same(child, change.Cause.Property.Subject);
        Assert.Equal(ChangeOriginKind.Local, change.Cause.Origin.Kind);
        Assert.Null(change.Cause.Origin.Source);
        Assert.Equal("c1", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenIntermediateWritten_ThenCauseIsThatIntermediateWriteWithLocalOrigin()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = new Node { Name = "c0" };
        var root = new Node(context) { Name = "root", Child = child };

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: a structural write to the intermediate.
        var child2 = new Node { Name = "c2" };
        root.Child = child2;

        // Assert: a PathChange whose Cause is the intermediate write, on the root subject, with Local origin.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, change.Kind);
        Assert.Equal(nameof(Node.Child), change.Cause.Property.Name);
        Assert.Same(root, change.Cause.Property.Subject);
        Assert.Equal(ChangeOriginKind.Local, change.Cause.Origin.Kind);
        Assert.Equal("c2", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenLeafWrittenFromSource_ThenCauseOriginIsThatSourceVerbatim()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = new Node { Name = "c0" };
        var root = new Node(context) { Name = "root", Child = child };
        var source = new object();

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: an inbound source write to the leaf.
        new PropertyReference(child, nameof(Node.Name))
            .SetValueFromSource(source, DateTimeOffset.UtcNow, null, "fromSource");

        // Assert: the triggering write's origin passes through the Cause verbatim (kind and source instance).
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.ValueChange, change.Kind);
        Assert.Equal(ChangeOriginKind.FromSource, change.Cause.Origin.Kind);
        Assert.Same(source, change.Cause.Origin.Source);
        Assert.Equal("fromSource", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public void WhenIntermediateWrittenFromSource_ThenCauseOriginIsThatSourceVerbatim()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = new Node { Name = "c0" };
        var root = new Node(context) { Name = "root", Child = child };
        var source = new object();

        var events = new List<SubjectPathChange<string>>();
        using var subscription = root.SubscribeToPath(x => x.Child!.Name, (in SubjectPathChange<string> c) => events.Add(c), SubjectPathValidation.Full);

        // Act: an inbound source write to the intermediate.
        var child2 = new Node { Name = "c2" };
        new PropertyReference(root, nameof(Node.Child))
            .SetValueFromSource(source, DateTimeOffset.UtcNow, null, child2);

        // Assert: a structural PathChange whose Cause carries the source verbatim.
        var change = Assert.Single(events);
        Assert.Equal(SubjectPathChangeKind.PathChange, change.Kind);
        Assert.Equal(nameof(Node.Child), change.Cause.Property.Name);
        Assert.Equal(ChangeOriginKind.FromSource, change.Cause.Origin.Kind);
        Assert.Same(source, change.Cause.Origin.Source);
        Assert.Equal("c2", change.NewState.GetValueOrDefault());
    }

    [Fact]
    public async Task WhenFromSourceDispatchDelayedPastLocalCommit_ThenSingleEventCarriesLocalValueUnderFromSourceCauseAndLocalCallbackSuppressed()
    {
        // Origin-is-not-provenance, the documented contract: Cause.Origin is the origin of the TRIGGERING
        // write, never provenance for New. New is always a fresh walk, so it can carry a value written by a
        // DIFFERENT, later write than the one whose origin the Cause reports. Consumers needing per-write
        // origin fidelity must use the per-property primitive or the queue channel, not a path subscription.
        //
        // Orchestration (a read gate delays the FromSource DISPATCH, not its commit):
        //   T1 issues a FromSource write to the leaf. It commits "fromSource", then dispatches; the path
        //   callback takes the subscription lock and starts its validating walk. A read gate parks that walk
        //   just before it reads the leaf, so T1 is frozen mid-walk holding the subscription lock.
        //   T2 issues a Local write to the SAME leaf. It commits "local", then its dispatch blocks on the
        //   subscription lock (held by the parked T1).
        //   Releasing the gate lets T1's walk read the now-committed "local": the single delivered event
        //   carries New == "local" under Cause.Origin == FromSource. T2 then takes the lock, walks, finds its
        //   value equals the just-advanced baseline, and is SUPPRESSED (coalesced) with no callback of its own.

        // Arrange
        var gate = new WalkReadGate(nameof(Node.Name));
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeSubscriptions()
            .WithService(() => gate, _ => false);
        var node = new Node(context) { Name = "seed" };
        var source = new object();

        var deliveredLock = new object();
        var delivered = new List<SubjectPathChange<string>>();
        using var subscription = node.SubscribeToPath(x => x.Name, (in SubjectPathChange<string> c) =>
        {
            lock (deliveredLock)
            {
                delivered.Add(c);
            }
        }, SubjectPathValidation.Full);

        gate.Arm(); // the seed walk already ran during Subscribe, so only the dispatch walks are gated now

        // Act: T1 = the FromSource write. Its dispatch walk parks (holding the subscription lock) before the
        // leaf read.
        var fromSourceWrite = Task.Run(() =>
            new PropertyReference(node, nameof(Node.Name)).SetValueFromSource(source, DateTimeOffset.UtcNow, null, "fromSource"));
        Assert.True(gate.Parked.Wait(Timeout), "the FromSource dispatch walk never parked");

        // T2 = the Local write on its own thread; it commits "local", then its dispatch blocks on the lock.
        var localWrite = new Thread(() => node.Name = "local") { IsBackground = true };
        localWrite.Start();
        await AsyncTestHelpers.WaitUntilAsync(
            () => node.Name == "local" && localWrite.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
            Timeout, TimeSpan.FromMilliseconds(5),
            "the Local write never committed and blocked on the subscription lock");

        // Release T1: its walk now reads the committed "local", superseding the FromSource payload.
        gate.Release.Set();
        await fromSourceWrite.WaitAsync(Timeout);
        Assert.True(localWrite.Join(Timeout), "the Local write never completed");

        // Assert: exactly one delivered event. New is the fresh-walk (Local) value, NOT the FromSource
        // payload, while the Cause reports the FromSource trigger verbatim; the Local write's own callback was
        // coalesced away because, by the time it walked, the observed value already equalled the baseline.
        SubjectPathChange<string>[] observed;
        lock (deliveredLock)
        {
            observed = delivered.ToArray();
        }

        var change = Assert.Single(observed);
        Assert.Equal("local", change.NewState.GetValueOrDefault());
        Assert.NotEqual("fromSource", change.NewState.GetValueOrDefault());
        Assert.Equal(ChangeOriginKind.FromSource, change.Cause.Origin.Kind);
        Assert.Same(source, change.Cause.Origin.Source);

        // Quiescent consistency: the settled graph and Current agree with the delivered New.
        Assert.Equal("local", node.Name);
        Assert.Equal("local", subscription.Current.GetValueOrDefault());
    }

    // A read interceptor that, once armed, parks the FIRST read of one named property (the FromSource dispatch
    // walk) holding whatever lock its caller holds, until released. Parking exactly once lets every later read
    // (the Local write's own dispatch walk) pass straight through.
    private sealed class WalkReadGate : IReadInterceptor
    {
        private readonly string _propertyName;
        private int _armed;
        private int _parkedOnce;

        public WalkReadGate(string propertyName) => _propertyName = propertyName;

        public ManualResetEventSlim Parked { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            if (Volatile.Read(ref _armed) == 1
                && context.Property.Name == _propertyName
                && Interlocked.Exchange(ref _parkedOnce, 1) == 0)
            {
                Parked.Set();
                if (!Release.Wait(Timeout))
                {
                    throw new TimeoutException("the walk read gate was never released");
                }
            }

            return next(ref context);
        }
    }
}
