using Microsoft.Extensions.Logging;
using Moq;
using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Tests.Models;
using Namotion.Interceptor.Registry;
using TwinCAT.TypeSystem;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

public class AdsSubscriptionManagerTests
{
    private static AdsSubscriptionManager CreateManager()
    {
        return new AdsSubscriptionManager(TestHelpers.CreateConfiguration(), new Mock<ILogger>().Object);
    }

    [Fact]
    public void Constructor_WithNullConfiguration_ShouldThrow()
    {
        // Arrange
        var logger = new Mock<ILogger>().Object;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsSubscriptionManager(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        // Arrange
        var configuration = TestHelpers.CreateConfiguration();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new AdsSubscriptionManager(configuration, null!));
    }

    [Fact]
    public void InitialState_AllPropertiesHaveDefaultValues()
    {
        // Arrange & Act
        var manager = CreateManager();

        // Assert
        Assert.NotNull(manager);
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
        Assert.False(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void GetSymbolPath_UnknownProperty_ReturnsNull()
    {
        // Arrange
        var manager = CreateManager();
        var context = TestHelpers.CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        var result = manager.GetSymbolPath(property.Reference);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetSymbolPath_MultipleUnknownProperties_AllReturnNull()
    {
        // Arrange
        var manager = CreateManager();
        var context = TestHelpers.CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var property in registeredSubject.Properties)
        {
            Assert.Null(manager.GetSymbolPath(property.Reference));
        }
    }

    [Fact]
    public void TryGetSymbol_NoSymbolLoader_ReturnsNull()
    {
        // Arrange & Act
        var result = AdsSubscriptionManager.TryGetSymbol(null, "GVL.SomeSymbol");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_SymbolsPropertyThrows_ReturnsNull()
    {
        // Arrange
        var mockLoader = new Mock<ISymbolLoader>();
        mockLoader.Setup(l => l.Symbols).Throws(new InvalidOperationException("Not loaded"));

        // Act
        var result = AdsSubscriptionManager.TryGetSymbol(mockLoader.Object, "GVL.Broken");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetSymbol_SymbolsPropertyReturnsNull_ReturnsNull()
    {
        // Arrange
        var mockLoader = new Mock<ISymbolLoader>();
        mockLoader.Setup(l => l.Symbols).Returns((SymbolCollection)null!);

        // Act
        var result = AdsSubscriptionManager.TryGetSymbol(mockLoader.Object, "GVL.SomeSymbol");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ClearAll_ResetsAllCaches()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.ClearAll();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_SetsPollingDirtyFlag()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.ClearAll();

        // Assert
        Assert.True(manager.IsPollingCollectionDirty);
    }

    [Fact]
    public void ClearAll_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        manager.ClearAll();
        manager.ClearAll();
        manager.ClearAll();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public void ClearAll_AfterClear_GetSymbolPathReturnsNull()
    {
        // Arrange
        var manager = CreateManager();
        var context = TestHelpers.CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act
        manager.ClearAll();

        // Assert
        Assert.Null(manager.GetSymbolPath(property.Reference));
    }

    [Fact]
    public void OnPropertyReleasing_UnknownProperty_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = TestHelpers.CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act & Assert
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnPropertyReleasing_CalledTwiceForSameProperty_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = TestHelpers.CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.First();

        // Act & Assert
        manager.OnPropertyReleasing(property.Reference);
        manager.OnPropertyReleasing(property.Reference);
    }

    [Fact]
    public void OnPropertyReleasing_MultipleProperties_DoesNotThrow()
    {
        // Arrange
        var manager = CreateManager();
        var context = TestHelpers.CreateContext();
        var model = new TestPlcModel(context);
        var registeredSubject = model.TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var property in registeredSubject.Properties)
        {
            manager.OnPropertyReleasing(property.Reference);
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldComplete()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        // Arrange
        var manager = CreateManager();

        // Act & Assert
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_PropertiesStillAccessible()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        await manager.DisposeAsync();

        // Assert
        Assert.Equal(0, manager.NotificationCount);
        Assert.Equal(0, manager.PolledCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterClearAll_ShouldComplete()
    {
        // Arrange
        var manager = CreateManager();
        manager.ClearAll();

        // Act & Assert
        await manager.DisposeAsync();
    }
}
