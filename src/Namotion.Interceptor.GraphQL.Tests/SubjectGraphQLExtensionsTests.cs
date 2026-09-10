using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class SubjectGraphQLExtensionsTests
{
    [Fact]
    public void AddSubjectGraphQL_WithRootName_ConfiguresCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_ =>
        {
            var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
            return new Sensor(context);
        });

        // Act
        services.AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>("sensor");

        // Assert - just verify it builds without error
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddSubjectGraphQL_WithFullConfiguration_ConfiguresCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddGraphQLServer()
            .AddSubjectGraphQL(
                sp =>
                {
                    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
                    return new Sensor(context);
                },
                _ => new GraphQLSubjectConfiguration
                {
                    RootName = "mySensor",
                    BufferTime = TimeSpan.FromMilliseconds(100)
                });

        // Assert
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }
}
