using HomeBlaze.Services.Tests.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services.Tests;

public class RootManagerRootLoadedTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _configurationFiles = [];

    [Fact]
    public async Task WhenWaiterSubscribesBeforeTheRootIsLoaded_ThenItReceivesTheRoot()
    {
        // Arrange
        var rootManager = CreateRootManager(CreateConfigurationFile());
        var waitTask = rootManager.RootLoaded;

        // Act
        await rootManager.StartAsync(CancellationToken.None);
        var root = await waitTask.WaitAsync(WaitTimeout);

        // Assert
        Assert.Same(rootManager.Root, root);
    }

    [Fact]
    public async Task WhenWaiterSubscribesAfterTheRootIsLoaded_ThenTheWaitIsAlreadyCompleted()
    {
        // Arrange
        var rootManager = CreateRootManager(CreateConfigurationFile());
        await rootManager.StartAsync(CancellationToken.None);
        await rootManager.ExecuteTask!.WaitAsync(WaitTimeout);

        // Act
        var waitTask = rootManager.RootLoaded;

        // Assert
        Assert.True(waitTask.IsCompletedSuccessfully);
        Assert.Same(rootManager.Root, await waitTask);
    }

    [Fact]
    public async Task WhenTheRootFailsToLoad_ThenTheWaitFaultsWithTheLoadException()
    {
        // Arrange
        var missingFile = Path.Combine(Path.GetTempPath(), $"homeblaze-missing-root-{Guid.NewGuid():N}.json");
        var rootManager = CreateRootManager(missingFile);
        var waitTask = rootManager.RootLoaded;

        // Act
        await rootManager.StartAsync(CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => waitTask.WaitAsync(WaitTimeout));
        await Assert.ThrowsAsync<FileNotFoundException>(() => rootManager.ExecuteTask!.WaitAsync(WaitTimeout));
        Assert.False(rootManager.IsLoaded);
    }

    private RootManager CreateRootManager(string configurationFilePath)
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(TestSubject)]);

        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var configuration = new Mock<IConfiguration>();
        configuration
            .Setup(instance => instance["HomeBlaze:RootConfigFile"])
            .Returns(configurationFilePath);

        RootManager? rootManager = null;
        var pathResolver = new SubjectPathResolver(() => rootManager!.Root);
        rootManager = new RootManager(typeRegistry, serializer, context, pathResolver, configuration.Object);

        _disposables.Add(serviceProvider);
        _disposables.Add(rootManager);
        return rootManager;
    }

    private string CreateConfigurationFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeblaze-root-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            {
              "$type": "{{typeof(TestSubject).FullName}}",
              "configProperty": "loaded"
            }
            """);

        _configurationFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var configurationFile in _configurationFiles)
        {
            File.Delete(configurationFile);
        }
    }
}
