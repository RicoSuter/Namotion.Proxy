using System.Runtime.CompilerServices;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tracking.Tests;

internal static class TestAssemblyInitializer
{
    /// <summary>
    /// Tests here read sequential write timestamps as distinct values and count clock captures, neither
    /// of which the wall clock can be relied on for.
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        MonotonicTimestampClock.Install();
    }
}
