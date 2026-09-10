using Namotion.Interceptor.Tracking.Paths;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

public class PathValueTests
{
    [Fact]
    public void WhenUnresolved_ThenTryGetValueIsFalseAndDefaultsReturned()
    {
        // Arrange
        var value = SubjectPathValue<string>.Unresolved;

        // Act
        var got = value.TryGetValue(out var inner);

        // Assert
        Assert.False(value.IsResolved);
        Assert.False(got);
        Assert.Null(inner);
        Assert.Null(value.GetValueOrDefault());
        Assert.Equal("fb", value.GetValueOrDefault("fb"));
    }

    [Fact]
    public void WhenResolvedNull_ThenTryGetValueIsTrueWithNull()
    {
        // Arrange
        var value = SubjectPathValue<string?>.Resolved(null);

        // Act
        var got = value.TryGetValue(out var inner);

        // Assert
        Assert.True(value.IsResolved);
        Assert.True(got);
        Assert.Null(inner);
        Assert.Null(value.GetValueOrDefault());      // resolved but null
        Assert.Equal("fb", value.GetValueOrDefault("fb")); // fallback on resolved null
    }

    [Fact]
    public void WhenResolvedNonNull_ThenValueReturnedAndFallbackIgnored()
    {
        // Arrange
        var value = SubjectPathValue<int>.Resolved(7);

        // Act & Assert
        Assert.True(value.TryGetValue(out var inner));
        Assert.Equal(7, inner);
        Assert.Equal(7, value.GetValueOrDefault());
        Assert.Equal(7, value.GetValueOrDefault(99));
    }

    [Fact]
    public void WhenComparedForSuppression_ThenResolvednessAndValueDecideEquivalence()
    {
        // Arrange & Act & Assert
        Assert.True(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Unresolved, SubjectPathValue<int>.Unresolved));
        Assert.False(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Unresolved, SubjectPathValue<int>.Resolved(0)));
        Assert.True(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Resolved(5), SubjectPathValue<int>.Resolved(5)));
        Assert.False(SubjectPathValue<int>.AreEquivalent(
            SubjectPathValue<int>.Resolved(5), SubjectPathValue<int>.Resolved(6)));
    }
}
