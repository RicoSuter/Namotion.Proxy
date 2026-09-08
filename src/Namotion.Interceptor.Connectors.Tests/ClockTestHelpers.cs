namespace Namotion.Interceptor.Connectors.Tests;

internal static class ClockTestHelpers
{
    /// <summary>
    /// Spins until the wall clock reports a new tick, so a timestamp stamped after this call cannot
    /// land on the same value as one stamped before it. A condition rather than a fixed delay,
    /// because the clock's resolution differs per platform and is coarse on Windows.
    /// </summary>
    internal static void WaitForClockTick()
    {
        var start = DateTimeOffset.UtcNow.UtcTicks;

        SpinWait spin = default;
        while (DateTimeOffset.UtcNow.UtcTicks == start)
        {
            spin.SpinOnce();
        }
    }
}
