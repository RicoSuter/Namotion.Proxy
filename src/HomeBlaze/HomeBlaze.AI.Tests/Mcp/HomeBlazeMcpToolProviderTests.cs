using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.AI.Tests.Mcp;

public class HomeBlazeMcpToolProviderTests
{
    [Fact]
    public async Task WhenListMethods_ThenReturnsMethodMetadata()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);

        // Add a method property to the room
        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("TurnOn", _ => new MethodMetadata(_ => "done")
        {
            Kind = MethodKind.Operation,
            Title = "Turn On",
            PropertyName = "TurnOn",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "list_methods");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("methods", out var methods));
        var methodArray = methods.EnumerateArray().ToArray();
        Assert.Contains(methodArray, m => m.GetProperty("name").GetString() == "TurnOn");
    }

    [Fact]
    public async Task WhenInvokeQueryMethodInReadOnlyMode_ThenAllowed()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: true);

        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("GetStatus", _ => new MethodMetadata(_ => "OK")
        {
            Kind = MethodKind.Query,
            Title = "Get Status",
            PropertyName = "GetStatus",
            ResultType = typeof(string),
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "GetStatus" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task WhenInvokeOperationMethodInReadOnlyMode_ThenBlocked()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: true);

        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("TurnOn", _ => new MethodMetadata(_ => "done")
        {
            Kind = MethodKind.Operation,
            Title = "Turn On",
            PropertyName = "TurnOn",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "TurnOn" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("read-only", error.GetString());
    }

    [Fact]
    public async Task WhenInvokeNonExistentMethod_ThenReturnsError()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);
        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "DoesNotExist" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public async Task WhenInvokeOnInvalidPath_ThenReturnsError()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);
        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "NonExistent", method = "DoSomething" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenInvokeMethodWithParameters_ThenParametersArePassedCorrectly()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);

        string? capturedInput = null;
        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("SetName", _ => new MethodMetadata(args =>
        {
            capturedInput = args?[0] as string;
            return null;
        })
        {
            Kind = MethodKind.Operation,
            Title = "Set Name",
            PropertyName = "SetName",
            Parameters =
            [
                new MethodParameter { Name = "name", Type = typeof(string) }
            ]
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new
        {
            path = "",
            method = "SetName",
            parameters = new { name = "NewName" }
        });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal("NewName", capturedInput);
    }

    [Fact]
    public async Task WhenMethodThrows_ThenReturnsGenericError()
    {
        // Arrange
        var (room, config, factory) = CreateTestSetup(isReadOnly: false);

        var registered = room.TryGetRegisteredSubject()!;
        registered.AddProperty<MethodMetadata>("FailMethod", _ => new MethodMetadata(_ =>
            throw new InvalidOperationException("Internal failure"))
        {
            Kind = MethodKind.Operation,
            Title = "Fail",
            PropertyName = "FailMethod",
            Parameters = []
        });

        var tool = factory.CreateTools().First(t => t.Name == "invoke_method");

        // Act
        var input = JsonSerializer.SerializeToElement(new { path = "", method = "FailMethod" });
        var result = await tool.Handler(input, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(result);

        // Assert — should return generic error, not expose internal details
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("failed", error.GetString());
    }

    internal static (TestThing room, McpServerConfiguration config, McpToolFactory factory) CreateTestSetup(bool isReadOnly)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var room = new TestThing(context) { Name = "Test Room", Temperature = 21.5m };

        var pathProvider = new StateAttributePathProvider();
        var toolProvider = new HomeBlazeMcpToolProvider(
            () => room, pathProvider,
            new EmptyServiceProvider(),
            NullLogger<HomeBlazeMcpToolProvider>.Instance,
            isReadOnly);

        var config = new McpServerConfiguration
        {
            PathProvider = pathProvider,
            IsReadOnly = isReadOnly,
            ToolProviders = { toolProvider }
        };
        var factory = new McpToolFactory(room, config);
        return (room, config, factory);
    }

    private class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
