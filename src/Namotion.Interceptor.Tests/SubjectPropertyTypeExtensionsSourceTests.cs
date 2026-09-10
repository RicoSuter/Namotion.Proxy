using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Tests;

/// <summary>
/// The classifier moved into the core assembly because the runtime write routing needs it, but its
/// namespace stays where consumers import it from. This file lives in a project that references the
/// core assembly only, so it compiles only while the type keeps resolving through that namespace.
/// </summary>
public class SubjectPropertyTypeExtensionsSourceTests
{
    [Fact]
    public void WhenImportedFromTheTrackingNamespace_ThenTheTypeClassifiersStillResolve()
    {
        // Arrange
        var scalarType = typeof(int);

        // Act
        var canContainSubjects = scalarType.CanContainSubjects();

        // Assert
        Assert.False(canContainSubjects);
        Assert.Equal("Namotion.Interceptor.Tracking", typeof(SubjectPropertyTypeExtensions).Namespace);
    }
}
