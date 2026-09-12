using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// Starts for subjects entering the graph together must overlap, not run one after another. Each start
/// is appended to its own target's chain and nothing awaits them in turn; an earlier implementation
/// posted every start to one shared consumer loop and paid them in series.
/// </summary>
/// <remarks>
/// Asserted by making overlap the only way the test can finish rather than by timing it. Every subject
/// reports arrival in its own <c>StartAsync</c> and then waits for all the others to arrive, so the run
/// completes only if all of them are inside <c>StartAsync</c> at once. Serialized starts cannot reach
/// the second arrival, because the first is still waiting.
/// <para>
/// A ratio of elapsed times was tried first and was wrong: thirty two subjects against one measured a
/// ratio near 1.0 on a developer machine and 5.4 on a two core continuous integration runner, so any
/// tolerance wide enough to be stable there was close to the signal it was meant to detect. This
/// version has no tolerance and no timing dependence, and it does not care how long a start takes.
/// </para>
/// <para>
/// The waits are asynchronous rather than blocking, so thirty two pending starts occupy no thread pool
/// threads. A blocking version starves a small runner and fails for a reason unrelated to the property.
/// </para>
/// <para>
/// This pins that starts overlap, and nothing else. It does not pin that each target's own transitions
/// are serialized, which
/// <see cref="HostedServiceTargetTests.WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap"/>
/// covers.
/// </para>
/// </remarks>
public class HostedServiceStartupShapeTests
{
    private const int ManySubjects = 32;

    [Fact]
    public async Task WhenManySubjectsEnterTheGraphTogether_ThenTheirStartsOverlap()
    {
        // Arrange
        var (host, context) = await HostingTestHost.StartAsync();

        try
        {
            var graph = new StartupShapeGraph(context);
            var rendezvous = new StartupRendezvous(ManySubjects);

            var children = new StartupShapeSubject[ManySubjects];
            for (var index = 0; index < ManySubjects; index++)
            {
                children[index] = new StartupShapeSubject { Rendezvous = rendezvous };
            }

            // Act
            graph.Children = children;

            // Assert - completes only when every subject is inside StartAsync at the same time.
            // Serialized starts never reach the second arrival, so this is the whole assertion.
            await rendezvous.WaitForAllAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}

/// <summary>
/// Completes once the expected number of starts have arrived, and releases all of them together.
/// </summary>
public sealed class StartupRendezvous
{
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _expected;

    private int _arrived;

    public StartupRendezvous(int expected)
    {
        _expected = expected;
    }

    /// <summary>Reports one arrival and waits for the rest, without occupying a thread.</summary>
    public Task ArriveAndWaitAsync()
    {
        if (Interlocked.Increment(ref _arrived) == _expected)
        {
            _allArrived.TrySetResult();
        }

        return _allArrived.Task;
    }

    public async Task WaitForAllAsync(TimeSpan timeout)
    {
        try
        {
            await _allArrived.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Only {Volatile.Read(ref _arrived)} of {_expected} starts were running at once. " +
                "Starts that overlap all arrive; starts that run in series never reach the second, " +
                "because the first is still waiting here.");
        }
    }
}

[InterceptorSubject]
public partial class StartupShapeGraph
{
    public partial StartupShapeSubject[]? Children { get; set; }
}

[InterceptorSubject]
public partial class StartupShapeSubject : IHostedService
{
    public StartupRendezvous? Rendezvous { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
        => Rendezvous?.ArriveAndWaitAsync() ?? Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
