namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Covers declarations whose identifiers are C# keywords escaped with '@'. The escape is part of
/// the identifier's spelling but not part of its value, so a generator that copies the value into
/// generated source emits a bare keyword and produces code that cannot compile.
/// </summary>
public class EscapedIdentifierTests
{
    [Fact]
    public void WhenSubjectClassNameIsAnEscapedKeyword_ThenGeneratedCodeCompiles()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class @class
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Contains("public partial class @class", generated.SingleSource());
    }

    [Fact]
    public void WhenContainingTypeNameIsAnEscapedKeyword_ThenGeneratedCodeCompiles()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial class @class
    {
        [InterceptorSubject]
        public partial class Machine
        {
            public partial string Name { get; set; }
        }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Contains("partial class @class", generated.SingleSource());
    }

    [Fact]
    public void WhenInterceptedMethodParameterIsAnEscapedKeyword_ThenGeneratedCodeCompiles()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Machine
    {
        public partial string Name { get; set; }

        public void DoWorkWithoutInterceptor(int @event) { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Contains("public void DoWork(int @event)", generated.SingleSource());
    }

    [Fact]
    public void WhenNamespaceSegmentIsAnEscapedKeyword_ThenGeneratedCodeIsEmittedAndCompiles()
    {
        // Arrange: the namespace reaches the generated file's hint name, and '@' is rejected there,
        // which fails the whole subject instead of only mangling a name inside the file.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace @class.Inner
{
    [InterceptorSubject]
    public partial class Machine
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Equal("class.Inner.Machine.g.cs", generated.Sources.Single().HintName);
    }

    [Fact]
    public void WhenSubjectNameUsesAUnicodeEscape_ThenGeneratedCodeIsEmittedAndCompiles()
    {
        // Arrange: the other spelling an identifier can carry. It puts a backslash rather than an
        // '@' into the source name, so a hint name derived from that spelling fails the same way.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class \u0041bc
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Equal("Repro.Abc.g.cs", generated.Sources.Single().HintName);
    }
}
