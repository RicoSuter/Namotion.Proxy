using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A thread runs at most one lifecycle topology transaction, so a second one on a different
/// context is rejected rather than waiting for a gate no order covers. The rejection bookkeeping
/// is a thread-local counter, which makes unwinding it on every exit path part of the contract:
/// a leaked count on a pooled thread would reject the next unrelated transaction that thread runs.
/// </summary>
public class TopologyTransactionTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    /// <summary>Writes into another context once, from downstream of the lifecycle in the chain.</summary>
    private sealed class CrossContextWriteOnceInterceptor(Action crossContextWrite) : IWriteInterceptor
    {
        private bool _fired;

        public Exception? CrossContextWriteException;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);

            if (_fired)
            {
                return;
            }

            _fired = true;
            CrossContextWriteException = Record.Exception(crossContextWrite);
        }
    }

    [Fact]
    public void WhenASecondTransactionIsRejected_ThenTheThreadCanStillRunLaterTransactions()
    {
        // Arrange: a write interceptor downstream of the first context's lifecycle runs while that
        // lifecycle's gate is held, and from there writes a structural property of a subject the
        // second context owns. No lifecycle callback is involved, so nothing but the
        // one-transaction rule can reject it, and uncontended it would otherwise just succeed.
        var secondContext = CreateContext();
        var pinnedInSecond = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInSecond).AttachToContext(secondContext);

        var interceptor = new CrossContextWriteOnceInterceptor(
            () => pinnedInSecond.Father = new Person { FirstName = "rejected" });
        var firstContext = CreateContext();
        firstContext.AddService<IWriteInterceptor>(interceptor);

        var trigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)trigger).AttachToContext(firstContext);

        // Act: the enclosing write still completes, and the same thread then opens a fresh
        // transaction on the very context whose gate the rejected write reached for.
        trigger.Mother = new Person { FirstName = "m" };
        var laterException = Record.Exception(
            () => pinnedInSecond.Mother = new Person { FirstName = "later" });

        // Assert
        Assert.IsType<LifecycleContractViolationException>(interceptor.CrossContextWriteException);
        Assert.Null(pinnedInSecond.Father);
        Assert.Same(firstContext, ((IInterceptorSubject)trigger.Mother!).TryGetContext());
        Assert.Null(laterException);
        Assert.Same(secondContext, ((IInterceptorSubject)pinnedInSecond.Mother!).TryGetContext());
    }

    [Fact]
    public void WhenATransactionReentersItsOwnGate_ThenItIsNotRejected()
    {
        // Arrange: a same-context attach callback that adds properties re-enters the one gate the
        // thread already holds, which the one-transaction rule must keep legal.
        var addedFromCallback = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change is { IsContextAttach: true, Subject: Person { FirstName: "trigger" } } && !addedFromCallback)
                {
                    addedFromCallback = true;
                    change.Subject.AddProperties(new SubjectPropertyMetadata(
                        "Extra", typeof(string), [], _ => "value", null, isIntercepted: true, isDynamic: true));
                }
            }), _ => false);

        // Act
        var trigger = new Person { FirstName = "trigger" };
        var exception = Record.Exception(() => ((IInterceptorSubject)trigger).AttachToContext(context));

        // Assert
        Assert.Null(exception);
        Assert.True(addedFromCallback);
        Assert.True(((IInterceptorSubject)trigger).Properties.ContainsKey("Extra"));
    }
}
