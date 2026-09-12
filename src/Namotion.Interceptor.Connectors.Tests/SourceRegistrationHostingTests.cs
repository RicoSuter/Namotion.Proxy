using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Covers the interaction between source monitoring and the hosted-service attach path.
/// </summary>
/// <remarks>
/// This is the only place the two meet. Namotion.Interceptor.Hosting and
/// Namotion.Interceptor.Connectors are siblings that do not reference each other, so before this
/// file no test project referenced both and the combination was entirely uncovered - which is how
/// the defect these tests pin survived: the registration gate fires on ApplicationStarted, but a
/// source attached to the subject graph is started from a queue that ApplicationStarted does not
/// wait for, so registration completed while the source had not registered yet and a wait on that
/// branch completed vacuously against an unsynchronized tree.
/// <para>
/// The attach API takes a factory, so the handler owns the instance and a test cannot hold a
/// reference to one that is still inside StartAsync. The tests therefore drive the services through
/// a <see cref="StartRelease"/> they own, and reach a started instance through the attachment's
/// <c>Current</c>.
/// </para>
/// </remarks>
public class SourceRegistrationHostingTests
{
    [Fact]
    public async Task WhenAnAttachedHostedServiceHasNotStartedYet_ThenRegistrationIsNotCompleteAtHostStart()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        using var startRelease = new StartRelease();

        // Attached before the host starts, so its start is queued and drains asynchronously while
        // the host's own startup runs to completion.
        root.AttachHostedService(() => new GatedStartHostedService(startRelease));

        var host = builder.Build();

        // Act
        await host.StartAsync();

        // Assert - ApplicationStarted has fired and released the monitor's initial hold, but this
        // service is still sitting in StartAsync, so the hold taken when it was attached is still
        // outstanding and registration must not be complete.
        Assert.False(monitor.IsRegistrationComplete);

        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        startRelease.ReleaseStart();

        await AsyncTestHelpers.WaitUntilAsync(() => monitor.IsRegistrationComplete);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenAnAttachedHostedServiceStartThrows_ThenItsHoldIsStillReleased()
    {
        // Arrange
        // The hold is released in a finally, so a failing start cannot wedge every wait on the tree
        // forever. Without that, this is a permanent hang rather than a wrong answer.
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        using var startRelease = new StartRelease();

        // Gated, then throwing: a start that failed immediately would let this test pass with no
        // hold ever taken, since registration would simply have completed at host start.
        root.AttachHostedService(() => new ThrowingStartHostedService(startRelease));

        var host = builder.Build();

        // Act
        await host.StartAsync();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);

        startRelease.ReleaseStart();

        await AsyncTestHelpers.WaitUntilAsync(() => monitor.IsRegistrationComplete);
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenAHandlerThrowsOnASubscriptionMadeBeforeTheHostIsBuilt_ThenTheErrorIsLogged()
    {
        // Arrange
        // Companion to the test above, one layer down: a subscription used to capture the resolved
        // logger at Subscribe time, which is typically before the ILoggerFactory reaches the context,
        // so every exception a handler threw was swallowed for that subscription's lifetime.
        var builder = Host.CreateApplicationBuilder();
        var recordingLogger = new RecordingLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RecordingLoggerProvider(recordingLogger));

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        using var subscription = monitor.Subscribe(_ => throw new InvalidOperationException("handler is buggy"));

        var host = builder.Build();
        await host.StartAsync();

        // Act
        monitor.Register(new TestStateSource(root));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => recordingLogger.Errors.Any(message => message.Contains("source event handler threw")));

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenAnAwaitedAttachIsStillStarting_ThenRegistrationIsHeldOpen()
    {
        // Arrange
        // The awaiting attach overload blocks its own caller, but that does not block whatever else
        // decides startup is finished, so it needs a hold like the fire-and-forget path. Without one,
        // a wait taken while this start is still queued completes vacuously.
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        var host = builder.Build();
        await host.StartAsync();
        Assert.True(monitor.IsRegistrationComplete);

        using var startRelease = new StartRelease();

        // Act - the hold is taken synchronously, before the returned task is handed back.
        var attach = root.AttachHostedServiceAsync(
            () => new GatedStartHostedService(startRelease), CancellationToken.None);

        // Assert
        Assert.False(monitor.IsRegistrationComplete);

        startRelease.ReleaseStart();
        var attachment = await attach.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(attachment.Current);
        await AsyncTestHelpers.WaitUntilAsync(() => monitor.IsRegistrationComplete);

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenAStartingServiceAttachesAChild_ThenRegistrationStaysHeldUntilTheChildStarts()
    {
        // Arrange
        // HostedServiceHandler guarantees that nested attaches compose: a service that attaches
        // children during its own StartAsync takes their holds before its own is released, so the
        // count never reaches zero in between. Nothing tested that, and it is the case where a
        // single-level barrier would let go too early.
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        var host = builder.Build();
        await host.StartAsync();
        Assert.True(monitor.IsRegistrationComplete);

        using var childRelease = new StartRelease();

        // Act - the awaiting overload returns only once the parent's own start transition has run to
        // completion, so the parent's hold is provably gone by the time the assertion below reads the
        // count. The child is attached through the fire-and-forget path from inside that start.
        await root.AttachHostedServiceAsync(
            () => new ChildAttachingHostedService(root, () => new GatedStartHostedService(childRelease)),
            CancellationToken.None);

        // Assert - the parent has finished starting and released its hold, but the child is still
        // inside StartAsync, so registration must still be held open by the child's hold.
        Assert.False(monitor.IsRegistrationComplete);

        childRelease.ReleaseStart();
        await AsyncTestHelpers.WaitUntilAsync(() => monitor.IsRegistrationComplete);

        await host.StopAsync();
    }

    [Fact]
    public async Task WhenNoHostedServiceIsAttached_ThenHostStartAloneCompletesRegistration()
    {
        // Arrange
        // Companion to the first test: proves the assertion there distinguishes the two cases,
        // rather than registration simply never completing at host start.
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithSourceMonitoring(builder.Services)
            .WithHostedServices(builder.Services);

        var root = new Person(context);
        var monitor = context.GetSourceMonitor();
        var host = builder.Build();

        // Act
        await host.StartAsync();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
        await root.WaitForSynchronizationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopAsync();
    }
}

/// <summary>
/// The signal a gated hosted service waits on inside StartAsync. Owned by the test rather than by
/// the service, because the attach API takes a factory: the handler constructs the instance, so a
/// test has no reference to one that is still inside StartAsync.
/// </summary>
internal sealed class StartRelease : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);

    public void ReleaseStart() => _release.Set();

    public void WaitForRelease(CancellationToken cancellationToken)
    {
        // Bounded, so a mechanism that never releases fails the test instead of hanging the run.
        _release.Wait(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public void Dispose() => _release.Dispose();
}

/// <summary>A hosted service whose StartAsync blocks until the test releases it.</summary>
internal sealed class GatedStartHostedService(StartRelease release) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => Task.Run(() => release.WaitForRelease(cancellationToken), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A hosted service whose StartAsync fails once the test releases it.</summary>
internal sealed class ThrowingStartHostedService(StartRelease release) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() => release.WaitForRelease(cancellationToken), cancellationToken);
        throw new InvalidOperationException("start failed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A hosted service that attaches another one from inside its own StartAsync.</summary>
internal sealed class ChildAttachingHostedService(IInterceptorSubject subject, Func<IHostedService> childFactory)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        subject.AttachHostedService(childFactory);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
