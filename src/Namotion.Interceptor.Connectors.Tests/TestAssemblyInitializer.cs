using System.Runtime.CompilerServices;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Connectors.Tests;

internal static class TestAssemblyInitializer
{
    /// <summary>
    /// The snapshots under <c>Updates</c> need distinct timestamps per write: Verify names scrubbed
    /// timestamps by value, so two writes sharing one render as a single name where the snapshot
    /// records two.
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        MonotonicTimestampClock.Install();
    }
}
