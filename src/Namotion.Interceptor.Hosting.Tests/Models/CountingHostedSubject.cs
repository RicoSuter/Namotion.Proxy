using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject that is its own hosted service and counts the calls, so a double start is measurable
/// rather than inferred from a side effect.
/// </summary>
[InterceptorSubject]
public partial class CountingHostedSubject : IHostedService
{
    private int _startCount;
    private int _stopCount;

    public partial string? Name { get; set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    /// <summary>
    /// Awaited inside <see cref="StopAsync"/>. Lets a test hold the subject mid stop and observe what
    /// the handler does to the subject's attachments while it is still unwinding.
    /// </summary>
    public Func<Task>? StopHold { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);

        if (StopHold is { } hold)
        {
            await hold();
        }
    }
}
