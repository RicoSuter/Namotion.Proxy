using HomeBlaze.Abstractions;
using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class RootManagerTests
{
    [Fact]
    public async Task WhenRootAttachmentIsInProgress_ThenLoadingWaitsForRegistryPublication()
    {
        // Arrange
        using var barrier = new AttachBarrier();
        using var fixture = new RootFixture(barrier);
        var manager = fixture.Manager;

        // Act
        await manager.StartAsync(CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // Assert
            Assert.NotNull(manager.Root);
            Assert.Same(manager.Root, fixture.Resolver.ResolveSubject("/", PathStyle.Canonical));
            Assert.Null(fixture.Registry.TryGetRegisteredSubject(manager.Root));
            Assert.False(manager.IsLoaded);
            Assert.False(manager.RootLoaded.IsCompleted);
        }
        finally
        {
            barrier.Release.Set();
            await manager.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(manager.IsLoaded);
        await manager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(fixture.Registry.TryGetRegisteredSubject(manager.Root));
        Assert.Same(manager.Root, fixture.Context.GetService<TestSubject>());
    }

    [Fact]
    public async Task WhenRootAttachmentFails_ThenRootIsNotReportedAsLoaded()
    {
        // Arrange
        var failure = new InvalidOperationException("Attach failed");
        using var barrier = new AttachBarrier(failure);
        using var fixture = new RootFixture(barrier);

        // Act
        await fixture.Manager.StartAsync(CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        barrier.Release.Set();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert
        Assert.Same(failure, exception);
        Assert.NotNull(fixture.Manager.Root);
        Assert.False(fixture.Manager.IsLoaded);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task WhenRootStartupIsAlreadyCanceled_ThenCompletionReportsCancellation()
    {
        // Arrange
        using var barrier = new AttachBarrier();
        using var fixture = new RootFixture(barrier);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await fixture.Manager.StartAsync(cancellation.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Manager.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Manager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(fixture.Manager.RootLoaded.IsCanceled);
        Assert.False(fixture.Manager.IsLoaded);
        Assert.Null(fixture.Manager.Root);
    }

    [Theory]
    [InlineData(SubjectAttachmentAnchorKind.Provisional, false)]
    [InlineData(SubjectAttachmentAnchorKind.Explicit, false)]
    [InlineData(SubjectAttachmentAnchorKind.Provisional, true)]
    [InlineData(SubjectAttachmentAnchorKind.Explicit, true)]
    public async Task WhenRootConstructorAttachesToAContext_ThenLoadingPreservesOnlyTheTargetContext(
        SubjectAttachmentAnchorKind anchor, bool foreignContext)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var constructionContext = foreignContext
            ? InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()
            : context;
        var attachments = 0;
        constructionContext.GetService<LifecycleInterceptor>().SubjectAttached += _ => attachments++;
        var types = new TypeProvider();
        types.AddTypes([typeof(AnchoredRootSubject)]);
        using var services = new ServiceCollection()
            .AddSingleton<IInterceptorSubjectContext>(constructionContext)
            .AddSingleton(new RootAttachmentOptions(anchor))
            .BuildServiceProvider();
        var path = Path.Combine(Path.GetTempPath(), $"root-anchor-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"$type":"HomeBlaze.Services.Tests.AnchoredRootSubject"}""");
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(value => value["HomeBlaze:RootConfigFile"]).Returns(path);
        RootManager? manager = null;
        using var rootManager = manager = new RootManager(new SubjectTypeRegistry(types),
            new ConfigurableSubjectSerializer(types, services), context,
            new SubjectPathResolver(() => manager?.Root), configuration.Object);
        try
        {
            // Act
            await rootManager.StartAsync(CancellationToken.None);

            // Assert
            if (foreignContext)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => rootManager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(rootManager.IsLoaded);
            }
            else
            {
                await rootManager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(rootManager.IsLoaded);
                Assert.Equal(SubjectAttachmentAnchorKind.Explicit, rootManager.Root!.Executor.AttachmentAnchor);
            }
            Assert.Same(constructionContext, rootManager.Root!.TryGetContext());
            Assert.Equal(1, attachments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RootFixture : IDisposable
    {
        private readonly string _configurationPath = Path.Combine(Path.GetTempPath(), $"root-readiness-{Guid.NewGuid():N}.json");
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

        public IInterceptorSubjectContext Context { get; }
        public ISubjectRegistry Registry { get; }
        public SubjectPathResolver Resolver { get; }
        public RootManager Manager { get; }

        public RootFixture(AttachBarrier barrier)
        {
            var types = new TypeProvider();
            types.AddTypes([typeof(TestSubject)]);
            Context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
            Context.AddService(barrier);
            Registry = Context.GetService<ISubjectRegistry>();
            RootManager? manager = null;
            Resolver = new SubjectPathResolver(() => manager?.Root);
            File.WriteAllText(_configurationPath, """{"$type":"HomeBlaze.Services.Tests.Serialization.TestSubject"}""");
            var configuration = new Mock<IConfiguration>();
            configuration.Setup(value => value["HomeBlaze:RootConfigFile"]).Returns(_configurationPath);
            Manager = manager = new RootManager(new SubjectTypeRegistry(types),
                new ConfigurableSubjectSerializer(types, _services), Context, Resolver, configuration.Object);
        }

        public void Dispose()
        {
            Manager.Dispose();
            _services.Dispose();
            File.Delete(_configurationPath);
        }
    }

    [RunsBefore(typeof(SubjectRegistry))]
    private sealed class AttachBarrier(Exception? failure = null) : ILifecycleHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Attachment was not released");
            if (failure is not null) throw failure;
        }

        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
        }
    }
}

public record RootAttachmentOptions(SubjectAttachmentAnchorKind Anchor);

public class AnchoredRootSubject : TestSubject
{
    public AnchoredRootSubject(IInterceptorSubjectContext context, RootAttachmentOptions options)
    {
        this.AttachToContext(context, options.Anchor);
    }
}
