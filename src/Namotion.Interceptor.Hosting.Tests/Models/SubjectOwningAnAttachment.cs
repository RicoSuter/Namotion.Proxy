using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// The shape both OPC UA wrappers have: a subject that is its own hosted service and owns a factory
/// attachment it creates from <see cref="ExecuteAsync"/>. The unwind deliberately does not detach that
/// attachment. It runs inside the handler's stop transition for this subject, and the attachment's stop
/// is queued behind that transition, so a detach from here waits on a chain that is waiting on this
/// method to return, and the host recovers only when its shutdown timeout expires.
/// </summary>
[InterceptorSubject]
public partial class SubjectOwningAnAttachment : BackgroundService
{
    private TrackedBackgroundService? _instance;

    public partial string? Name { get; set; }

    /// <summary>The instance the factory built, or null while the attachment has started nothing.</summary>
    public TrackedBackgroundService? Instance => Volatile.Read(ref _instance);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await this.AttachHostedServiceAsync(() =>
        {
            var instance = new TrackedBackgroundService();
            Volatile.Write(ref _instance, instance);
            return instance;
        }, CancellationToken.None);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
