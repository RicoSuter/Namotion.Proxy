using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task FullScenario_QueryWithNestedObjects_ReturnsCorrectData()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context)
        {
            Temperature = 20.0m,
            Humidity = 50.0m,
            Location = new Location(context) { Building = "A", Room = "101" }
        };

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>("sensor")
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync(@"
            query {
                sensor {
                    temperature
                    humidity
                    location { building room }
                    status
                }
            }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("20", json);
        Assert.Contains("50", json);
        Assert.Contains("\"A\"", json);
        Assert.Contains("\"101\"", json);
        Assert.Contains("Normal", json);
    }

    [Fact]
    public async Task FullScenario_QueryWithDerivedProperty_ReturnsComputedValue()
    {
        // Arrange - temperature > 30 should make status "Hot"
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context) { Temperature = 35.0m };

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>()
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ root { status } }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("Hot", json);
    }

    [Fact]
    public async Task FullScenario_CustomSubjectSelector_WorksCorrectly()
    {
        // Arrange
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddSubjectGraphQL(
                _ =>
                {
                    var context = InterceptorSubjectContext.Create()
                        .WithFullPropertyTracking()
                        .WithRegistry();
                    var sensor = new Sensor(context) { Temperature = 99.9m };
                    return sensor;
                },
                "customSensor")
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ customSensor { temperature } }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("99.9", json);
    }

    [Fact]
    public async Task FullScenario_FullConfiguration_WorksCorrectly()
    {
        // Arrange
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddSubjectGraphQL(
                _ =>
                {
                    var context = InterceptorSubjectContext.Create()
                        .WithFullPropertyTracking()
                        .WithRegistry();
                    return new Sensor(context) { Temperature = 42.0m };
                },
                _ => new GraphQLSubjectConfiguration
                {
                    RootName = "mySensor",
                    BufferTime = TimeSpan.FromMilliseconds(100)
                })
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ mySensor { temperature status } }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("42", json);
        Assert.Contains("Hot", json);
    }

    [Fact]
    public async Task FullScenario_ConfigurationProviderUsingServiceProvider_WorksCorrectly()
    {
        // Arrange - config factory that depends on IServiceProvider
        var services = new ServiceCollection();
        services.AddSingleton(new GraphQLSubjectConfiguration
        {
            RootName = "fromDI",
            BufferTime = TimeSpan.FromMilliseconds(200)
        });

        var executor = await services
            .AddGraphQLServer()
            .AddSubjectGraphQL(
                _ =>
                {
                    var context = InterceptorSubjectContext.Create()
                        .WithFullPropertyTracking()
                        .WithRegistry();
                    return new Sensor(context) { Temperature = 77.7m };
                },
                sp => sp.GetRequiredService<GraphQLSubjectConfiguration>()) // Uses IServiceProvider!
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ fromDI { temperature } }");

        // Assert
        var operationResult = (IOperationResult)result;
        Assert.Null(operationResult.Errors);
        var json = JsonSerializer.Serialize(operationResult.Data);
        Assert.Contains("77.7", json);
    }

}
