namespace Namotion.Interceptor.Generator.Tests;

public class SourceGeneratorTests
{
    [Fact]
    public Task WhenGeneratingClassWithInterceptorSubject_ThenPartialClassIsGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{
    public partial int Value { get; set; }
    public partial string? Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.SingleSource()).UseDirectory("Snapshots");
    }


    [Fact]
    public Task WhenGeneratingClassWithProtectedProperty_ThenPropertyCorrectlyGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{
    public partial int Value { get; set; }
    public partial string? Name { get; set; }

    protected string? Hidden { get; set; }
}

public partial class ClassWithoutInterceptorSubject
{
    public partial int Value { get; set; }
    public partial string? Name { get; set; }

    protected string? Hidden { get; set; }
}
";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        // The source is deliberately invalid: ClassWithoutInterceptorSubject declares partial
        // properties with no attribute, so nothing generates their implementation. Assert the
        // expected CS9248 pair and nothing else, so a generator-caused error would still fail here.
        // The count is asserted too, because Assert.All alone would pass on an empty collection and
        // stop proving that the protected property is excluded.
        Assert.Equal(2, generated.CompilationErrors.Count);
        Assert.All(generated.CompilationErrors, error =>
        {
            Assert.Equal("CS9248", error.Id);
            Assert.Contains("ClassWithoutInterceptorSubject", error.GetMessage());
        });

        return Verify(generated.SingleSource()).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WhenGeneratingClassWithInheritance_ThenPartialClassIsGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }

    public partial string LastName { get; set; }
}

[InterceptorSubject]
public partial class Teacher : Person
{
    public partial string MainCourse { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.AllSources()).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WhenGeneratingNestedClass_ThenPartialClassIsGeneratedWithContainingTypes()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

namespace TestNamespace
{
    public partial class OuterClass
    {
        [InterceptorSubject]
        public partial class NestedSubject
        {
            public partial string Name { get; set; }
        }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.SingleSource()).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WhenGeneratingDeepNestedClass_ThenPartialClassIsGeneratedWithAllContainingTypes()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

namespace TestNamespace
{
    public partial class Level1
    {
        public partial class Level2
        {
            [InterceptorSubject]
            public partial class DeepNestedSubject
            {
                public partial int Value { get; set; }
            }
        }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.SingleSource()).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WhenGeneratingClassWithProtectedInternalProperty_ThenPropertyCorrectlyGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{
    protected internal partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.SingleSource()).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WhenGeneratingClassWithPrivateProtectedProperty_ThenPropertyCorrectlyGenerated()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class SampleSubject
{
    private protected partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.SingleSource()).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WhenGeneratingClassWithInheritanceAndCustomAttribute_ThenBasePropertiesAreIncluded()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

public class MyInterceptorSubjectAttribute : InterceptorSubjectAttribute { }

[MyInterceptorSubject]
public partial class Light
{
    public partial string Name { get; set; }
    public partial bool On { get; set; }
}

[MyInterceptorSubject]
public partial class DimmableLight : Light
{
    public partial double Brightness { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        return Verify(generated.AllSources()).UseDirectory("Snapshots");
    }
}
