using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class StructuralWriteTests
{
    // The one SetPropertyValue entry routes on TProperty, so every structural scenario here
    // writes a subject-typed property; the scalar comparisons write an int one.

    /// <summary>
    /// Performs an attachment transition through the raw seam from inside the write chain, while
    /// the executor holds the attachment monitor. The monitor is reentrant on the writing thread,
    /// so this is the one shape that can transition the attachment mid-write.
    /// </summary>
    private sealed class AttachmentTransitionInterceptor : IWriteInterceptor
    {
        public IInterceptorExecutor? Executor;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            // Detach, the one transition legal from an attached state: the subject was attached to
            // resolve this very chain, and a direct swap to another context is rejected by design.
            Assert.True(Executor!.TryUpdateAttachment(Executor.AttachmentRevision, null, SubjectAttachmentAnchorKind.None, out _));
            next(ref context);
        }
    }

    /// <summary>
    /// Pauses the writing thread inside the chain so the test thread can transition the attachment
    /// while the write is provably between entry and terminal.
    /// </summary>
    private sealed class GatingWriteInterceptor : IWriteInterceptor
    {
        public readonly ManualResetEventSlim ReachedChain = new(false);
        public readonly ManualResetEventSlim ResumeWrite = new(false);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            ReachedChain.Set();
            Assert.True(ResumeWrite.Wait(TimeSpan.FromSeconds(30)));
            next(ref context);
        }
    }

    private sealed class RevisionCapturingInterceptor : IWriteInterceptor
    {
        public readonly List<long> Revisions = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            Revisions.Add(context.Revision);
        }
    }

    /// <summary>
    /// Counts on which side of the structural routing decision this lifecycle ran: a gate entry
    /// means the routing saw it, a chain execution means the compiled write chain contained it.
    /// The graph entry points throw because the registering test never attaches or admits anything
    /// through the probe, and a silent no-op there would hide a scenario defect.
    /// </summary>
    private sealed class CountingLifecycleInterceptor : ILifecycleInterceptor
    {
        private readonly object _structuralWriteGate = new();

        public int GateEnterCount;
        public int WritePropertyCount;

        public void EnterStructuralWriteGate()
        {
            Monitor.Enter(_structuralWriteGate);
            Interlocked.Increment(ref GateEnterCount);
        }

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        public bool TryAddProperties(SubjectPropertyRegistration registration) =>
            throw new NotSupportedException("The probe admits no properties.");

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor) =>
            throw new NotSupportedException("The probe attaches no subjects.");

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context) =>
            throw new NotSupportedException("The probe detaches no subjects.");

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref WritePropertyCount);
            next(ref context);
        }
    }

    private static IInterceptorExecutor GetExecutor(IInterceptorSubject subject)
    {
        return subject.Executor;
    }

    [Fact]
    public void WhenAttachmentIsUnchanged_ThenStructuralWriteCommitsLikeAScalarWrite()
    {
        // Arrange: no write interceptor, so the zero-interceptor structural terminal runs. The
        // one SetPropertyValue entry routes on TProperty: the subject-typed write takes the
        // structural protocol, the int one the scalar route.
        var context = InterceptorSubjectContext.Create();
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        StructuralHolder? structuralValue = null;
        int? scalarValue = null;
        var child = new StructuralHolder();

        // Act
        var structuralWritten = executor.SetPropertyValue("Child", child, null, (_, value) => structuralValue = value);
        var scalarWritten = executor.SetPropertyValue("Count", 43, 0, (_, value) => scalarValue = value);

        // Assert: both routes committed, stamped write state, and consumed one commit revision each
        // from the same per-subject counter.
        Assert.True(structuralWritten);
        Assert.True(scalarWritten);
        Assert.Same(child, structuralValue);
        Assert.Equal(43, scalarValue);

        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
        Assert.Equal(2, ((InterceptorExecutor)executor).Revision);
    }

    [Fact]
    public void WhenChainHasInterceptors_ThenStructuralWriteRunsThemAndStampsTheRevision()
    {
        // Arrange
        var capturing = new RevisionCapturingInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(capturing);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);

        // Act
        var written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => { });

        // Assert
        Assert.True(written);
        Assert.Equal([1L], capturing.Revisions);
    }

    [Fact]
    public void WhenAttachmentChangesBetweenEntryAndTerminal_ThenStructuralWriteStillCommits()
    {
        // Arrange: the interceptor transitions the attachment mid-chain, deterministically on the
        // writing thread itself. The attachment monitor is reentrant on that thread, so this is
        // the one shape that can move the attachment inside the protocol; it must order rather
        // than fail, because the write was validated for the attachment it entered with.
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;
        var backingWritten = false;

        // Act
        var written = executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => backingWritten = true);

        // Assert
        Assert.True(written);
        Assert.True(backingWritten);
        var property = new PropertyReference((IInterceptorSubject)subject, "Child");
        Assert.True(property.TryGetWriteState(true, out var commitRevision, out _));
        Assert.Equal(1, commitRevision);
    }

    [Fact]
    public void WhenConcurrentDetachRacesAStructuralWrite_ThenTheDetachWaitsAndBothComplete()
    {
        // Arrange: the writer pauses inside the chain while it holds the attachment monitor, so a
        // concurrent attachment transition must queue on that monitor instead of invalidating the
        // in-flight write. This is the ordering the deleted attachment-revision guard used to turn
        // into an exception.
        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        var backingWritten = false;
        Exception? writerException = null;

        var writer = new Thread(() =>
        {
            try
            {
                executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => backingWritten = true);
            }
            catch (Exception exception)
            {
                writerException = exception;
            }
        });

        var detachStarting = new ManualResetEventSlim(false);
        var detachCompleted = new ManualResetEventSlim(false);
        var detacher = new Thread(() =>
        {
            var revision = executor.AttachmentRevision;
            detachStarting.Set();
            executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _);
            detachCompleted.Set();
        });

        // Act: start the detach while the writer is provably between entry and terminal.
        writer.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));
        detacher.Start();

        // The detach must not complete while the write holds the attachment monitor. Waiting for
        // the detacher to reach the transition first keeps the negative wait from passing
        // vacuously on a thread that never got scheduled.
        Assert.True(detachStarting.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(detachCompleted.Wait(TimeSpan.FromMilliseconds(100)));

        gate.ResumeWrite.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the structural write did not complete");
        Assert.True(detacher.Join(TimeSpan.FromSeconds(30)), "the attachment transition did not complete");

        // Assert: the write committed and the detach landed after it.
        Assert.Null(writerException);
        Assert.True(backingWritten);
        Assert.True(detachCompleted.IsSet);
        Assert.Null(executor.AttachedContext);
    }

    [Fact]
    public void WhenLifecycleIsRegisteredWhileAStructuralWriteWaitsForTheMonitor_ThenThatWriteSeesItNowhere()
    {
        // Arrange: the routing decision and the write chain must derive from one context state.
        // A lifecycle registered between the routing resolution and the chain resolution would
        // otherwise sit in the chain but not in the routing, and its WriteProperty would take the
        // structural gate inside the attachment monitor, inverting the documented lock order.
        //
        // Warm up the whole no-lifecycle structural path first, so the raced writer below executes
        // only jitted code between its start and the attachment monitor.
        var warmupGate = new GatingWriteInterceptor();
        warmupGate.ResumeWrite.Set();
        var warmupContext = InterceptorSubjectContext.Create();
        warmupContext.AddService(warmupGate);
        GetExecutor(new StructuralHolder(warmupContext)).SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => { });

        var gate = new GatingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(gate);
        var subject = new StructuralHolder(context);
        var executor = GetExecutor(subject);
        var probe = new CountingLifecycleInterceptor();

        var monitorHolderCommitted = false;
        var monitorHolder = new Thread(() =>
            executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => monitorHolderCommitted = true));
        monitorHolder.IsBackground = true;

        var racedWriteCommitted = false;
        var racedWriter = new Thread(() =>
            executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => racedWriteCommitted = true));
        racedWriter.IsBackground = true;

        // Act: the first write pauses inside its chain, holding the attachment monitor. The raced
        // write then resolves its routing (no lifecycle yet) and parks on that monitor.
        monitorHolder.Start();
        Assert.True(gate.ReachedChain.Wait(TimeSpan.FromSeconds(30)));

        racedWriter.Start();

        // WaitSleepJoin is a positive observation of the park: the warmed-up path between Start
        // and the monitor contains no other managed blocking point, and the gate-entry assert
        // below catches the residual misordering where the park happened before the routing read.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((racedWriter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the raced writer never parked on the attachment monitor");
            Thread.Yield();
        }

        // The registration lands after the raced write resolved its routing and before it resolves
        // its chain, which is exactly the window the pinned state has to close.
        context.AddService(probe);
        gate.ResumeWrite.Set();

        Assert.True(monitorHolder.Join(TimeSpan.FromSeconds(30)), "the monitor-holding write did not complete");
        Assert.True(racedWriter.Join(TimeSpan.FromSeconds(30)), "the raced write did not complete");

        // Assert: scenario validity first. Had the raced routing seen the probe, it would have
        // entered the gate, and the run would prove nothing about the window.
        Assert.True(probe.GateEnterCount == 0,
            "the probe was registered before the raced write resolved its routing, so the race was not established");

        // The raced write sees the late lifecycle nowhere: not in the routing (above) and not in
        // the chain. A chain resolved from a fresh state instead of the routing's pinned state
        // would have run the probe here, without its gate.
        Assert.Equal(0, probe.WritePropertyCount);
        Assert.True(monitorHolderCommitted);
        Assert.True(racedWriteCommitted);

        // The next write pins a fresh state and sees the probe on both sides: the routing enters
        // its gate and the chain runs it.
        executor.SetPropertyValue("Child", new StructuralHolder(), null, (_, _) => { });
        Assert.Equal(1, probe.GateEnterCount);
        Assert.Equal(1, probe.WritePropertyCount);
    }

    [Fact]
    public void WhenAttachmentChangesDuringScalarWrite_ThenTheWriteDoesNotThrow()
    {
        // Arrange: same mid-chain transition as the structural test, but through the scalar route,
        // which must not pay for or react to attachment changes.
        var transitioning = new AttachmentTransitionInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(transitioning);
        var subject = new Car(context);
        var executor = GetExecutor(subject);
        transitioning.Executor = executor;

        // Act
        subject.Speed = 42;

        // Assert
        Assert.Equal(42, subject.Speed);
    }

    [Fact]
    public void WhenUnattachedSubjectTakesAStructuralWrite_ThenItShortCircuitsLikeAScalarWrite()
    {
        // Arrange: a subject that was never attached to any context. Its structural setter takes
        // the same short circuit the scalar one does, so construction costs exactly what it costs
        // on an unintercepted object: no executor, no lock, no commit revision, no write state.
        var holder = new StructuralHolder();

        // Act
        holder.Child = new StructuralHolder();
        holder.Count = 7;

        // Assert
        Assert.NotNull(holder.Child);
        Assert.Equal(7, holder.Count);
        Assert.Null(holder.TryGetContext());
        Assert.Equal(0, ((InterceptorExecutor)((IInterceptorSubject)holder).Executor).Revision);
        Assert.False(new PropertyReference(holder, nameof(StructuralHolder.Child)).TryGetWriteState(true, out _, out _));
        Assert.False(new PropertyReference(holder, nameof(StructuralHolder.Count)).TryGetWriteState(true, out _, out _));
    }

    [Fact]
    public void WhenAttachedSubjectTakesAStructuralWrite_ThenItRunsTheChainUnderTheAttachmentMonitor()
    {
        // Arrange: once a context is attached the write runs the ordinary chain inside the
        // attachment monitor, so it consumes a commit revision and stamps write state.
        var holder = new StructuralHolder(InterceptorSubjectContext.Create());

        // Act
        holder.Child = new StructuralHolder();

        // Assert
        Assert.Equal(1, ((InterceptorExecutor)((IInterceptorSubject)holder).Executor).Revision);
        Assert.True(new PropertyReference(holder, nameof(StructuralHolder.Child)).TryGetWriteState(true, out var revision, out _));
        Assert.Equal(1, revision);
    }
}

[Attributes.InterceptorSubject]
public partial class StructuralHolder
{
    public partial int Count { get; set; }

    public partial StructuralHolder? Child { get; set; }
}
