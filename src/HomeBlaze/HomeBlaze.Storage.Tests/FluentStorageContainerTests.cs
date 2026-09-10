using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests;

public class FluentStorageContainerTests
{
    private static (TypeProvider typeProvider, SubjectTypeRegistry typeRegistry, ConfigurableSubjectSerializer serializer, IServiceProvider serviceProvider, RootManager rootManager) CreateDependencies()
    {
        var typeProvider = new TypeProvider();
        // Register HomeBlaze.Samples assembly for Motor and other configurable subjects
        typeProvider.AddAssembly(typeof(Samples.Motor).Assembly);
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var context = InterceptorSubjectContext.Create();

        var services = new ServiceCollection();
        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton<IInterceptorSubjectContext>(context);
        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<MarkdownContentParser>();

        var serviceProvider = services.BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<ConfigurableSubjectSerializer>();
        var rootManager = serviceProvider.GetRequiredService<RootManager>();

        return (typeProvider, typeRegistry, serializer, serviceProvider, rootManager);
    }

    [Fact]
    public void Constructor_InitializesProperties()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();

        // Act
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Assert
        Assert.Equal("disk", storage.StorageType);
        Assert.Equal(string.Empty, storage.ConnectionString);
        Assert.NotNull(storage.Children);
        Assert.Empty(storage.Children);
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Title_ReturnsConnectionStringFileName_WhenNotEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act
        storage.ConnectionString = @"Foo/Bar/Storage";

        // Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public void Title_ReturnsStorage_WhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act & Assert
        Assert.Equal("Storage", storage.Title);
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenConnectionStringEmpty()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ThrowsForUnsupportedStorageType()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        storage.ConnectionString = "test-connection";
        storage.StorageType = "unsupported-type";

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => storage.ConnectAsync(CancellationToken.None));
        Assert.Contains("unsupported-type", ex.Message);
    }

    [Fact]
    public void Status_DefaultsToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Assert - Initial state
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public void Icon_ReturnsStorageIcon()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act
        var icon = storage.IconName;

        // Assert
        Assert.Equal("Storage", icon);
    }

    [Fact]
    public void Dispose_SetsStatusToDisconnected()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        // Act
        storage.Dispose();

        // Assert
        Assert.Equal(StorageStatus.Disconnected, storage.Status);
    }

    [Fact]
    public async Task AddAndDeleteSubject_UpdatesChildrenHierarchy()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        storage.StorageType = "inmemory";
        await storage.ConnectAsync(CancellationToken.None);

        // Create a test subject (using Motor from HomeBlaze.Samples)
        var context = serviceProvider.GetRequiredService<IInterceptorSubjectContext>();
        var motor = new Samples.Motor(context) { Name = "Test Motor" };

        // Act 1: Add subject
        await storage.AddSubjectAsync("testmotor.json", motor, CancellationToken.None);

        // Assert 1: Subject should be in Children
        Assert.Single(storage.Children);
        Assert.True(storage.Children.ContainsKey("testmotor"));
        Assert.Equal(motor, storage.Children["testmotor"]);

        // Act 2: Delete subject
        await storage.DeleteSubjectAsync(motor, CancellationToken.None);

        // Assert 2: Subject should be removed from Children
        Assert.Empty(storage.Children);
        Assert.False(storage.Children.ContainsKey("testmotor"));

        // Cleanup
        storage.Dispose();
    }

    [Fact]
    public async Task AddAndDeleteSubject_InNestedFolder_UpdatesChildrenHierarchy()
    {
        // Arrange
        var (_, typeRegistry, serializer, serviceProvider, _) = CreateDependencies();
        var storage = new FluentStorageContainer(typeRegistry, serializer, serviceProvider);

        storage.StorageType = "inmemory";
        await storage.ConnectAsync(CancellationToken.None);

        // Create a test subject
        var context = serviceProvider.GetRequiredService<IInterceptorSubjectContext>();
        var motor = new Samples.Motor(context) { Name = "Nested Motor" };

        // Act 1: Add subject in nested folder
        await storage.AddSubjectAsync("demo/motors/motor1.json", motor, CancellationToken.None);

        // Assert 1: Folder hierarchy should be created
        Assert.Single(storage.Children);
        Assert.True(storage.Children.ContainsKey("demo"));

        var demoFolder = storage.Children["demo"] as VirtualFolder;
        Assert.NotNull(demoFolder);
        Assert.Single(demoFolder.Children);
        Assert.True(demoFolder.Children.ContainsKey("motors"));

        var motorsFolder = demoFolder.Children["motors"] as VirtualFolder;
        Assert.NotNull(motorsFolder);
        Assert.Single(motorsFolder.Children);
        Assert.True(motorsFolder.Children.ContainsKey("motor1"));
        Assert.Equal(motor, motorsFolder.Children["motor1"]);

        // Act 2: Delete subject
        await storage.DeleteSubjectAsync(motor, CancellationToken.None);

        // Assert 2: Subject should be removed from nested folder
        // Note: Folders remain (they're not auto-cleaned up)
        var demoFolderAfterDelete = storage.Children["demo"] as VirtualFolder;
        var motorsFolderAfterDelete = demoFolderAfterDelete?.Children["motors"] as VirtualFolder;
        Assert.NotNull(motorsFolderAfterDelete);
        Assert.Empty(motorsFolderAfterDelete.Children);
        Assert.False(motorsFolderAfterDelete.Children.ContainsKey("motor1"));

        // Cleanup
        storage.Dispose();
    }
}