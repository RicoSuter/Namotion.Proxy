using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Tests.Lifecycle;

public class MethodPropertyInitializerTests
{
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
    public void OperationMethod_CreatesMethodMetadataProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
        Assert.Equal("Stop", metadata.Title);
        Assert.True(metadata.RequiresConfirmation);
    }

    [Fact]
    public void QueryMethod_CreatesMethodMetadataProperty()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("GetDiagnostics");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Query, metadata.Kind);
        Assert.Equal("Diagnostics", metadata.Title);
        Assert.Equal("diag-icon", metadata.Icon);
        Assert.Equal(2, metadata.Position);
    }

    [Fact]
    public void OperationMethod_HasCorrectParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("SetTarget");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        var userParameters = metadata.Parameters.Where(p => !p.IsFromServices).ToArray();
        Assert.Single(userParameters);
        Assert.Equal("temperature", userParameters[0].Name);
        Assert.Equal(typeof(decimal), userParameters[0].Type);
    }

    [Fact]
    public async Task OperationMethod_InvokeAsync_CallsUnderlyingMethod()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task AsyncMethod_InvokeAsync_ReturnsResult()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("GetDiagnostics");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Equal("diagnostics-result", result);
        Assert.Equal(typeof(string), metadata.ResultType);
    }

    [Fact]
    public void MethodWithCustomTitle_UsesAttributeTitle()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Stop", metadata!.Title);
    }

    [Fact]
    public void MethodWithoutCustomTitle_UsesMethodNameWithoutAsync()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("SetTarget");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("SetTarget", metadata!.Title);
    }

    [Fact]
    public void MethodFromInterface_IsDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("InterfaceOperation");

        // Assert
        Assert.NotNull(property);
        var metadata = property.GetValue() as MethodMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(MethodKind.Operation, metadata.Kind);
    }

    [Fact]
    public void CancellationTokenParameter_IsRuntimeProvided()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var ctParam = metadata!.Parameters.Single(p => p.Name == "cancellationToken");
        Assert.True(ctParam.IsRuntimeProvided);
        Assert.False(ctParam.RequiresInput);
    }

    [Fact]
    public void FromServicesParameter_IsFromServicesNotAutoInjected()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsRuntimeProvided);
        Assert.False(svcParam.RequiresInput);
    }

    [Fact]
    public void NullableFromServicesParameter_IsNullable()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert — ITestLogger? is nullable
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsNullable);
    }

    [Fact]
    public void NonNullableFromServicesParameter_IsNotNullable()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithRequiredServiceSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert — ITestLogger (non-nullable) is not nullable
        var svcParam = metadata!.Parameters.Single(p => p.Name == "logger");
        Assert.True(svcParam.IsFromServices);
        Assert.False(svcParam.IsNullable);
    }

    [Fact]
    public void RegularParameter_RequiresInput()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        var valueParam = metadata!.Parameters.Single(p => p.Name == "value");
        Assert.False(valueParam.IsRuntimeProvided);
        Assert.False(valueParam.IsFromServices);
        Assert.True(valueParam.RequiresInput);
    }

    [Fact]
    public async Task MethodWithCancellationToken_InvokeAsync_PassesTokenThrough()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;
        var cts = new CancellationTokenSource();

        // Act — only pass user parameters; CancellationToken is injected via the argument
        await metadata!.InvokeAsync([42], null, cts.Token);

        // Assert
        Assert.Equal(42, subject.LastValue);
        Assert.Equal(cts.Token, subject.LastToken);
    }

    [Fact]
    public void ParameterlessMethod_HasEmptyParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Empty(metadata!.Parameters);
    }

    [Fact]
    public async Task ParameterlessMethod_InvokeAsync_WithNullParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public async Task ParameterlessMethod_InvokeAsync_WithEmptyParameters()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        await metadata!.InvokeAsync([], null, CancellationToken.None);

        // Assert
        Assert.True(subject.StopCalled);
    }

    [Fact]
    public void VoidMethod_HasNullResultType()
    {
        // Arrange
        var context = CreateContext();
        var subject = new VoidMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Reset");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.ResultType);
    }

    [Fact]
    public async Task VoidMethod_InvokeAsync_ReturnsNull()
    {
        // Arrange
        var context = CreateContext();
        var subject = new VoidMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Reset");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act
        var result = await metadata!.InvokeAsync(null, null, CancellationToken.None);

        // Assert
        Assert.Null(result);
        Assert.True(subject.ResetCalled);
    }

    [Fact]
    public async Task TaskMethod_WithNoGenericResult_HasNullResultType()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Stop");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Null(metadata!.ResultType);
    }

    [Fact]
    public async Task InvokeAsync_MethodThrows_ExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ThrowingMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("Fail");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert — TargetInvocationException is unwrapped to surface the original exception
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("Something went wrong", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_AsyncMethodThrows_ExceptionPropagates()
    {
        // Arrange
        var context = CreateContext();
        var subject = new ThrowingMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("FailAsync");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert — TargetInvocationException is unwrapped to surface the original exception
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("Async failure", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_TooFewUserParameters_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Contains("Expected 1", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_TooManyUserParameters_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodWithSpecialParamsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("DoWork");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => metadata!.InvokeAsync([1, 2], null, CancellationToken.None));
        Assert.Contains("Expected 1", exception.Message);
        Assert.Contains("received 2", exception.Message);
    }

    [Fact]
    public void MethodWithDescription_PreservesDescription()
    {
        // Arrange
        var context = CreateContext();
        var subject = new DescribedMethodSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var property = registered.TryGetProperty("Shutdown");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert
        Assert.Equal("Shuts down the system safely", metadata!.Description);
    }

    [Fact]
    public void MethodWithoutAttributes_NotDiscovered()
    {
        // Arrange
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act — InterfaceOperationAsync exists but "ToString" should not be a method property
        var property = registered.TryGetProperty("ToString");

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public async Task TrulyAsyncMethod_InvokeAsync_PropagatesException()
    {
        // Arrange — tests that exceptions thrown after an await are correctly propagated
        var context = CreateContext();
        var subject = new TrulyAsyncThrowingSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;
        var property = registered.TryGetProperty("FailAfterAwait");
        var metadata = property!.GetValue() as MethodMetadata;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => metadata!.InvokeAsync(null, null, CancellationToken.None));
        Assert.Equal("truly async failure", exception.Message);
    }

    [Fact]
    public void InterfaceMethodDiscovered_WhenConcreteMethodLacksAttribute()
    {
        // Arrange — when both type and interface have the same method name,
        // the concrete type's version is registered (deduplication by name)
        var context = CreateContext();
        var subject = new MethodTestSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act — InterfaceOperationAsync is defined on both the interface and the type.
        // The interface declares [Operation(Title = "Interface Op")] but the concrete
        // implementation has no attribute, so the interface attribute is what gets discovered.
        var property = registered.TryGetProperty("InterfaceOperation");
        var metadata = property!.GetValue() as MethodMetadata;

        // Assert — only one property registered for this method name
        var allMethods = registered.GetAllMethods();
        Assert.Equal(1, allMethods.Count(m => m.Title == "Interface Op" || m.Title == "InterfaceOperation"));
    }

    [Fact]
    public void SubjectWithNoMethods_HasNoMethodProperties()
    {
        // Arrange
        var context = CreateContext();
        var subject = new NoMethodsSubject(context);
        var registered = subject.TryGetRegisteredSubject()!;

        // Act
        var methods = registered.GetAllMethods();

        // Assert
        Assert.Empty(methods);
    }

    [Fact]
    public void WhenSubjectMovesBetweenParents_ThenMethodPropertiesAreNotAddedTwice()
    {
        // Arrange
        var context = CreateContext();
        var first = new MethodTestSubjectHolder(context);
        var second = new MethodTestSubjectHolder(context);
        var subject = new MethodTestSubject();

        // Act: the move detaches the subject, so it is registered again under the new parent and
        // every lifecycle handler runs a second time over method properties that are already there.
        first.Child = subject;
        first.Child = null;
        second.Child = subject;

        // Assert
        var registered = ((IInterceptorSubject)subject).TryGetRegisteredSubject();
        Assert.NotNull(registered);
        Assert.NotNull(registered.TryGetProperty("Stop"));
    }

    [Fact]
    public void WhenAMethodNameClashesWithADeclaredProperty_ThenItIsReportedRatherThanSkipped()
    {
        // Arrange & Act: the subject declares a property named Stop and also has [Operation]
        // StopAsync(), so the method property cannot be created.
        var context = CreateContext();

        // Assert: the re-attach guard only treats an existing method property as already done, so a
        // genuine clash still reaches AddProperty instead of being silently dropped.
        var exception = Assert.Throws<InvalidOperationException>(() => new ClashingMethodSubject(context));
        Assert.Contains("Stop", exception.Message);
    }
}

[InterceptorSubject]
public partial class ClashingMethodSubject
{
    public partial string Stop { get; set; }

    public ClashingMethodSubject()
    {
        Stop = string.Empty;
    }

    [Operation]
    public Task StopAsync()
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class MethodTestSubjectHolder
{
    public partial MethodTestSubject? Child { get; set; }
}

public interface IMethodTestInterface
{
    [Operation(Title = "Interface Op")]
    Task InterfaceOperationAsync();
}

[InterceptorSubject]
public partial class MethodTestSubject : IMethodTestInterface
{
    public bool StopCalled { get; set; }

    [Operation(Title = "Stop", RequiresConfirmation = true)]
    public Task StopAsync()
    {
        StopCalled = true;
        return Task.CompletedTask;
    }

    [Operation]
    public Task SetTargetAsync(decimal temperature)
    {
        return Task.CompletedTask;
    }

    [Query(Title = "Diagnostics", Icon = "diag-icon", Position = 2)]
    public Task<string> GetDiagnosticsAsync()
    {
        return Task.FromResult("diagnostics-result");
    }

    public Task InterfaceOperationAsync()
    {
        return Task.CompletedTask;
    }
}

public interface ITestLogger { }

[InterceptorSubject]
public partial class MethodWithSpecialParamsSubject
{
    public int LastValue { get; set; }
    public CancellationToken LastToken { get; set; }

    [Operation]
    public Task DoWorkAsync(int value, [FromServices] ITestLogger? logger, CancellationToken cancellationToken)
    {
        LastValue = value;
        LastToken = cancellationToken;
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class VoidMethodSubject
{
    public bool ResetCalled { get; set; }

    [Operation(Title = "Reset")]
    public void Reset()
    {
        ResetCalled = true;
    }
}

[InterceptorSubject]
public partial class ThrowingMethodSubject
{
    [Operation(Title = "Fail")]
    public void Fail()
    {
        throw new InvalidOperationException("Something went wrong");
    }

    [Operation(Title = "Fail Async")]
    public Task FailAsyncAsync()
    {
        throw new InvalidOperationException("Async failure");
    }
}

[InterceptorSubject]
public partial class DescribedMethodSubject
{
    [Operation(Title = "Shutdown", Description = "Shuts down the system safely")]
    public Task ShutdownAsync()
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class TrulyAsyncThrowingSubject
{
    [Operation(Title = "Fail After Await")]
    public async Task FailAfterAwaitAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("truly async failure");
    }
}

[InterceptorSubject]
public partial class MethodWithRequiredServiceSubject
{
    [Operation]
    public Task DoWorkAsync([FromServices] ITestLogger logger)
    {
        return Task.CompletedTask;
    }
}

[InterceptorSubject]
public partial class NoMethodsSubject
{
    public partial string? Name { get; set; }
}
