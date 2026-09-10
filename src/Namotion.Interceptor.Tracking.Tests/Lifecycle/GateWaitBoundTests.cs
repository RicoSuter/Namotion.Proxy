using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The holder check convicts a holder the runtime reports as blocked. A holder that spins, polls or
/// waits inside unmanaged code is never in that state, so nothing observes it and only the last
/// resort bound ends the wait. These pin that bound, which at its real size no test could reach.
/// </summary>
public class GateWaitBoundTests
{
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTheGateHolderSpinsForeverInsteadOfBlocking_ThenAContendingWriteIsStillBounded()
    {
        // Arrange: the holder never blocks, so the holder check can never see it and only the
        // bound can end the contending write's wait.
        var context = CreateContextBoundedQuickly();

        var target = new Person { FirstName = "target" };
        ((IInterceptorSubject)target).AttachToContext(context);

        var spinning = new ManualResetEventSlim();
        var releaseSpin = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            var subject = new Person { FirstName = "holder" };
            context.TryGetLifecycleInterceptor()!.SubjectAttached += _ =>
            {
                spinning.Set();
                while (!releaseSpin.IsSet)
                {
                    Thread.SpinWait(200);
                }
            };

            ((IInterceptorSubject)subject).AttachToContext(context);
        })
        { IsBackground = true };

        // Act
        holder.Start();
        Assert.True(spinning.Wait(TimeSpan.FromSeconds(10)), "the holder never took the gate");

        var contended = Record.Exception(() => target.Father = new Person { FirstName = "waited" });

        releaseSpin.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the holder never finished");

        // Assert: the wait ended in a named exception rather than hanging on a holder nothing can
        // observe, and it says it could not tell which cause it was.
        var violation = Assert.IsType<LifecycleContractViolationException>(contended);
        Assert.Contains("Timed out after", violation.Message);
        Assert.Contains("Nothing here can tell which it is", violation.Message);
        Assert.Contains("Nothing was read and nothing was changed", violation.Message);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTheGateHolderAlternatesBlockedAndRunning_ThenAContendingWriteIsStillBounded()
    {
        // Arrange: a holder that polls resets the blocked window on every sample that catches it
        // running, so it never accrues the threshold however long it is genuinely stuck.
        var context = CreateContextBoundedQuickly();

        var target = new Person { FirstName = "target" };
        ((IInterceptorSubject)target).AttachToContext(context);

        var polling = new ManualResetEventSlim();
        var releasePoll = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            var subject = new Person { FirstName = "holder" };
            context.TryGetLifecycleInterceptor()!.SubjectAttached += _ =>
            {
                polling.Set();
                while (!releasePoll.IsSet)
                {
                    releasePoll.Wait(TimeSpan.FromMilliseconds(15));
                    Thread.SpinWait(2_000);
                }
            };

            ((IInterceptorSubject)subject).AttachToContext(context);
        })
        { IsBackground = true };

        // Act
        holder.Start();
        Assert.True(polling.Wait(TimeSpan.FromSeconds(10)), "the holder never took the gate");

        var contended = Record.Exception(() => target.Father = new Person { FirstName = "waited" });

        releasePoll.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the holder never finished");

        // Assert
        var violation = Assert.IsType<LifecycleContractViolationException>(contended);
        Assert.Contains("Timed out after", violation.Message);
    }

    /// <summary>
    /// A context whose last-resort bound is milliseconds rather than minutes, and whose blocked
    /// threshold stays far above it so only the bound can decide these.
    /// </summary>
    private static IInterceptorSubjectContext CreateContextBoundedQuickly()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        lifecycle.GateWaitTimeoutMilliseconds = 1_000;
        lifecycle.BlockedHolderThresholdMilliseconds = 60_000;

        return context;
    }
}
