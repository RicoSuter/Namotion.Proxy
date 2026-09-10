using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A hosted subject comparing by value, which is legal for a hand written subject and must not merge
/// two graph nodes. Every subject keyed collection in the handler has to say so explicitly.
/// </summary>
[InterceptorSubject]
public partial class ValueEqualityHostedSubject : IHostedService
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override bool Equals(object? obj) => obj is ValueEqualityHostedSubject;

    public override int GetHashCode() => 0;
}

/// <summary>Holds two of them in separate properties, so one can leave while the other stays.</summary>
[InterceptorSubject]
public partial class ValueEqualityContainer
{
    public partial ValueEqualityHostedSubject? First { get; set; }

    public partial ValueEqualityHostedSubject? Second { get; set; }
}
