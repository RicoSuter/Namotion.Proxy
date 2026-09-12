using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>
/// A subject whose only declared constructor takes dependencies, so the generator emits no
/// (IInterceptorSubjectContext) constructor. This is the shape every HomeBlaze device has.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithDependencies : BackgroundService
{
    private readonly ILogger<SubjectWithDependencies> _logger;

    public partial string? Name { get; set; }

    public int StartCount;

    /// <summary>
    /// The value the start observed, which is how a configure callback that lost the race with the
    /// start is measurable rather than inferred.
    /// </summary>
    public string? NameAtStart { get; private set; }

    public SubjectWithDependencies(ILogger<SubjectWithDependencies> logger)
    {
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        NameAtStart = Name;
        Interlocked.Increment(ref StartCount);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
