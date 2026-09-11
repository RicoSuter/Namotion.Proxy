using Namotion.Interceptor.Hosting.Tests.Models;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// A production seam, armed to report when the code under test reaches it and to hold there until the
/// test lets it past.
/// </summary>
/// <remarks>
/// Disposal disarms the seam and releases whatever is parked on it, so an assertion that fails while a
/// transition is held leaves no chain wedged on a gate nothing is going to open. That is why the arming
/// helpers on <see cref="TestGateExtensions"/> are meant to be held in a <c>using</c>.
/// </remarks>
internal sealed class TestGate : IDisposable
{
    /// <summary>
    /// How long anything waits on this gate. Long enough that only a broken build reaches it, and
    /// bounded so a broken build fails its test rather than hanging the run.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private readonly Action _disarm;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TestGate(Action disarm) => _disarm = disarm;

    /// <summary>
    /// Runs inside the seam before it reports being reached, so whatever it does is in place by the time
    /// a test thread waiting for the seam returns.
    /// </summary>
    public Action? OnReached { get; set; }

    /// <summary>Completes once the code under test has reached the seam.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Whether the seam has been reached, read without waiting for it.</summary>
    public bool WasReached => _reached.Task.IsCompleted;

    /// <summary>Lets what is held at the seam past it, and everything that reaches it afterwards.</summary>
    public void Release() => _released.TrySetResult();

    public Task WaitUntilReachedAsync() => _reached.Task.WaitAsync(GateTimeout);

    /// <summary>Waits for the seam from a thread that cannot await, such as another seam's body.</summary>
    public void WaitUntilReached() => _reached.Task.Wait(GateTimeout);

    public void Dispose()
    {
        _disarm();
        Release();
    }

    /// <summary>Arms a seam whose body is awaited, so holding it parks rather than blocking a thread.</summary>
    public static TestGate Arm(Action<Func<Task>?> seam)
    {
        var gate = new TestGate(() => seam(null));
        seam(gate.EnterAsync);
        return gate;
    }

    /// <summary>Arms a seam whose body is synchronous, so holding it blocks the thread that reached it.</summary>
    public static TestGate ArmBlocking(Action<Action?> seam)
    {
        var gate = new TestGate(() => seam(null));
        seam(gate.EnterAndBlock);
        return gate;
    }

    private Task EnterAsync()
    {
        OnReached?.Invoke();
        _reached.TrySetResult();
        return _released.Task;
    }

    private void EnterAndBlock()
    {
        OnReached?.Invoke();
        _reached.TrySetResult();
        _released.Task.Wait(GateTimeout);
    }
}

/// <summary>
/// Arms one <see cref="TestGate"/> per seam the hosting code carries. Named per seam rather than taking
/// the property, so a call site says which interleaving it drives and cannot arm an awaitable seam with
/// a blocking body.
/// </summary>
internal static class TestGateExtensions
{
    /// <summary>Holds the drain after it began and before it clears liveness.</summary>
    public static TestGate HoldAtDrain(this HostedServiceHandler handler)
        => TestGate.Arm(gate => handler.DrainGate = gate);

    /// <summary>Holds the drain between its owned snapshot and the stops it appends.</summary>
    public static TestGate HoldAtDrainAppend(this HostedServiceHandler handler)
        => TestGate.Arm(gate => handler.DrainAppendGate = gate);

    /// <summary>Holds the drain between its first wait for in flight transitions and the release loop.</summary>
    public static TestGate HoldAtDrainRelease(this HostedServiceHandler handler)
        => TestGate.Arm(gate => handler.DrainReleaseGate = gate);

    /// <summary>Holds an attach between its ownership take and the gate re-read after it.</summary>
    public static TestGate HoldAtOwnershipTake(this HostedServiceHandler handler)
        => TestGate.ArmBlocking(gate => handler.OwnershipTakenGate = gate);

    /// <summary>Holds a take inside the chain lock, between its liveness read and its exchange.</summary>
    public static TestGate HoldAtLivenessRead(this HostedServiceHandler handler)
        => TestGate.ArmBlocking(gate => handler.LivenessReadGate = gate);

    /// <summary>Holds a liveness write inside the graph mutation lock it is taken under.</summary>
    public static TestGate HoldAtLivenessWrite(this HostedServiceHandler handler)
        => TestGate.ArmBlocking(gate => handler.LivenessWriteGate = gate);

    /// <summary>Holds every transition on the target's chain at the top of its body.</summary>
    public static TestGate HoldAtTransition(this HostedServiceTarget target)
        => TestGate.Arm(gate => target.TransitionGate = gate);

    /// <summary>Holds a take inside the chain lock, between the take and the append.</summary>
    public static TestGate HoldAtChainLock(this HostedServiceTarget target)
        => TestGate.ArmBlocking(gate => target.ChainLockGate = gate);

    /// <summary>Holds the subject inside its own <c>StopAsync</c>.</summary>
    public static TestGate HoldAtStop(this CountingHostedSubject subject)
        => TestGate.Arm(gate => subject.StopHold = gate);
}
