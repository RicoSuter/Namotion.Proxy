using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class GraphQLSubjectConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasExpectedDefaults()
    {
        // Act
        var config = new GraphQLSubjectConfiguration();

        // Assert
        Assert.Equal("root", config.RootName);
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.BufferTime);
        Assert.IsType<CamelCasePathProvider>(config.PathProvider);
    }

    [Fact]
    public void Validate_WhenRootNameIsEmpty_Throws()
    {
        // Act & Assert
        var config = new GraphQLSubjectConfiguration { RootName = "" };
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_WhenRootNameIsWhitespace_Throws()
    {
        // Act & Assert
        var config = new GraphQLSubjectConfiguration { RootName = "   " };
        Assert.Throws<ArgumentException>(() => config.Validate());
    }
}
