using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject whose own start always fails, so a caller waiting for that start has a fault to observe.
/// </summary>
[InterceptorSubject]
public partial class ThrowingHostedSubject : IHostedService
{
    public partial string? Name { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("start failed");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
