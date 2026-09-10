using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class QueryTests
{
    [Fact]
    public async Task Query_WithCustomRootName_ReturnsSubject()
    {
        // Arrange
        var sensor = CreateSensor();
        sensor.Temperature = 25.5m;

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>("sensor")
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ sensor { temperature } }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        Assert.NotNull(operationResult.Data);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("25.5", json);
    }

    [Fact]
    public async Task Query_WithDefaultRootName_ReturnsSubject()
    {
        // Arrange
        var sensor = CreateSensor();
        sensor.Temperature = 30.0m;

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>()
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ root { temperature } }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        Assert.NotNull(operationResult.Data);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("30", json);
    }

    private static Sensor CreateSensor()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        return new Sensor(context);
    }
}
