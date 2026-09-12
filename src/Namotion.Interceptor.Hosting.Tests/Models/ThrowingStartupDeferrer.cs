using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A startup completion deferrer that throws from taking a hold, from releasing one, or both. Taking
/// runs inside a property write, so an escape there surfaces at an unrelated assignment; releasing
/// runs in a transition body's finally, so an escape there strands every hold behind it and the host
/// waits on a completion that never comes. These switches are what make both observable.
/// </summary>
public sealed class ThrowingStartupDeferrer : IStartupCompletionDeferrer
{
    private int _taken;
    private int _released;

    /// <summary>Throws instead of returning a hold, so nothing is left to release.</summary>
    public bool ThrowOnDefer { get; init; }

    /// <summary>Returns a hold whose disposal throws, so the release loop has to survive it.</summary>
    public bool ThrowOnRelease { get; init; }

    /// <summary>Calls that reached this deferrer, whether or not they threw.</summary>
    public int Taken => Volatile.Read(ref _taken);

    /// <summary>Disposals that reached this deferrer's hold, whether or not they threw.</summary>
    public int Released => Volatile.Read(ref _released);

    public IDisposable DeferCompletion()
    {
        Interlocked.Increment(ref _taken);

        if (ThrowOnDefer)
        {
            throw new InvalidOperationException("taking a hold failed");
        }

        return new Hold(this);
    }

    private sealed class Hold : IDisposable
    {
        private readonly ThrowingStartupDeferrer _owner;

        public Hold(ThrowingStartupDeferrer owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _owner._released);

            if (_owner.ThrowOnRelease)
            {
                throw new InvalidOperationException("releasing a hold failed");
            }
        }
    }
}
