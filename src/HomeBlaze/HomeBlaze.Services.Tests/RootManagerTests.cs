using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class RootManagerTests
{
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
            }
            else
            {
                await rootManager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10));
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
}

public record RootAttachmentOptions(SubjectAttachmentAnchorKind Anchor);

public class AnchoredRootSubject : TestSubject
{
    public AnchoredRootSubject(IInterceptorSubjectContext context, RootAttachmentOptions options)
    {
        this.AttachToContext(context, options.Anchor);
    }
}
