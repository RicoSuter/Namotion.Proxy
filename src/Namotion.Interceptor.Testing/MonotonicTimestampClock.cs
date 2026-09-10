namespace Namotion.Interceptor.Testing;

/// <summary>
/// Timestamp function that never returns the same value twice, so two writes stamped in sequence always
/// carry distinct timestamps. Install it with <see cref="Install"/> from a module initializer in the test
/// assembly that needs it: the function is process-global, and installing it per test leaves a race
/// between parallel test classes.
/// <para>
/// Tests need this because the wall clock resolves to a microsecond, which two property writes stay well
/// inside once the process is warm, so anything that reads two timestamps as distinct fails only under
/// load and only in a full-assembly run.
/// </para>
/// </summary>
public static class MonotonicTimestampClock
{
    private static long _lastTicks;

    [ThreadStatic]
    private static int _threadCount;

    /// <summary>
    /// Gets how many times the clock was captured on the calling thread. Tests that assert a capture
    /// count (lazy-cache verification, cascade snap counting) read this before and after the work and
    /// diff it; the counter is thread-static, so concurrent tests cannot pollute it.
    /// </summary>
    public static int CurrentThreadCount => _threadCount;

    /// <summary>
    /// Installs this clock as <see cref="SubjectChangeContext.GetTimestampFunction"/> for the process.
    /// </summary>
    public static void Install()
    {
        SubjectChangeContext.GetTimestampFunction = Capture;
    }

    /// <summary>
    /// Returns the current time, or one tick past the previous return when the clock has not advanced.
    /// </summary>
    public static DateTimeOffset Capture()
    {
        _threadCount++;

        long previousTicks, nextTicks;
        do
        {
            previousTicks = Volatile.Read(ref _lastTicks);
            nextTicks = Math.Max(DateTime.UtcNow.Ticks, previousTicks + 1);
        }
        while (Interlocked.CompareExchange(ref _lastTicks, nextTicks, previousTicks) != previousTicks);

        return new DateTimeOffset(nextTicks, TimeSpan.Zero);
    }
}
