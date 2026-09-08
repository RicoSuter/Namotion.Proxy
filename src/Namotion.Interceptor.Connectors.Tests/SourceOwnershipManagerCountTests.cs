using Moq;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceOwnershipManagerCountTests
{
    [Fact]
    public void WhenPropertiesAreClaimed_ThenCountRises()
    {
        // Arrange
        var (_, manager) = CreateSourceWithManager();

        // Act
        manager.ClaimSource(CreatePropertyReference("FirstName"));
        manager.ClaimSource(CreatePropertyReference("LastName"));

        // Assert
        Assert.Equal(2, manager.Count);
    }

    [Fact]
    public void WhenAPropertyIsReleased_ThenCountFalls()
    {
        // Arrange
        var (_, manager) = CreateSourceWithManager();
        var property = CreatePropertyReference("FirstName");
        manager.ClaimSource(property);

        // Act
        manager.ReleaseSource(property);

        // Assert
        Assert.Equal(0, manager.Count);
    }

    [Fact]
    public void WhenASubjectDetaches_ThenCountFallsByItsProperties()
    {
        // Arrange
        var (lifecycle, manager) = CreateSourceWithManager();
        var detachingSubject = CreateSubject();
        var remainingSubject = CreateSubject();

        manager.ClaimSource(new PropertyReference(detachingSubject, "FirstName"));
        manager.ClaimSource(new PropertyReference(detachingSubject, "LastName"));
        manager.ClaimSource(new PropertyReference(remainingSubject, "FirstName"));
        Assert.Equal(3, manager.Count);

        // Act
        lifecycle.RaiseSubjectDetaching(detachingSubject);

        // Assert
        Assert.Equal(1, manager.Count);
    }

    [Fact]
    public void WhenDisposed_ThenCountIsZero()
    {
        // Arrange
        var (_, manager) = CreateSourceWithManager();
        manager.ClaimSource(CreatePropertyReference("FirstName"));

        // Act
        manager.Dispose();

        // Assert
        Assert.Equal(0, manager.Count);
    }

    private static (LifecycleInterceptor Lifecycle, SourceOwnershipManager Manager) CreateSourceWithManager()
    {
        var lifecycleInterceptor = new LifecycleInterceptor();

        var subjectMock = new Mock<IInterceptorSubject>();
        var contextMock = new Mock<IInterceptorSubjectContext>();
        contextMock.Setup(c => c.TryGetService<LifecycleInterceptor>()).Returns(lifecycleInterceptor);
        subjectMock.Setup(s => s.Context).Returns(contextMock.Object);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.RootSubject).Returns(subjectMock.Object);
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        return (lifecycleInterceptor, new SourceOwnershipManager(sourceMock.Object));
    }

    private static IInterceptorSubject CreateSubject()
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        subjectMock.Setup(s => s.Data).Returns(new System.Collections.Concurrent.ConcurrentDictionary<(string?, string), object?>());
        subjectMock.Setup(s => s.Context).Returns(InterceptorSubjectContext.Create());
        return subjectMock.Object;
    }

    private static PropertyReference CreatePropertyReference(string name) => new(CreateSubject(), name);
}
