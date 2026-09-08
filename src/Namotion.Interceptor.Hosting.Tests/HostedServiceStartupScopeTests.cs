using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceStartupScopeTests
{
    [Fact]
    public async Task WhenNestedScopeCompletes_ThenStartWaitsForTheOuterScope()
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
                inner!.Complete();
            }
            await fixture.Handler.StartAsync(CancellationToken.None);
            Assert.False(service.Started.Task.IsCompleted);
            configuration = "configured";
            outer!.Complete();
        }

        // Assert
        Assert.Equal("configured", await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAScopeFails_ThenItsQueuedStartIsCanceled(bool outerFails)
    {
        // Arrange
        await using var fixture = new Fixture();
        var service = new ProbeService(() => "started");
        var subject = new Person(fixture.Context);
        Task attachment;

        // Act
        using (var outer = fixture.Context.DeferHostedServiceStartup())
        {
            using (var inner = fixture.Context.DeferHostedServiceStartup())
            {
                attachment = subject.AttachHostedServiceAsync(service, CancellationToken.None);
                if (outerFails) inner!.Complete();
            }
            if (!outerFails) outer!.Complete();
        }

        // Assert
        await fixture.Handler.StartAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(service.Started.Task.IsCompleted);
        await fixture.HoldsReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenHandlerStopsDuringAnOpenScope_ThenStartIsCanceledAndItsCompletionHoldIsReleased()
    {
        // Arrange
        await using var fixture = new Fixture();
        var service = new ProbeService(() => "started");
        var subject = new Person(fixture.Context);
        using var scope = fixture.Context.DeferHostedServiceStartup();
        var attachment = subject.AttachHostedServiceAsync(service, CancellationToken.None);

        // Act
        await fixture.Handler.StartAsync(CancellationToken.None);
        await fixture.Handler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attachment.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.HoldsReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(service.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenHandlerStartsInsideAFailedScope_ThenLaterServicesCanStartTheirChildren()
    {
        // Arrange
        await using var fixture = new Fixture();
        using (fixture.Context.DeferHostedServiceStartup())
        {
            await fixture.Handler.StartAsync(CancellationToken.None);
        }
        var subject = new Person(fixture.Context);
        var child = new ProbeService(() => "child");
        var parent = new ProbeService(() =>
        {
            subject.AttachHostedService(child);
            return "parent";
        });

        // Act
        await subject.AttachHostedServiceAsync(parent, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal("child", await child.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class Fixture : IAsyncDisposable, IStartupCompletionDeferrer
    {
        private readonly ServiceProvider _provider;
        public IInterceptorSubjectContext Context { get; }
        public IHostedService Handler { get; }
        public TaskCompletionSource HoldsReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            var services = new ServiceCollection().AddLogging();
            Context = InterceptorSubjectContext.Create().WithHostedServices(services);
            Context.AddService<IStartupCompletionDeferrer>(this);
            _provider = services.BuildServiceProvider();
            Handler = Assert.Single(_provider.GetServices<IHostedService>());
        }

        public IDisposable DeferCompletion() => new CompletionHold(HoldsReleased);

        public async ValueTask DisposeAsync()
        {
            await Handler.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }

    private sealed class CompletionHold(TaskCompletionSource released) : IDisposable
    {
        public void Dispose() => released.TrySetResult();
    }

    private sealed class ProbeService(Func<string> readConfiguration) : IHostedService
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(readConfiguration());
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
