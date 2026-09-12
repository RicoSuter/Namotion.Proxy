using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A startup completion deferrer that counts its outstanding holds and lets a test run code at the
/// moment a hold is taken. The handler takes the hold after the attachment has been published and
/// before its start is appended, so the callback is the one piece of code that can drive that window
/// without a delay.
/// </summary>
public sealed class CallbackStartupDeferrer : IStartupCompletionDeferrer
{
    private int _outstanding;
    private int _taken;

    /// <summary>Invoked while the hold is being taken. Exceptions are swallowed by the handler.</summary>
    public Action? OnDefer { get; set; }

    /// <summary>Holds taken but not yet released.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Holds taken so far, which tells "released again" apart from "never taken".</summary>
    public int Taken => Volatile.Read(ref _taken);

    public IDisposable DeferCompletion()
    {
        Interlocked.Increment(ref _outstanding);
        Interlocked.Increment(ref _taken);

        OnDefer?.Invoke();
        return new Hold(this);
    }

    private sealed class Hold : IDisposable
    {
        private readonly CallbackStartupDeferrer _owner;
        private int _disposed;

        public Hold(CallbackStartupDeferrer owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _owner._outstanding);
            }
        }
    }
}
