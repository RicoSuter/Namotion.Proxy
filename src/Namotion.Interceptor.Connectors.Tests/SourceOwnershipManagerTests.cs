using Moq;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceOwnershipManagerTests
{
    [Fact]
    public void ClaimSource_WhenNotOwned_ReturnsTrue()
    {
        // Arrange
        var (_, manager) = CreateSourceWithManager();
        var property = CreatePropertyReference();

        // Act
        var result = manager.ClaimSource(property);

        // Assert
        Assert.True(result);
        Assert.Contains(property, manager.Properties);
    }

    [Fact]
    public void ClaimSource_WhenOwnedBySameSource_ReturnsTrue()
    {
        // Arrange
        var (_, manager) = CreateSourceWithManager();
        var property = CreatePropertyReference();

        // First claim
        manager.ClaimSource(property);

        // Act - Second claim with same source
        var result = manager.ClaimSource(property);

        // Assert
        Assert.True(result);
        Assert.Single(manager.Properties); // Should not duplicate
    }

    [Fact]
    public void ClaimSource_WhenOwnedByDifferentSource_ReturnsFalse()
    {
        // Arrange
        var lifecycleInterceptor = new LifecycleInterceptor();
        var (_, manager1) = CreateSourceWithManager(lifecycleInterceptor);
        var (_, manager2) = CreateSourceWithManager(lifecycleInterceptor);
        var property = CreatePropertyReference();

        // First source claims property
        manager1.ClaimSource(property);

        // Act - Second source tries to claim same property
        var result = manager2.ClaimSource(property);

        // Assert
        Assert.False(result);
        Assert.DoesNotContain(property, manager2.Properties);
    }

    [Fact]
    public void ReleaseSource_CallsOnReleasingCallback()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var (_, manager) = CreateSourceWithManager(
            onReleasing: p => releasedProperties.Add(p));

        var property = CreatePropertyReference();
        manager.ClaimSource(property);

        // Act
        manager.ReleaseSource(property);

        // Assert
        Assert.Single(releasedProperties);
        Assert.Equal(property, releasedProperties[0]);
        Assert.DoesNotContain(property, manager.Properties);
    }

    [Fact]
    public void ReleaseSource_WhenNotOwned_DoesNothing()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var (_, manager) = CreateSourceWithManager(
            onReleasing: p => releasedProperties.Add(p));

        var property = CreatePropertyReference();

        // Act - Release without claiming first
        manager.ReleaseSource(property);

        // Assert
        Assert.Empty(releasedProperties);
    }

    [Fact]
    public void Dispose_ReleasesAllProperties()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var (_, manager) = CreateSourceWithManager(
            onReleasing: p => releasedProperties.Add(p));

        var property1 = CreatePropertyReference("Prop1");
        var property2 = CreatePropertyReference("Prop2");
        manager.ClaimSource(property1);
        manager.ClaimSource(property2);

        // Act
        manager.Dispose();

        // Assert
        Assert.Equal(2, releasedProperties.Count);
        Assert.Empty(manager.Properties);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_OnlyReleasesOnce()
    {
        // Arrange
        var releaseCount = 0;
        var (_, manager) = CreateSourceWithManager(
            onReleasing: _ => releaseCount++);

        var property = CreatePropertyReference();
        manager.ClaimSource(property);

        // Act
        manager.Dispose();
        manager.Dispose();
        manager.Dispose();

        // Assert
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void SubjectDetaching_ReleasesPropertiesForSubject()
    {
        // Arrange
        var releasedProperties = new List<PropertyReference>();
        var detachedSubjects = new List<IInterceptorSubject>();
        var (lifecycleInterceptor, manager) = CreateSourceWithManager(
            onReleasing: p => releasedProperties.Add(p),
            onSubjectDetaching: s => detachedSubjects.Add(s));

        // Create subjects and properties (subjects need Data for PropertyReference to work)
        var subject1Mock = new Mock<IInterceptorSubject>();
        subject1Mock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        subject1Mock.Setup(s => s.Context).Returns(InterceptorSubjectContext.Create());
        var subject2Mock = new Mock<IInterceptorSubject>();
        subject2Mock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        subject2Mock.Setup(s => s.Context).Returns(InterceptorSubjectContext.Create());
        var property1 = new PropertyReference(subject1Mock.Object, "Prop1");
        var property2 = new PropertyReference(subject2Mock.Object, "Prop2");
        var property3 = new PropertyReference(subject1Mock.Object, "Prop3");

        manager.ClaimSource(property1);
        manager.ClaimSource(property2);
        manager.ClaimSource(property3);

        // Act - Detach subject1
        lifecycleInterceptor.RaiseSubjectDetaching(subject1Mock.Object);

        // Assert
        Assert.Single(detachedSubjects);
        Assert.Equal(subject1Mock.Object, detachedSubjects[0]);
        Assert.Equal(2, releasedProperties.Count); // property1 and property3
        Assert.Contains(property1, releasedProperties);
        Assert.Contains(property3, releasedProperties);
        Assert.Single(manager.Properties); // Only property2 remains
        Assert.Contains(property2, manager.Properties);
    }

    [Fact]
    public void Constructor_WhenLifecycleNotConfigured_ThrowsInvalidOperationException()
    {
        // Arrange
        var subjectMock = new Mock<IInterceptorSubject>();
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<LifecycleInterceptor>()).Returns((LifecycleInterceptor?)null);
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.RootSubject).Returns(subjectMock.Object);
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => new SourceOwnershipManager(sourceMock.Object));
        Assert.Contains("LifecycleInterceptor not configured", ex.Message);
        Assert.Contains("WithLifecycle()", ex.Message);
    }

    [Fact]
    public void SetSource_AfterRelease_AllowsNewClaim()
    {
        // Arrange
        var lifecycleInterceptor = new LifecycleInterceptor();
        var (_, manager1) = CreateSourceWithManager(lifecycleInterceptor);
        var (_, manager2) = CreateSourceWithManager(lifecycleInterceptor);
        var property = CreatePropertyReference();

        // First source claims property
        manager1.ClaimSource(property);

        // First source releases property
        manager1.ReleaseSource(property);

        // Act - Second source claims same property
        var result = manager2.ClaimSource(property);

        // Assert
        Assert.True(result);
        Assert.Contains(property, manager2.Properties);
    }

    private static (LifecycleInterceptor Lifecycle, SourceOwnershipManager Manager) CreateSourceWithManager(
        LifecycleInterceptor? lifecycleInterceptor = null,
        Action<PropertyReference>? onReleasing = null,
        Action<IInterceptorSubject>? onSubjectDetaching = null)
    {
        lifecycleInterceptor ??= new LifecycleInterceptor();

        var subjectMock = new Mock<IInterceptorSubject>();
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<LifecycleInterceptor>()).Returns(lifecycleInterceptor);
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.RootSubject).Returns(subjectMock.Object);
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        var manager = new SourceOwnershipManager(sourceMock.Object, onReleasing, onSubjectDetaching);
        return (lifecycleInterceptor, manager);
    }

    private static PropertyReference CreatePropertyReference(string name = "TestProperty")
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        subjectMock.Setup(s => s.Context).Returns(InterceptorSubjectContext.Create());
        return new PropertyReference(subjectMock.Object, name);
    }
}

// Extension for testing - expose method to raise event
internal static class LifecycleInterceptorExtensions
{
    public static void RaiseSubjectDetaching(this LifecycleInterceptor interceptor, IInterceptorSubject subject)
    {
        // Use reflection to raise the event since it's internal
        var eventField = typeof(LifecycleInterceptor)
            .GetField("SubjectDetaching", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        if (eventField?.GetValue(interceptor) is Action<SubjectLifecycleChange> handler)
        {
            handler.Invoke(new SubjectLifecycleChange { Subject = subject, ReferenceCount = 0 });
        }
    }
}
