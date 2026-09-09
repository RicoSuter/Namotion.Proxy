using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceStartupScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenScopeIsOpen_ThenIndependentFlowCanStartAndStopServices(bool alreadyStarted)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var scoped = new ProbeService(() => "scoped");
        var independent = new ProbeService(() => "independent");
        if (alreadyStarted)
        {
            await subject.AttachHostedServiceAsync(independent, CancellationToken.None);
        }
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentFlow = Task.Run(async () =>
        {
            await proceed.Task;
            if (!alreadyStarted)
            {
                await subject.AttachHostedServiceAsync(independent, CancellationToken.None);
            }
            await subject.DetachHostedServiceAsync(independent, CancellationToken.None);
        });

        // Act
        using (var scope = fixture.Context.DeferHostedServiceStartup())
        {
            subject.AttachHostedService(scoped);
            proceed.SetResult();
            await independentFlow.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.False(scoped.Started.Task.IsCompleted);
        }
        await scoped.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenNestedScopeIsDisposed_ThenStartWaitsForTheOuterScope()
    {
        // Arrange
        await using var fixture = new Fixture();
        var configuration = "uninitialized";
        var service = new ProbeService(() => configuration);
        var subject = new Person(fixture.Context);

        // Act
        using (var outer = fixture.Context.DeferHostedServiceStartup())
        {
            using (var inner = fixture.Context.DeferHostedServiceStartup())
            {
                subject.AttachHostedService(service);
            }
            await fixture.Handler.StartAsync(CancellationToken.None);
            Assert.False(service.Started.Task.IsCompleted);
            configuration = "configured";
        }

        // Assert
        Assert.Equal("configured", await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task WhenHandlerStopsDuringAnOpenScope_ThenAllStartsAreCanceledAndHoldsAreReleased(int count)
    {
        // Arrange
        await using var fixture = new Fixture();
        var services = Enumerable.Range(0, count).Select(_ => new ProbeService(() => "started")).ToArray();
        var subject = new Person(fixture.Context);
        using var scope = fixture.Context.DeferHostedServiceStartup();
        var attachments = services.Select(service => subject.AttachHostedServiceAsync(service, CancellationToken.None)).ToArray();
        Assert.Equal(count, fixture.HoldsTaken);
        Assert.Equal(0, fixture.HoldsDisposed);

        // Act
        await fixture.Handler.StartAsync(CancellationToken.None);
        await fixture.Handler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        foreach (var attachment in attachments)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(count, fixture.HoldsDisposed);
        Assert.All(services, service => Assert.False(service.Started.Task.IsCompleted));
    }

    [Fact]
    public async Task WhenShutdownCancellationDetachesADeferredService_ThenShutdownStillStopsIt()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new ProbeService(() => "deferred", () => stopped.TrySetResult());
        var blocking = new ProbeService(() => "blocking", startup: releaseStart.Task,
            starting: token => token.Register(() => subject.DetachHostedService(deferred)));
        subject.AttachHostedService(blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var scope = fixture.Context.DeferHostedServiceStartup();
        subject.AttachHostedService(deferred);
        Task? stopping = null;

        try
        {
            // Act
            stopping = fixture.Handler.StopAsync(CancellationToken.None);

            // Assert
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deferred.Started.Task.IsCompleted);
        }
        finally
        {
            releaseStart.TrySetResult();
            await (stopping ?? fixture.Handler.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(2, fixture.HoldsDisposed);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("Dispose")]
    [InlineData("Cancellation")]
    public async Task WhenShutdownBeginsWhileAStartIsBlocked_ThenReattachmentTakesNoStartupHold(string shutdown)
    {
        // Arrange
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await fixture.Handler.StartAsync(cancellation.Token);
        var subject = new Person(fixture.Context);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new ProbeService(() => "blocking", startup: releaseStart.Task);
        subject.AttachHostedService(blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var scope = fixture.Context.DeferHostedServiceStartup();
        var deferred = new ProbeService(() => "deferred", () => stopped.TrySetResult());
        var attachment = subject.AttachHostedServiceAsync(deferred, CancellationToken.None);
        Task? stopping = null;

        try
        {
            // Act
            if (shutdown == "Stop")
            {
                stopping = fixture.Handler.StopAsync(CancellationToken.None);
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else if (shutdown == "Dispose")
            {
                ((IDisposable)fixture.Handler).Dispose();
            }
            else
            {
                await cancellation.CancelAsync();
            }
            subject.DetachHostedService(deferred);
            var exception = Record.Exception(() => subject.AttachHostedService(deferred));

            // Assert
            Assert.Null(exception);
            Assert.Equal(2, fixture.HoldsTaken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subject.AttachHostedServiceAsync(
                new ProbeService(() => "late"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, fixture.HoldsTaken);
        }
        finally
        {
            releaseStart.TrySetResult();
            await (stopping ?? fixture.Handler.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, fixture.HoldsDisposed);
        Assert.False(deferred.Started.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeferredServiceIsDetached_ThenItsStartIsCanceledBeforeScopeRelease(bool reattach)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var events = new List<string>();
        var service = new ProbeService(() => { events.Add("start"); return "started"; }, () => events.Add("stop"));
        var subject = new Person(fixture.Context);
        Task? secondAttachment = null;

        // Act
        using (var scope = fixture.Context.DeferHostedServiceStartup())
        {
            var firstAttachment = subject.AttachHostedServiceAsync(service, CancellationToken.None);
            var detachment = subject.DetachHostedServiceAsync(service, CancellationToken.None);
            if (reattach)
            {
                secondAttachment = subject.AttachHostedServiceAsync(service, CancellationToken.None);
            }
            await detachment.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttachment.WaitAsync(TimeSpan.FromSeconds(10)));
            await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref fixture.HoldsDisposed) == 1);
            Assert.False(service.Started.Task.IsCompleted);
        }
        if (secondAttachment is not null)
        {
            await secondAttachment.WaitAsync(TimeSpan.FromSeconds(10));
        }
        // A queued independent start is a barrier for all previously eligible actions.
        await subject.AttachHostedServiceAsync(new ProbeService(() => "barrier"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(reattach ? new[] { "stop", "start" } : new[] { "stop" }, events);
        await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref fixture.HoldsDisposed) == fixture.HoldsTaken);
        Assert.Equal(fixture.HoldsTaken, fixture.HoldsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenScopeReleasesSeveralStarts_ThenTheyRunInAttachmentOrder(bool nested)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var events = new List<string>();
        var subject = new Person(fixture.Context);
        var attachments = new List<Task>();

        // Act
        using (var outer = fixture.Context.DeferHostedServiceStartup())
        {
            using (var inner = nested ? fixture.Context.DeferHostedServiceStartup() : null)
            {
                attachments.Add(subject.AttachHostedServiceAsync(new ProbeService(() => { events.Add("first"); return "first"; }), CancellationToken.None));
            }
            attachments.Add(subject.AttachHostedServiceAsync(new ProbeService(() => { events.Add("second"); return "second"; }), CancellationToken.None));
            Assert.Equal(2, fixture.HoldsTaken);
            Assert.Equal(0, fixture.HoldsDisposed);
        }
        await Task.WhenAll(attachments).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.HoldsReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(new[] { "first", "second" }, events);
        Assert.Equal(2, fixture.HoldsDisposed);
    }

    [Fact]
    public async Task WhenHandlerStartsInsideAnOpenScope_ThenTheActionLoopDoesNotInheritIt()
    {
        // Arrange
        await using var fixture = new Fixture();
        var subject = new Person(fixture.Context);
        var child = new ProbeService(() => "child");
        var parent = new ProbeService(() =>
        {
            subject.AttachHostedService(child);
            return "parent";
        });

        // Attached from a flow created before the scope below, so the scope never captures this
        // attach and the loop's own flow is the only thing that can defer the child.
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentFlow = Task.Run(async () =>
        {
            await proceed.Task;
            await subject.AttachHostedServiceAsync(parent, CancellationToken.None);
        });

        // Act - the scope stays open across the start, so a loop that inherited the flow it was
        // started in would capture the child that parent attaches inside its own StartAsync and
        // hold it until this scope is disposed.
        using (fixture.Context.DeferHostedServiceStartup())
        {
            await fixture.Handler.StartAsync(CancellationToken.None);
            proceed.SetResult();
            await independentFlow.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("child", await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [InlineData("OutOfOrder")]
    [InlineData("OtherFlow")]
    [InlineData("Twice")]
    public async Task WhenAScopeIsDisposedIrregularly_ThenCapturedAndLaterServicesCanStart(string disposal)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var captured = new ProbeService(() => "captured");
        var outer = fixture.Context.DeferHostedServiceStartup();
        var inner = fixture.Context.DeferHostedServiceStartup();
        subject.AttachHostedService(captured);

        // Act
        var irregular = Record.Exception(() =>
        {
            switch (disposal)
            {
                case "OutOfOrder": outer!.Dispose(); inner!.Dispose(); break;
                case "OtherFlow": Task.Run(() => inner!.Dispose()).GetAwaiter().GetResult(); inner!.Dispose(); outer!.Dispose(); break;
                default: inner!.Dispose(); inner.Dispose(); outer!.Dispose(); outer.Dispose(); break;
            }
        });

        // Assert
        Assert.Null(irregular);
        Assert.Equal("captured", await captured.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var later = new ProbeService(() => "later");
        await subject.AttachHostedServiceAsync(later, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenAStopIsStillQueuedAtShutdown_ThenTheDetachedServiceIsStillStopped()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = new ProbeService(() => "detached", () => stopped.TrySetResult());
        await subject.AttachHostedServiceAsync(detached, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Occupies the loop, so the stop queued below is still waiting when shutdown begins.
        var blocking = new ProbeService(() => "blocking", startup: releaseStart.Task);
        subject.AttachHostedService(blocking);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        subject.DetachHostedService(detached);

        // Act
        var stopping = fixture.Handler.StopAsync(CancellationToken.None);
        releaseStart.SetResult();

        // Assert
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenAnAwaitedAttachResumes_ThenItDoesNotOccupyTheActionLoop()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var next = new ProbeService(() => "next");

        // Act - the continuation attaches another service and waits for it, so it can only finish
        // if the loop is free to run that start rather than being inside this continuation.
        var attaching = Task.Run(async () =>
        {
            await subject.AttachHostedServiceAsync(new ProbeService(() => "first"), CancellationToken.None);
            subject.AttachHostedService(next);
            return next.Started.Task.Wait(TimeSpan.FromSeconds(5));
        });

        // Assert
        Assert.True(await attaching.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class Fixture : IAsyncDisposable, IStartupCompletionDeferrer
    {
        private readonly ServiceProvider _provider;
        public IInterceptorSubjectContext Context { get; }
        public IHostedService Handler { get; }
        public int HoldsTaken;
        public int HoldsDisposed;
        public TaskCompletionSource HoldsReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var services = new ServiceCollection().AddLogging();
            Context = InterceptorSubjectContext.Create().WithHostedServices(services);
            Context.AddService<IStartupCompletionDeferrer>(this);
            _provider = services.BuildServiceProvider();
            Handler = Assert.Single(_provider.GetServices<IHostedService>());
        }

        public IDisposable DeferCompletion()
        {
            Interlocked.Increment(ref HoldsTaken);
            return new CompletionHold(() =>
            {
                if (Interlocked.Increment(ref HoldsDisposed) == Volatile.Read(ref HoldsTaken))
                {
                    HoldsReleased.TrySetResult();
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Handler.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }

    private sealed class CompletionHold(Action released) : IDisposable
    {
        public void Dispose() => released();
    }

    private sealed class ProbeService(Func<string> readConfiguration, Action? stopped = null, Task? startup = null, Action<CancellationToken>? starting = null) : IHostedService
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            starting?.Invoke(cancellationToken);
            Started.TrySetResult(readConfiguration());
            return startup ?? Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            stopped?.Invoke();
            return Task.CompletedTask;
        }
    }
}
