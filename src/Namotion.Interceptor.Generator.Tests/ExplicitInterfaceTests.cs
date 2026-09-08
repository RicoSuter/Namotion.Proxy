namespace Namotion.Interceptor.Generator.Tests;

public class ExplicitInterfaceTests
{
    [Fact]
    public void WhenSubInterfaceExplicitlyImplementsMember_ThenGeneratedCodeCompiles()
    {
        // Arrange (case A, the shape reported in issue 428)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public interface IHuman { Gender Gender { get; } }
    public interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Gender""]", generatedSource);
        Assert.Contains("((global::Repro.IHuman)o).Gender", generatedSource);
        // The reported bug doubly qualified the member through both interfaces
        // (nameof(global::Repro.IMale.Repro.IHuman.Gender)); the fix resolves the name from the
        // implemented member, so only the single, correctly-cast form should ever appear.
        Assert.DoesNotContain("IMale.Repro.IHuman.Gender", generatedSource);
    }

    [Fact]
    public void WhenClassExplicitlyImplementsMember_ThenGeneratedCodeCompiles()
    {
        // Arrange (case B)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public interface IHuman { Gender Gender { get; } }

    [InterceptorSubject]
    public partial class John : IHuman
    {
        Gender IHuman.Gender => Gender.Male;
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains("((global::Repro.IHuman)o).Gender", generated.SingleSource());
    }

    [Fact]
    public void WhenClassDeclaresPropertyAndInheritsExplicitImplementation_ThenGeneratedCodeCompiles()
    {
        // Arrange (case C)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public interface IHuman { Gender Gender { get; } }
    public interface IMale : IHuman { Gender IHuman.Gender => Gender.Male; }

    [InterceptorSubject]
    public partial class John : IMale
    {
        public partial Gender Gender { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert: the tracked class property wins, so it is intercepted
        var generatedSource = generated.SingleSource();
        Assert.Contains("isIntercepted: true", generatedSource);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(generatedSource, @"\[""Gender""\]"));
    }

    [Fact]
    public void WhenExplicitImplementationTargetsNestedInterface_ThenGeneratedCodeCompiles()
    {
        // Arrange (case D)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public enum Gender { Male, Female }
    public partial class Outer { public interface IHuman { Gender Gender { get; } } }
    public interface IMale : Outer.IHuman { Gender Outer.IHuman.Gender => Gender.Male; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains(@"[""Gender""]", generated.SingleSource());
    }

    [Fact]
    public void WhenExplicitImplementationTargetsGenericInterface_ThenGeneratedCodeCompiles()
    {
        // Arrange (case F)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman<T> { T Value { get; } }
    public interface IIntHuman : IHuman<int> { int IHuman<int>.Value => 42; }

    [InterceptorSubject]
    public partial class John : IIntHuman { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Value""]", generatedSource);
        Assert.Contains("((global::Repro.IHuman<int>)o).Value", generatedSource);
    }

    [Fact]
    public void WhenReportedIssueShapeIsUsed_ThenGeneratedCodeCompiles()
    {
        // Arrange: issue 428's shape, renamed, including the global namespace
        const string source = @"
using Namotion.Interceptor.Attributes;

public enum Rank { Junior, Senior }

public interface IEmployee { Rank Rank { get; } }

public interface ISenior : IEmployee { Rank IEmployee.Rank => Rank.Senior; }

[InterceptorSubject]
public partial class Alice : ISenior { }";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Rank""]", generatedSource);
        Assert.Contains("((global::IEmployee)o).Rank", generatedSource);
        Assert.Contains("nameof(global::IEmployee.Rank)", generatedSource);
        Assert.DoesNotContain("YourDefaultNamespace", generatedSource);
    }
}
