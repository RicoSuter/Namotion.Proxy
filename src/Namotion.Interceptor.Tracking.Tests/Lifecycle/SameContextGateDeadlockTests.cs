using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The topology gate is held across the whole write chain, so code running inside it that dispatches
/// structural work to another thread and waits for that thread deadlocks: the holder cannot release
/// the gate until the work finishes and the work cannot start until the gate is released. The
/// dispatch goes through a thread, a pool or a queue and is invisible from here, so this cannot be
/// rejected up front the way a second gate can. A waiter watches the holder instead, which turns the
/// hang into a named exception on the dispatched thread and leaves the holder free to finish.
/// </summary>
/// <remarks>
/// One test per way into the gate, because each reaches it through different code: a downstream
/// interceptor, the two handler kinds, the subject event, and a structural getter the lifecycle
/// itself invokes. They pin the report rather than the hang, so a return to waiting forever fails on
/// the bounded join instead of taking the suite with it.
///
/// The last test is the one that decides whether watching the holder beats waiting on a clock: a
/// whole-graph attach holds the gate for far longer than the deadlock window while doing nothing but
/// work, and a contending write has to wait it out rather than convict it.
/// </remarks>
public class SameContextGateDeadlockTests
{
    /// <summary>Comfortably longer than the conviction threshold, so a hang is still reported as one.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Well under the last-resort bound, so a case decided by that bound instead of by
    /// watching the holder fails these.</summary>
    private static readonly TimeSpan FastFailureBudget = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAGateHolderWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFailsInsteadOfHanging()
    {
        // Arrange: a write interceptor downstream of the lifecycle runs while the gate is held, and
        // from there dispatches a structural write on the same context to a worker it then joins.
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person { FirstName = "worker target" };
        ((IInterceptorSubject)workerTarget).AttachToContext(context);

        var interceptor = new DispatchAndJoinOnceInterceptor(
            () => workerTarget.Father = new Person { FirstName = "fromWorker" },
            JoinTimeout);
        context.AddService<IWriteInterceptor>(interceptor);

        var trigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Act
        var stopwatch = Stopwatch.StartNew();
        trigger.Mother = new Person { FirstName = "fromHolder" };
        stopwatch.Stop();

        // Assert: the worker gave up rather than waiting forever, and said what the caller did.
        Assert.True(interceptor.WorkerJoined, "the dispatched worker never returned, so the gate wait is unbounded");
        AssertReportedAsDeadlock(interceptor.WorkerException, stopwatch.Elapsed);

        // Assert: only the dispatched write failed. The holder's own write went through, and the
        // subject the worker could not attach is untouched.
        Assert.Equal("fromHolder", ((Person)trigger.Mother!).FirstName);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenALifecycleHandlerWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context);
        var trigger = new Person { FirstName = "trigger" };
        Exception? workerException = null;
        var stopwatch = new Stopwatch();
        context.WithService(() => new DelegateLifecycleHandler(change =>
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, trigger))
            {
                workerException = DispatchAndJoin(() => workerTarget.Father = new Person(), stopwatch);
            }
        }), _ => false);

        // Act
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAPropertyHandlerWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context);
        var trigger = new Person { FirstName = "trigger" };
        Exception? workerException = null;
        var stopwatch = new Stopwatch();
        context.WithService(() => new DelegatePropertyAttachHandler(change =>
        {
            if (change.Property.Name == nameof(Person.FirstName) && ReferenceEquals(change.Subject, trigger))
            {
                workerException = DispatchAndJoin(() => workerTarget.Mother = new Person(), stopwatch);
            }
        }), _ => false);

        // Act
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Mother);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenASubjectAttachedHandlerWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context);
        var trigger = new Person { FirstName = "trigger" };
        Exception? workerException = null;
        var stopwatch = new Stopwatch();
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, trigger))
            {
                workerException = DispatchAndJoin(() => workerTarget.Father = new Person(), stopwatch);
            }
        };

        // Act
        ((IInterceptorSubject)trigger).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAStructuralGetterWaitsForStructuralWorkItDispatched_ThenTheDispatchedWriteFails()
    {
        // Arrange: the explicit attach reads the candidate's structural properties under the gate,
        // so the getter is the one entry point no interceptor ordering can move.
        var context = CreateContextConvictingQuickly();

        var workerTarget = new Person(context) { FirstName = "worker target" };
        var candidate = new Person { FirstName = "candidate" };
        Exception? workerException = null;
        var getterCalls = 0;
        var stopwatch = new Stopwatch();
        ((IInterceptorSubject)candidate).AddProperties(new SubjectPropertyMetadata(
            "Captured", typeof(Person), [], _ =>
            {
                if (++getterCalls == 1)
                {
                    workerException = DispatchAndJoin(() => workerTarget.Father = new Person(), stopwatch);
                }

                return null;
            }, null, isIntercepted: true, isDynamic: true));

        // Act
        ((IInterceptorSubject)candidate).AttachToContext(context);

        // Assert
        AssertReportedAsDeadlock(workerException, stopwatch.Elapsed);
        Assert.Null(workerTarget.Father);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTheGateHolderIsRunningALargeAttach_ThenAContendingWriteWaitsForItInsteadOfFailing()
    {
        // Arrange: a whole-graph attach runs under the gate and holds it for seconds while doing
        // nothing but work, which is the one legitimate hold long enough to be mistaken for a
        // deadlock. A second thread writes into the same context while that attach is in flight.
        var context = CreateContextConvictingQuickly();

        var contendedTarget = new Person { FirstName = "contended" };
        ((IInterceptorSubject)contendedTarget).AttachToContext(context);

        var attachStarted = new ManualResetEventSlim();
        var attachedCount = 0;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += _ =>
        {
            if (Interlocked.Increment(ref attachedCount) == AttachedSubjectsBeforeContending)
            {
                attachStarted.Set();
            }
        };

        var root = BuildBinaryTree(SlowAttachTreeDepth);
        Exception? attachException = null;
        var attacher = new Thread(() => attachException = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context)))
        {
            IsBackground = true
        };

        // Act
        attacher.Start();
        Assert.True(attachStarted.Wait(JoinTimeout), "the attach never reached the graph, so nothing was contended");

        var stopwatch = Stopwatch.StartNew();
        var contendedException = Record.Exception(() => contendedTarget.Father = new Person { FirstName = "waited" });
        stopwatch.Stop();

        // Assert: the contending write waited out a hold many times the deadlock window and then
        // went through, and the attach it waited for completed.
        Assert.Null(contendedException);
        Assert.Equal("waited", ((Person)contendedTarget.Father!).FirstName);
        Assert.True(attacher.Join(JoinTimeout), "the attach never finished");
        Assert.Null(attachException);
        Assert.True(stopwatch.Elapsed > MinimumContendedWait,
            $"the write only waited {stopwatch.Elapsed}, so it never overlapped the attach and proves nothing");
    }

    /// <summary>Large enough that the attach holds the gate for far longer than the deadlock window.</summary>
    private const int SlowAttachTreeDepth = 17;

    /// <summary>How many subjects the attach publishes before the second thread starts contending.</summary>
    private const int AttachedSubjectsBeforeContending = 128;

    /// <summary>Longer than the deadlock window, so a write that never overlapped fails the test.</summary>
    private static readonly TimeSpan MinimumContendedWait = TimeSpan.FromMilliseconds(300);

    private static Person BuildBinaryTree(int depth)
    {
        var person = new Person();
        if (depth > 1)
        {
            person.Father = BuildBinaryTree(depth - 1);
            person.Mother = BuildBinaryTree(depth - 1);
        }

        return person;
    }

    /// <summary>Runs one structural write on a worker thread and returns what waiting for the gate cost it.</summary>
    private static Exception? DispatchAndJoin(Action structuralWrite, Stopwatch stopwatch)
    {
        Exception? exception = null;
        var worker = new Thread(() => exception = Record.Exception(structuralWrite)) { IsBackground = true };
        stopwatch.Start();
        worker.Start();
        var joined = worker.Join(JoinTimeout);
        stopwatch.Stop();
        Assert.True(joined, "the dispatched worker never returned, so the gate wait is unbounded");
        return exception;
    }

    /// <summary>
    /// A context whose gate convicts a continuously blocked holder in milliseconds rather than in
    /// the production threshold, so these cases cost their dispatch rather than that whole bound.
    /// </summary>
    private static IInterceptorSubjectContext CreateContextConvictingQuickly()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!)
            .BlockedHolderThresholdMilliseconds = 200;

        return context;
    }

    private static void AssertReportedAsDeadlock(Exception? workerException, TimeSpan elapsed)
    {
        var violation = Assert.IsType<LifecycleContractViolationException>(workerException);
        Assert.Contains("topology gate", violation.Message);
        Assert.Contains("never once seen running", violation.Message);
        Assert.Contains("dispatched structural work to another thread", violation.Message);
        Assert.Contains("Nothing was read and nothing was changed", violation.Message);
        Assert.True(elapsed < FastFailureBudget,
            $"the deadlock took {elapsed} to report, so the holder check did not decide it");
    }

    /// <summary>Dispatches one structural write to a worker thread and waits for it, once.</summary>
    private sealed class DispatchAndJoinOnceInterceptor(Action dispatchedWrite, TimeSpan joinTimeout) : IWriteInterceptor
    {
        private bool _fired;

        public bool WorkerJoined { get; private set; }

        public Exception? WorkerException { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (!_fired)
            {
                _fired = true;
                var worker = new Thread(() => WorkerException = Record.Exception(dispatchedWrite)) { IsBackground = true };
                worker.Start();
                WorkerJoined = worker.Join(joinTimeout);
            }

            next(ref context);
        }
    }
}
