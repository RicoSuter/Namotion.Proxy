using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A structural write enters the target context's topology gate before the write chain is resolved,
/// so the callback guard that would reject a cross-context write from inside a lifecycle callback
/// only runs once that gate is already held. Two symmetric callbacks therefore acquire two gates in
/// opposite order. The explicit attach, detach and property admission entry points all check before
/// their gate; the structural write is the one topology mutation that does not.
/// </summary>
public class CrossContextGateDeadlockTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>
    /// Reproduces the finding that a cross-context structural write from a lifecycle callback takes
    /// the foreign gate before any guard runs. The rendezvous is artificial, because it lines both
    /// callbacks up inside their own gates at the same time; the acquisition order they then take
    /// is the production one. A deadlock shows up as a bounded join failing rather than as a hung
    /// suite, and the worker threads are background threads so a deadlocked pair cannot keep the
    /// run alive.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoAttachCallbacksCrossWriteIntoEachOthersContexts_ThenBothOperationsComplete()
    {
        // Arrange: each context holds a subject the other context's attach callback writes to.
        var rendezvous = new Barrier(2);
        var rendezvousReached = 0;
        Person? pinnedInFirst = null;
        Person? pinnedInSecond = null;
        Exception? firstCrossWriteException = null;
        Exception? secondCrossWriteException = null;

        var firstHandler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextAttach || change.Subject is not Person { FirstName: "trigger" } || pinnedInSecond is null)
            {
                return;
            }

            if (!rendezvous.SignalAndWait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref rendezvousReached);
            firstCrossWriteException = Record.Exception(
                () => pinnedInSecond.Father = new Person { FirstName = "fromFirst" });
        });

        var secondHandler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextAttach || change.Subject is not Person { FirstName: "trigger" } || pinnedInFirst is null)
            {
                return;
            }

            if (!rendezvous.SignalAndWait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref rendezvousReached);
            secondCrossWriteException = Record.Exception(
                () => pinnedInFirst.Father = new Person { FirstName = "fromSecond" });
        });

        var firstContext = CreateContext().WithService(() => firstHandler, _ => false);
        var secondContext = CreateContext().WithService(() => secondHandler, _ => false);

        pinnedInFirst = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInFirst).AttachToContext(firstContext);
        pinnedInSecond = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInSecond).AttachToContext(secondContext);

        var firstTrigger = new Person { FirstName = "trigger" };
        var secondTrigger = new Person { FirstName = "trigger" };

        var firstAttach = new Thread(
            () => ((IInterceptorSubject)firstTrigger).AttachToContext(firstContext)) { IsBackground = true };
        var secondAttach = new Thread(
            () => ((IInterceptorSubject)secondTrigger).AttachToContext(secondContext)) { IsBackground = true };

        // Act
        firstAttach.Start();
        secondAttach.Start();
        var firstCompleted = firstAttach.Join(WriteProtocolAcceptance.JoinTimeout);
        var secondCompleted = firstCompleted && secondAttach.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert: both callbacks reached the cross write, so a timeout cannot pass by serializing.
        Assert.Equal(2, Volatile.Read(ref rendezvousReached));

        // Termination is necessary but not sufficient. The remedy is that the cross-context write is
        // rejected, so both assertions are made: completion first, because a deadlock reports better
        // as a timeout than as a null exception, and then the rejection itself. Asserting the
        // rejection is what stops this test passing if the deadlock disappears because the gate
        // stopped being held here rather than because the write became guarded.
        Assert.True(firstCompleted && secondCompleted,
            "probable ABBA deadlock on two lifecycle gates: the first attach " +
            $"{(firstCompleted ? "completed" : "did not complete")} and the second attach " +
            $"{(secondCompleted ? "completed" : "did not complete")} within {WriteProtocolAcceptance.JoinTimeout.TotalSeconds:F0} seconds");
        Assert.IsType<LifecycleContractViolationException>(firstCrossWriteException);
        Assert.IsType<LifecycleContractViolationException>(secondCrossWriteException);
    }

    /// <summary>
    /// A third-party write interceptor with no ordering attributes registered after the lifecycle,
    /// which places it downstream of the lifecycle in the resolved write chain. It therefore runs
    /// at callback depth zero while holding the writing context's topology gate, which the callback
    /// reentrancy guard explicitly does not bind.
    /// </summary>
    private sealed class CrossContextWriteInterceptor(Barrier rendezvous, TimeSpan rendezvousTimeout) : IWriteInterceptor
    {
        private volatile IInterceptorSubject? _armedSubject;
        private volatile string? _armedPropertyName;
        private Action? _crossContextWrite;

        public int RendezvousReached;

        public Exception? CrossContextWriteException;

        public void Arm(IInterceptorSubject subject, string propertyName, Action crossContextWrite)
        {
            _crossContextWrite = crossContextWrite;
            _armedPropertyName = propertyName;
            _armedSubject = subject;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var isArmed = ReferenceEquals(context.Property.Subject, _armedSubject) &&
                          context.Property.Name == _armedPropertyName;

            next(ref context);

            if (!isArmed)
            {
                return;
            }

            _armedSubject = null;

            if (!rendezvous.SignalAndWait(rendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref RendezvousReached);
            CrossContextWriteException = Record.Exception(_crossContextWrite!);
        }
    }

    /// <summary>
    /// The second half of the same finding: no lifecycle callback is involved, so no guard is
    /// consulted at all. Two ordinary downstream write interceptors cross-writing into each other's
    /// contexts acquire two topology gates in opposite order, because the gate is entered before the
    /// write chain is resolved and the interceptor runs inside it.
    ///
    /// The rendezvous is artificial, because it lines both interceptors up inside their own gates at
    /// the same time; the acquisition order they then take is the production one.
    ///
    /// Like the callback half above, this asserts the rejection and not merely that both operations
    /// terminate. The distinction matters here: the gate is currently entered around the whole write
    /// chain, so an interceptor moved to the other side of the lifecycle still holds it, but if
    /// entering the gate ever moves inside the lifecycle then an interceptor outside it would hold
    /// nothing, both writes would simply complete, and a termination-only assertion would pass while
    /// proving nothing. The expected exception type follows the contract that a second transaction on
    /// one thread is rejected; a design that rejects with a different type makes this a one-line
    /// update rather than a finding.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoDownstreamWriteInterceptorsCrossWriteIntoEachOthersContexts_ThenBothOperationsComplete()
    {
        // Arrange: each context holds a subject the other context's downstream interceptor writes to.
        var rendezvous = new Barrier(2);
        var firstInterceptor = new CrossContextWriteInterceptor(rendezvous, WriteProtocolAcceptance.RendezvousTimeout);
        var secondInterceptor = new CrossContextWriteInterceptor(rendezvous, WriteProtocolAcceptance.RendezvousTimeout);

        var firstContext = CreateContext();
        firstContext.AddService<IWriteInterceptor>(firstInterceptor);
        var secondContext = CreateContext();
        secondContext.AddService<IWriteInterceptor>(secondInterceptor);

        var pinnedInFirst = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInFirst).AttachToContext(firstContext);
        var pinnedInSecond = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInSecond).AttachToContext(secondContext);

        var firstTrigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)firstTrigger).AttachToContext(firstContext);
        var secondTrigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)secondTrigger).AttachToContext(secondContext);

        firstInterceptor.Arm(firstTrigger, nameof(Person.Mother),
            () => pinnedInSecond.Father = new Person { FirstName = "fromFirst" });
        secondInterceptor.Arm(secondTrigger, nameof(Person.Mother),
            () => pinnedInFirst.Father = new Person { FirstName = "fromSecond" });

        var firstWrite = new Thread(
            () => firstTrigger.Mother = new Person { FirstName = "m" }) { IsBackground = true };
        var secondWrite = new Thread(
            () => secondTrigger.Mother = new Person { FirstName = "m" }) { IsBackground = true };

        // Act
        firstWrite.Start();
        secondWrite.Start();
        var firstCompleted = firstWrite.Join(WriteProtocolAcceptance.JoinTimeout);
        var secondCompleted = firstCompleted && secondWrite.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert: both interceptors reached the cross write, so a timeout cannot pass by serializing.
        Assert.Equal(2,
            Volatile.Read(ref firstInterceptor.RendezvousReached) +
            Volatile.Read(ref secondInterceptor.RendezvousReached));

        Assert.True(firstCompleted && secondCompleted,
            "probable ABBA deadlock on two lifecycle gates: the first structural write " +
            $"{(firstCompleted ? "completed" : "did not complete")} and the second structural write " +
            $"{(secondCompleted ? "completed" : "did not complete")} within {WriteProtocolAcceptance.JoinTimeout.TotalSeconds:F0} seconds");

        Assert.IsType<LifecycleContractViolationException>(firstInterceptor.CrossContextWriteException);
        Assert.IsType<LifecycleContractViolationException>(secondInterceptor.CrossContextWriteException);
    }
}
