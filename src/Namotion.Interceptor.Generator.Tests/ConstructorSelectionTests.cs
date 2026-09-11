using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class ConstructorSelectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenPrivateParameterlessConstructorOrderChanges_ThenThePublicContextOverloadRemainsAvailable(
        bool parameterlessFirst, bool parameterizedObsolete)
    {
        // Arrange
        const string parameterless = "private Subject() { }";
        var parameterized = (parameterizedObsolete ? "[System.Obsolete(\"legacy overload\")] " : "") +
            "public Subject(int value) { }";
        var constructors = parameterlessFirst ? parameterless + parameterized : parameterized + parameterless;
        var source = $$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Subject
            {
                {{constructors}}
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        var constructor = type.GetConstructor([typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(constructor);
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)constructor.Invoke([context]);
        Assert.Same(context, subject.TryGetContext());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WhenParameterlessConstructorIsObsolete_ThenNoGeneratedOverloadChainsToIt(
        bool parameterlessFirst, bool obsoleteIsError)
    {
        // Arrange
        var parameterless = $"[System.Obsolete(\"legacy overload\", {obsoleteIsError.ToString().ToLowerInvariant()})] public Subject() {{ }}";
        const string parameterized = "public Subject(int value) { }";
        var constructors = parameterlessFirst ? parameterless + parameterized : parameterized + parameterless;
        var source = $$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Subject
            {
                {{constructors}}
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        Assert.Null(type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, [typeof(IInterceptorSubjectContext)], modifiers: null));
        Assert.NotNull(type.GetConstructor([typeof(int), typeof(IInterceptorSubjectContext)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAContextOnlyConstructorIsDeclared_ThenParameterlessOrderNeverAddsADuplicate(bool parameterlessFirst)
    {
        // Arrange
        const string parameterless = "private Subject() { }";
        const string contextConstructor = "public Subject(Namotion.Interceptor.IInterceptorSubjectContext context) : this() { WasHandWritten = true; }";
        var constructors = parameterlessFirst ? parameterless + contextConstructor : contextConstructor + parameterless;
        var source = $$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Subject
            {
                {{constructors}}
                public bool WasHandWritten { get; }
                public partial string Name { get; set; }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        var subject = (IInterceptorSubject)type.GetConstructor([typeof(IInterceptorSubjectContext)])!
            .Invoke([InterceptorSubjectContext.Create()]);
        Assert.True((bool)type.GetProperty("WasHandWritten")!.GetValue(subject)!);
        Assert.Null(subject.TryGetContext());
    }
    /// <summary>
    /// A declared constructor whose later parameters are all optional or params is callable with the
    /// context alone. The generated arity-one overload would win that call by better overload
    /// resolution, so the hand-written body would silently never run.
    /// </summary>
    [Theory]
    [InlineData("bool initialize = true")]
    [InlineData("params int[] values")]
    [InlineData("bool initialize = true, params int[] values")]
    public void WhenADeclaredContextConstructorIsCallableWithTheContextAlone_ThenNoGeneratedOverloadDisplacesIt(string tail)
    {
        // Arrange
        var source = $$"""
            using Namotion.Interceptor.Attributes;
            [InterceptorSubject]
            public partial class Subject
            {
                public Subject() { }
                public Subject(Namotion.Interceptor.IInterceptorSubjectContext context, {{tail}}) { WasHandWritten = true; }
                public bool WasHandWritten { get; }
                public partial string Name { get; set; }
            }
            public static class SubjectFactory
            {
                public static Subject Create(Namotion.Interceptor.IInterceptorSubjectContext context) => new Subject(context);
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var assembly = result.LoadAssembly();
        var subject = (IInterceptorSubject)assembly.GetType("SubjectFactory")!
            .GetMethod("Create")!.Invoke(null, [InterceptorSubjectContext.Create()])!;
        Assert.True((bool)assembly.GetType("Subject")!.GetProperty("WasHandWritten")!.GetValue(subject)!);
    }

    /// <summary>
    /// None of these can take the context alone, so dropping the generated overload would leave the
    /// subject with no way to receive a context at all.
    /// </summary>
    [Theory]
    [InlineData("public", "Namotion.Interceptor.IInterceptorSubjectContext context, int value")]
    [InlineData("private", "Namotion.Interceptor.IInterceptorSubjectContext context, bool initialize = true")]
    [InlineData("public", "in Namotion.Interceptor.IInterceptorSubjectContext context")]
    [InlineData("public", "ref Namotion.Interceptor.IInterceptorSubjectContext context")]
    [InlineData("public", "params Namotion.Interceptor.IInterceptorSubjectContext[] contexts")]
    [InlineData("public", "Namotion.Interceptor.InterceptorSubjectContext context")]
    [InlineData("public", "IDerivedContext context")]
    public void WhenADeclaredConstructorCannotTakeTheContextAlone_ThenTheGeneratedOverloadRemains(
        string accessibility, string parameters)
    {
        // Arrange
        var source = $$"""
            using Namotion.Interceptor.Attributes;
            public interface IDerivedContext : Namotion.Interceptor.IInterceptorSubjectContext { }
            [InterceptorSubject]
            public partial class Subject
            {
                public Subject() { }
                {{accessibility}} Subject({{parameters}}) { }
                public partial string Name { get; set; }
            }
            public static class SubjectFactory
            {
                public static Subject Create(Namotion.Interceptor.IInterceptorSubjectContext context) => new Subject(context);
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var context = InterceptorSubjectContext.Create();
        var subject = (IInterceptorSubject)result.LoadAssembly().GetType("SubjectFactory")!
            .GetMethod("Create")!.Invoke(null, [context])!;
        Assert.Same(context, subject.TryGetContext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenParameterlessConstructorIsInAnotherPartialDeclaration_ThenRequiredMemberContractIsPreserved(bool parameterlessFirst)
    {
        // Arrange
        const string parameterlessDeclaration = """
            public partial class Subject
            {
                [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
                private Subject() { Name = "initialized"; }
            }
            """;
        const string attributedDeclaration = """
            [Namotion.Interceptor.Attributes.InterceptorSubject]
            public partial class Subject
            {
                public Subject(int value) { Name = "other"; }
                public required partial string Name { get; set; }
            }
            """;
        var source = parameterlessFirst ? parameterlessDeclaration + attributedDeclaration :
            attributedDeclaration + parameterlessDeclaration;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);
        var type = result.LoadAssembly().GetType("Subject")!;
        var constructor = type.GetConstructor([typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(constructor);
        Assert.NotNull(constructor.GetCustomAttribute<SetsRequiredMembersAttribute>());
        var subject = constructor.Invoke([InterceptorSubjectContext.Create()]);
        Assert.Equal("initialized", type.GetProperty("Name")!.GetValue(subject));
    }

}
