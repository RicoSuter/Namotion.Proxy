using System.Collections.Concurrent;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Adversarial review probe for the holder-watching deadlock detector: does it convict a holder that
/// is blocked but making guaranteed progress, i.e. a false positive that fails an innocent thread's
/// legitimate write?
/// </summary>
public class AdversarialGateFalsePositiveTests
{
    /// <summary>
    /// Three threads, no cycle, no dispatch-and-wait. The gate holder blocks on an ordinary lock
    /// held by a thread that never touches the lifecycle and always releases it. The system is
    /// guaranteed to make progress, so the contending write must eventually succeed.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTheGateHolderBlocksOnALockThatIsGuaranteedToBeReleased_ThenTheContendingWriteStillSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var contendedTarget = new Person { FirstName = "target" };
        ((IInterceptorSubject)contendedTarget).AttachToContext(context);

        var unrelatedLock = new object();
        var holderIsInsideCallback = new ManualResetEventSlim();
        var lockTaken = new ManualResetEventSlim();

        // A third thread that has nothing to do with the lifecycle holds the lock for 600ms.
        var lockHolder = new Thread(() =>
        {
            lock (unrelatedLock)
            {
                lockTaken.Set();
                Thread.Sleep(600);
            }
        }) { IsBackground = true };
        lockHolder.Start();
        Assert.True(lockTaken.Wait(TimeSpan.FromSeconds(10)));

        var trigger = new Person { FirstName = "trigger" };
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, trigger))
            {
                holderIsInsideCallback.Set();
                lock (unrelatedLock)
                {
                }
            }
        };

        Exception? attachException = null;
        var attacher = new Thread(() =>
            attachException = Record.Exception(() => ((IInterceptorSubject)trigger).AttachToContext(context)))
        { IsBackground = true };

        // Act
        attacher.Start();
        Assert.True(holderIsInsideCallback.Wait(TimeSpan.FromSeconds(10)), "the holder never entered the callback");

        var stopwatch = Stopwatch.StartNew();
        var contendedException = Record.Exception(() => contendedTarget.Father = new Person { FirstName = "waited" });
        stopwatch.Stop();

        Assert.True(attacher.Join(TimeSpan.FromSeconds(30)), "the attach never finished");
        Assert.True(lockHolder.Join(TimeSpan.FromSeconds(30)));

        // Assert: nothing here is a deadlock. The lock holder always releases, the attach always
        // completes, so the contending write must go through rather than be convicted.
        Assert.Null(attachException);
        Assert.Null(contendedException);
        Assert.Equal("waited", ((Person)contendedTarget.Father!).FirstName);
    }

    /// <summary>
    /// The same shape but with no user callback at all: the holder blocks inside the framework's own
    /// claim step, on the attachment monitor of a subject a third thread is legitimately writing to
    /// while it is unattached (which holds that monitor across the whole write, see
    /// InterceptorExecutor.SetStructuralPropertyValue's lifecycle-free arm).
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTheGateHolderBlocksOnAnUnattachedSubjectsAttachmentMonitor_ThenTheContendingWriteStillSucceeds()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var contendedTarget = new Person { FirstName = "target" };
        ((IInterceptorSubject)contendedTarget).AttachToContext(context);

        var slowWriteStarted = new ManualResetEventSlim();
        var candidate = new SlowSetterSubject(() =>
        {
            slowWriteStarted.Set();
            Thread.Sleep(600);
        });

        // A third thread writes a structural property of an UNATTACHED subject. That runs under the
        // subject's attachment monitor for the whole (slow) write, violating no documented contract.
        var slowWriter = new Thread(() => candidate.Child = new Person()) { IsBackground = true };
        slowWriter.Start();
        Assert.True(slowWriteStarted.Wait(TimeSpan.FromSeconds(10)), "the slow write never started");

        // The gate holder attaches that same subject: it takes the gate, then blocks on the very
        // attachment monitor the slow write holds.
        var root = new Person { FirstName = "root" };
        ((IInterceptorSubject)root).AttachToContext(context);
        Exception? attachException = null;
        var attacher = new Thread(() =>
            attachException = Record.Exception(() => ((IInterceptorSubject)candidate).AttachToContext(context)))
        { IsBackground = true };

        // Act
        attacher.Start();

        // Wait for the state the scenario needs rather than for a duration: the attacher has to be
        // parked on the attachment monitor before the contending write starts, or the write races
        // ahead of the hold and proves nothing.
        Assert.True(
            SpinWait.SpinUntil(
                () => (attacher.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)),
            "the attacher never blocked, so it never held the gate against the monitor");

        var contendedException = Record.Exception(() => contendedTarget.Father = new Person { FirstName = "waited" });

        Assert.True(attacher.Join(TimeSpan.FromSeconds(30)), "the attach never finished");
        Assert.True(slowWriter.Join(TimeSpan.FromSeconds(30)));

        // Assert
        Assert.Null(attachException);
        Assert.Null(contendedException);
    }

    private sealed class SlowSetterSubject : IInterceptorSubject
    {
        private static readonly Action<SlowSetterSubject> WriteHook =
            subject => subject.OnWrite();

        private readonly Dictionary<string, SubjectPropertyMetadata> _metadata;
        private readonly Action _onWrite;
        private IInterceptorExecutor? _executor;
        private Person? _child;

        public SlowSetterSubject(Action onWrite)
        {
            _onWrite = onWrite;
            _metadata = new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(Person),
                    [],
                    static subject => ((SlowSetterSubject)subject)._child,
                    static (subject, value) => ((SlowSetterSubject)subject).Child = (Person?)value,
                    isIntercepted: true,
                    isDynamic: false)
            };
        }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => _metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();

        private void OnWrite() => _onWrite();

        public Person? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((SlowSetterSubject)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) =>
                {
                    var typed = (SlowSetterSubject)subject;
                    WriteHook(typed);
                    typed._child = newValue;
                });
        }
    }
}
