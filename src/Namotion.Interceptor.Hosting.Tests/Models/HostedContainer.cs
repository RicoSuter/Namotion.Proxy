using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A container that is its own hosted service and holds one hosted child. Both halves are counted, so
/// a child created from a lifecycle handler during the container's own context attach can be told
/// apart from the container's own start rather than inferred from a side effect.
/// </summary>
[InterceptorSubject]
public partial class HostedContainer : IHostedService
{
    private int _startCount;
    private int _stopCount;

    public partial CountingHostedSubject? Child { get; set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startCount);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopCount);
        return Task.CompletedTask;
    }
}
