using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins the constructor mirroring contract: every declared constructor gets a generated twin with
/// a trailing <see cref="IInterceptorSubjectContext"/> parameter, so that dependency injection
/// selects an attaching constructor instead of silently producing a detached subject.
/// </summary>
public class ConstructorMirroringTests
{
    [Fact]
    public void WhenOnlyConstructorTakesDependencies_ThenDependencyInjectionAttachesTheSubject()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Dependency
                {
                }

                [InterceptorSubject]
                public partial class Service
                {
                    private readonly Dependency _dependency;

                    public Service(Dependency dependency)
                    {
                        _dependency = dependency;
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        var result = GeneratorTestHost.RunForExecution(source);
        Assert.Empty(result.CompilationErrors);

        var assembly = result.LoadAssembly();
        var serviceType = assembly.GetType("Repro.Service");
        Assert.NotNull(serviceType);
        var dependencyType = assembly.GetType("Repro.Dependency");
        Assert.NotNull(dependencyType);

        var context = InterceptorSubjectContext.Create();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IInterceptorSubjectContext), context);
        services.AddSingleton(dependencyType);
        using var provider = services.BuildServiceProvider();

        // Act
        var subject = (IInterceptorSubject)ActivatorUtilities.CreateInstance(provider, serviceType);

        // Assert
        Assert.Same(context, subject.TryGetContext());
    }

    [Fact]
    public void WhenMirroredSignatureIsDeclaredByHand_ThenNoDuplicateIsEmitted()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Dependency
                {
                }

                [InterceptorSubject]
                public partial class Service
                {
                    public Service(Dependency dependency)
                    {
                    }

                    public Service(Dependency dependency, IInterceptorSubjectContext context) : this(dependency)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert: a generated duplicate of the hand-written signature would be CS0111.
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.Equal(2, serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length);
    }

    [Fact]
    public void WhenParameterlessAndParameterizedConstructorsExist_ThenEachGetsAMirror()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Dependency
                {
                }

                [InterceptorSubject]
                public partial class Service
                {
                    public Service()
                    {
                    }

                    public Service(Dependency dependency)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var assembly = result.LoadAssembly();
        var serviceType = assembly.GetType("Repro.Service");
        Assert.NotNull(serviceType);
        var dependencyType = assembly.GetType("Repro.Dependency");
        Assert.NotNull(dependencyType);

        Assert.NotNull(serviceType.GetConstructor([typeof(IInterceptorSubjectContext)]));
        Assert.NotNull(serviceType.GetConstructor([dependencyType, typeof(IInterceptorSubjectContext)]));
        Assert.Equal(4, serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length);
    }

    [Fact]
    public void WhenParameterlessConstructorIsDeclaredAfterAParameterizedOne_ThenContextConstructorStillExists()
    {
        // Arrange: the declaration order matters because the parameterless detection inspects only
        // the first declared constructor, which historically left this shape with no context
        // constructor at all.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Dependency
                {
                }

                [InterceptorSubject]
                public partial class Service
                {
                    public Service(Dependency dependency)
                    {
                    }

                    public Service()
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.NotNull(serviceType.GetConstructor([typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAConstructorParameterIsNamedContext_ThenTheMirrorStillCompiles()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public Service(string context)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.NotNull(serviceType.GetConstructor([typeof(string), typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAConstructorIsObsolete_ThenNoMirrorIsEmitted()
    {
        // Arrange: a mirror would chain ": this(x)", which references the obsolete constructor
        // from the generated file and raises CS0618 there, an error in any consumer build with
        // TreatWarningsAsErrors, in code the consumer does not own.
        const string source = """
            using System;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    [Obsolete("use another constructor")]
                    public Service(int x)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.CompilationWarnings);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.Null(serviceType.GetConstructor([typeof(int), typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAnObsoleteConstructorAlreadyHasTheMirroredSignature_ThenNoDuplicateIsEmitted()
    {
        // Arrange: the mirror of Service(int) is Service(int, IInterceptorSubjectContext), which is
        // the signature the obsolete constructor already declares. Emitting it anyway is CS0111 in
        // a generated file the consumer cannot edit, so the collision check has to see obsolete
        // constructors even though they are never mirrored themselves.
        const string source = """
            using System;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public Service(int x)
                    {
                    }

                    [Obsolete("use the mirror")]
                    public Service(int x, IInterceptorSubjectContext context)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.NotNull(serviceType.GetConstructor([typeof(int), typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAConstructorIsObsoleteAsError_ThenNoMirrorIsEmitted()
    {
        // Arrange: with error: true the chained reference is CS0619, a hard error in every build.
        // The fully qualified spelling proves the detection resolves the attribute symbol rather
        // than matching the written identifier.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    [global::System.ObsoleteAttribute("gone", error: true)]
                    public Service(int x)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.Null(serviceType.GetConstructor([typeof(int), typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAConstructorHasAPointerParameter_ThenNoMirrorIsEmitted()
    {
        // Arrange: "unsafe" sits on the constructor rather than the parameter, so the parameter
        // modifier filter does not catch it, and a mirror would re-state the pointer type without
        // an unsafe context (CS0214 in the generated file).
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public unsafe Service(int* p)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source, allowUnsafe: true);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.Single(serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void WhenAConstructorParameterIsAnEscapedKeyword_ThenTheMirrorStillCompiles()
    {
        // Arrange: "@event" is a valid parameter name, but its unescaped spelling is a keyword, so
        // a mirror that drops the escape emits "event" as an identifier and fails to parse.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public Service(string @event)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.NotNull(serviceType.GetConstructor([typeof(string), typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAConstructorParameterIsAnEscapedContext_ThenTheMirrorUsesADistinctParameterName()
    {
        // Arrange: "@context" and "context" name the same parameter, so a mirror that appends a
        // plain "context" parameter declares that name twice.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public Service(string @context)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);
        Assert.NotNull(serviceType.GetConstructor([typeof(string), typeof(IInterceptorSubjectContext)]));
    }

    [Fact]
    public void WhenAMirroredConstructorSetsRequiredMembers_ThenTheMirrorCarriesTheAttribute()
    {
        // Arrange: the mirror chains with ": this(...)", and C# requires a constructor chaining to
        // a [SetsRequiredMembers] constructor to repeat the attribute (CS9039).
        const string source = """
            using System.Diagnostics.CodeAnalysis;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Dependency
                {
                }

                [InterceptorSubject]
                public partial class Service
                {
                    public required int Threshold { get; set; }

                    [SetsRequiredMembers]
                    public Service(Dependency dependency)
                    {
                        Threshold = 1;
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var assembly = result.LoadAssembly();
        var serviceType = assembly.GetType("Repro.Service");
        Assert.NotNull(serviceType);
        var dependencyType = assembly.GetType("Repro.Dependency");
        Assert.NotNull(dependencyType);

        var mirror = serviceType.GetConstructor([dependencyType, typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(mirror);
        Assert.NotNull(mirror.GetCustomAttribute<SetsRequiredMembersAttribute>());
    }

    [Fact]
    public void WhenTheParameterlessConstructorSetsRequiredMembers_ThenTheContextConstructorCarriesTheAttribute()
    {
        // Arrange: the context form of the parameterless constructor chains the same way.
        const string source = """
            using System.Diagnostics.CodeAnalysis;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public required int Threshold { get; set; }

                    [SetsRequiredMembers]
                    public Service()
                    {
                        Threshold = 1;
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var serviceType = result.LoadAssembly().GetType("Repro.Service");
        Assert.NotNull(serviceType);

        var contextConstructor = serviceType.GetConstructor([typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(contextConstructor);
        Assert.NotNull(contextConstructor.GetCustomAttribute<SetsRequiredMembersAttribute>());
    }

    [Fact]
    public void WhenAMirroredConstructorDoesNotSetRequiredMembers_ThenTheMirrorDoesNotCarryTheAttribute()
    {
        // Arrange: adding the attribute unconditionally would tell every caller of the mirror that
        // the required members are already initialised, which silently disables the enforcement.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public class Dependency
                {
                }

                [InterceptorSubject]
                public partial class Service
                {
                    public required int Threshold { get; set; }

                    public Service(Dependency dependency)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);

        // Assert
        Assert.Empty(result.CompilationErrors);

        var assembly = result.LoadAssembly();
        var serviceType = assembly.GetType("Repro.Service");
        Assert.NotNull(serviceType);
        var dependencyType = assembly.GetType("Repro.Dependency");
        Assert.NotNull(dependencyType);

        var mirror = serviceType.GetConstructor([dependencyType, typeof(IInterceptorSubjectContext)]);
        Assert.NotNull(mirror);
        Assert.Null(mirror.GetCustomAttribute<SetsRequiredMembersAttribute>());
    }
}
