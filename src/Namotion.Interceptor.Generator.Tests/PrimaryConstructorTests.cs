using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class PrimaryConstructorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenPrimaryConstructorTakesDependencies_ThenContextMirrorPreservesInitialization(bool hasSecondaryConstructor)
    {
        // Arrange
        var secondaryConstructor = hasSecondaryConstructor ? "public Subject() : this(17) { }" : "";
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Subject(int value)
            {
                {{secondaryConstructor}}
                public int InitialValue { get; } = value;
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        var constructor = type.GetConstructor([typeof(int), typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(constructor);
        var context = InterceptorSubjectContext.Create();
        using var provider = new ServiceCollection()
            .AddSingleton(typeof(int), 42)
            .AddSingleton<IInterceptorSubjectContext>(context)
            .BuildServiceProvider();
        var subject = (IInterceptorSubject)ActivatorUtilities.CreateInstance(provider, type);
        Assert.Same(context, subject.TryGetContext());
        Assert.Equal(42, type.GetProperty("InitialValue")!.GetValue(subject));
        Assert.Equal(hasSecondaryConstructor, type.GetConstructor(Type.EmptyTypes) is not null);
    }

    [Fact]
    public void WhenPrimaryConstructorIsParameterless_ThenOnlyContextOverloadIsGenerated()
    {
        // Arrange
        const string source = """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Subject()
            {
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        Assert.Equal(2, type.GetConstructors().Length);
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)type.GetConstructor([typeof(IInterceptorSubjectContext)])!.Invoke([context]);
        Assert.Same(context, subject.TryGetContext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenPrimaryAndSecondaryConstructorsHaveMirroredSignatures_ThenDeclaredConstructorWins(bool primaryTakesContext)
    {
        // Arrange
        var parameters = primaryTakesContext ? "int value, Namotion.Interceptor.IInterceptorSubjectContext context" : "int value";
        var secondary = primaryTakesContext
            ? "public Subject(int value) : this(value, null) { }"
            : "public Subject(int value, Namotion.Interceptor.IInterceptorSubjectContext context) : this(value) { SuppliedContext = context; }";
        var initializer = primaryTakesContext ? "= context;" : "";
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Subject({{parameters}})
            {
                {{secondary}}
                public int InitialValue { get; } = value;
                public Namotion.Interceptor.IInterceptorSubjectContext SuppliedContext { get; } {{initializer}}
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        Assert.Equal(2, type.GetConstructors().Length);
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)type.GetConstructor([typeof(int), typeof(IInterceptorSubjectContext)])!.Invoke([42, context]);
        Assert.Same(context, type.GetProperty("SuppliedContext")!.GetValue(subject));
        Assert.Null(subject.TryGetContext());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenPrimaryConstructorIsObsolete_ThenNoConstructorIsGenerated(bool parameterless, bool obsoleteIsError)
    {
        // Arrange
        var parameters = parameterless ? "" : "int value";
        var initializer = parameterless ? "" : "public int InitialValue { get; } = value;";
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            [method: System.Obsolete("legacy", {{obsoleteIsError.ToString().ToLowerInvariant()}})]
            public partial class Subject({{parameters}})
            {
                {{initializer}}
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        Assert.Single(type.GetConstructors());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenPrimaryConstructorSetsRequiredMembers_ThenMirrorPreservesTheContract(bool parameterless)
    {
        // Arrange
        var parameters = parameterless ? "" : "string name";
        var initializer = parameterless ? "\"initialized\"" : "name";
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            [method: System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
            public partial class Subject({{parameters}})
            {
                public required string Name { get; set; } = {{initializer}};
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        var constructor = type.GetConstructor(parameterless
            ? [typeof(IInterceptorSubjectContext)] : [typeof(string), typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(constructor);
        Assert.NotNull(constructor.GetCustomAttribute<SetsRequiredMembersAttribute>());
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)constructor.Invoke(parameterless ? [context] : ["initialized", context]);
        Assert.Equal("initialized", type.GetProperty("Name")!.GetValue(subject));
        Assert.Same(context, subject.TryGetContext());
    }

    [Theory]
    [InlineData("params int[] values", "values.Length")]
    [InlineData("ref int value", "value")]
    public void WhenPrimaryConstructorHasUnsupportedModifiers_ThenNoInvalidConstructorIsGenerated(string parameters, string initializer)
    {
        // Arrange
        var source = $$"""
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Subject({{parameters}})
            {
                public int InitialValue { get; } = {{initializer}};
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        Assert.Single(result.LoadAssembly().GetType("Subject")!.GetConstructors());
    }

    [Fact]
    public void WhenPrimaryConstructorIsInAnotherPartialDeclaration_ThenItsParametersAreMirrored()
    {
        // Arrange
        const string source = """
            using Dependency = System.Collections.Generic.List<string>;
            public partial class Subject(Dependency @event)
            {
                public object Dependency { get; } = @event;
            }
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Subject
            {
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        var dependency = new List<string>();
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)type.GetConstructor([typeof(List<string>), typeof(IInterceptorSubjectContext)])!
            .Invoke([dependency, context]);
        Assert.Same(dependency, type.GetProperty("Dependency")!.GetValue(subject));
        Assert.Same(context, subject.TryGetContext());
    }
}
