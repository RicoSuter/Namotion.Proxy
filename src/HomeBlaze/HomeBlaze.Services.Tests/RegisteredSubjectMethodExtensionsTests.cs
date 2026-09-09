using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests;

public class RegisteredSubjectMethodExtensionsTests
{
    [Fact]
    public void WhenUnrelatedGettersThrow_ThenMethodDiscoveryStillFindsAllSupportedMetadataShapes()
    {
        // Arrange
        var subject = new MethodDiscoveryGetterSubject(CreateContext());
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var all = registered.GetAllMethods();
        var operations = registered.GetOperationMethods();
        var queries = registered.GetQueryMethods();

        // Assert
        Assert.Equal(new[] { "Object", "Interface", "Subclass" }, all.Select(method => method.Title));
        Assert.Equal(new[] { "Object", "Subclass" }, operations.Select(method => method.Title));
        Assert.Equal("Interface", Assert.Single(queries).Title);
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
    }

    [Fact]
    public void GetAllMethods_ReturnsAllMethodMetadata_OrderedByPosition()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var methods = registered.GetAllMethods();

        // Assert
        Assert.Equal(3, methods.Count);
        Assert.Equal("First", methods[0].Title);
        Assert.Equal("Second", methods[1].Title);
        Assert.Equal("Third", methods[2].Title);
    }

    [Fact]
    public void GetOperationMethods_ReturnsOnlyOperations()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var operations = registered.GetOperationMethods();

        // Assert
        Assert.Equal(2, operations.Count);
        Assert.All(operations, m => Assert.Equal(MethodKind.Operation, m.Kind));
    }

    [Fact]
    public void GetQueryMethods_ReturnsOnlyQueries()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var queries = registered.GetQueryMethods();

        // Assert
        Assert.Single(queries);
        Assert.Equal(MethodKind.Query, queries[0].Kind);
    }
    [Fact]
    public void GetAllMethods_IncludesDynamicallyAddedMethodMetadata()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        var dynamicMetadata = new MethodMetadata(_ => Task.CompletedTask)
        {
            Title = "Dynamic",
            Kind = MethodKind.Operation,
            PropertyName = "DynamicOp",
            Position = 0,
        };
        registered.AddProperty("DynamicOp", typeof(MethodMetadata), _ => dynamicMetadata, null);

        // Act
        var methods = registered.GetAllMethods();

        // Assert — dynamic method should appear first (Position = 0) alongside the 3 attribute-based methods
        Assert.Equal(4, methods.Count);
        Assert.Equal("Dynamic", methods[0].Title);
    }

    [Fact]
    public void GetOperationMethods_IncludesDynamicallyAddedOperationMetadata()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodExtTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        var dynamicMetadata = new MethodMetadata(_ => Task.CompletedTask)
        {
            Title = "DynamicOp",
            Kind = MethodKind.Operation,
            PropertyName = "DynamicOp",
        };
        registered.AddProperty("DynamicOp", typeof(MethodMetadata), _ => dynamicMetadata, null);

        // Act
        var operations = registered.GetOperationMethods();

        // Assert — should include both attribute-based operations and the dynamic one
        Assert.Equal(3, operations.Count);
        Assert.All(operations, m => Assert.Equal(MethodKind.Operation, m.Kind));
    }
}

[InterceptorSubject]
public partial class MethodExtTestSubject
{
    [Operation(Title = "First", Position = 1)]
    public Task FirstAsync() => Task.CompletedTask;

    [Operation(Title = "Second", Position = 2)]
    public Task SecondAsync() => Task.CompletedTask;

    [Query(Title = "Third", Position = 3)]
    public Task<string> ThirdAsync() => Task.FromResult("result");
}

public interface IMethodDiscoveryMarker;

public sealed class MethodDiscoveryMetadata : MethodMetadata, IMethodDiscoveryMarker
{
    public MethodDiscoveryMetadata() : base(_ => Task.CompletedTask) { }
}

[InterceptorSubject]
public partial class MethodDiscoveryGetterSubject
{
    public int UnavailableValue => throw new InvalidOperationException("Value is not ready");

    public object ObjectMethod { get; } = new MethodMetadata(_ => Task.CompletedTask)
    {
        Title = "Object", Position = 0, Kind = MethodKind.Operation
    };

    public IMethodDiscoveryMarker InterfaceMethod { get; } = new MethodDiscoveryMetadata
    {
        Title = "Interface", Position = 1, Kind = MethodKind.Query
    };

    public MethodDiscoveryMetadata SubclassMethod { get; } = new()
    {
        Title = "Subclass", Position = 2, Kind = MethodKind.Operation
    };
}
