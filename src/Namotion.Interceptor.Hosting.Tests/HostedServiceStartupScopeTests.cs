using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// The startup scope: a start captured in one waits for it, and for every scope enclosing it, before
/// the handler creates or starts anything.
/// </summary>
/// <remarks>
/// What a released start then does is the handler's contract rather than the scope's, so it is pinned
/// in <see cref="HostedServiceHandlerTests"/> and <see cref="HostedServiceHandlerRaceTests"/>. The
/// scope's own drain interaction is here, because nothing else parks a transition on a caller.
/// </remarks>
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
        var independentAttachment = alreadyStarted
            ? await subject.AttachHostedServiceAsync(() => independent, CancellationToken.None)
            : null;

        // Started before the scope opens, so its flow never carries the scope and its attach is not
        // captured by it. That is the property under test.
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentFlow = Task.Run(async () =>
        {
            await proceed.Task;
            independentAttachment ??= await subject.AttachHostedServiceAsync(() => independent, CancellationToken.None);
            await subject.DetachHostedServiceAsync(independentAttachment, CancellationToken.None);
        });

        // Act
        using (fixture.Context.DeferHostedServiceStartup())
        {
            subject.AttachHostedService(() => scoped);
            proceed.SetResult();
            await independentFlow.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("independent", await independent.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(scoped.Started.Task.IsCompleted);
        }

        Assert.Equal("scoped", await scoped.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
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
        using (fixture.Context.DeferHostedServiceStartup())
        {
            using (fixture.Context.DeferHostedServiceStartup())
            {
                subject.AttachHostedService(() => service);
            }

            await fixture.Handler.StartAsync(CancellationToken.None);
            Assert.False(service.Started.Task.IsCompleted);
            configuration = "configured";
        }

        // Assert
        Assert.Equal("configured", await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
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
        subject.AttachHostedService(() => captured);

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
        await subject.AttachHostedServiceAsync(() => later, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("later", await later.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WhenTheDrainBeginsWhileAScopeIsOpen_ThenItReleasesTheParkedStartInsteadOfWaitingForIt()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var deferred = new ProbeService(() => "deferred");

        // Act - the scope stays open across the whole shutdown, so a drain that waited the scope out
        // rather than releasing the start parked on it would hold its barrier until this scope is
        // disposed, which is after the assertions below.
        using (fixture.Context.DeferHostedServiceStartup())
        {
            subject.AttachHostedService(() => deferred);
            Assert.Equal(1, fixture.HoldsTaken);

            await fixture.Handler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            // Assert - a released start declines rather than starting, because the drain re-reads the
            // gate, and its startup hold is released either way.
            Assert.False(deferred.Started.Task.IsCompleted);
            await fixture.HoldsReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WhenAStopPathAttachesAService_ThenAnUnrelatedOpenScopeDoesNotCaptureIt()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var second = new ProbeService(() => "second");
        var first = new ProbeService(() => "first", stopped: () => subject.AttachHostedService(() => second));
        var attachment = await subject
            .AttachHostedServiceAsync(() => first, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Act - the detach is not awaited, which is what this scope's own rules ask of a caller, so its
        // stop body runs while the scope is still open and with the execution context of this flow.
        using (fixture.Context.DeferHostedServiceStartup())
        {
            subject.DetachHostedService(attachment);

            // Assert - what a stop path attaches belongs to no scope this caller opened.
            Assert.Equal("second", await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task WhenAStopPathAwaitsItsOwnAttach_ThenAnUnrelatedOpenScopeDoesNotWedgeTheChain()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.Handler.StartAsync(CancellationToken.None);
        var subject = new Person(fixture.Context);
        var second = new ProbeService(() => "second");
        Func<Task>? attachFromStop = null;
        var first = new ProbeService(() => "first", stopping: () => attachFromStop!());
        attachFromStop = () => subject.AttachHostedServiceAsync(() => second, CancellationToken.None);
        var attachment = await subject
            .AttachHostedServiceAsync(() => first, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Act - this stop is not captured by the scope, having been attached and started before it
        // opened, so awaiting the detach inside the scope is not the hazard awaiting a captured start
        // is. What the stop path attaches is what an inherited scope would park.
        using (fixture.Context.DeferHostedServiceStartup())
        {
            var detachment = subject.DetachHostedServiceAsync(attachment, CancellationToken.None);

            // Assert - the stop body returns rather than holding its chain for as long as this scope
            // stays open.
            Assert.True(await detachment.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("second", await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
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

    private sealed class ProbeService(
        Func<string> readConfiguration,
        Action? stopped = null,
        Task? startup = null,
        Action<CancellationToken>? starting = null,
        Func<Task>? stopping = null) : IHostedService
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            starting?.Invoke(cancellationToken);
            Started.TrySetResult(readConfiguration());
            return startup ?? Task.CompletedTask;
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            stopped?.Invoke();
            if (stopping is not null)
            {
                await stopping();
            }
        }
    }
}
