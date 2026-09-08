namespace Namotion.Interceptor.Generator.Tests;

public class VirtualPartialTests
{
    [Fact]
    public Task Test_VirtualPartial_GeneratesCorrectly()
    {
        // Arrange - Test that virtual + partial generates virtual property
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class BaseClass
{
    public virtual partial string VirtualProp { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert - Should generate virtual property implementation
        var generatedSource = generated.SingleSource();
        Assert.Contains("public virtual partial string VirtualProp", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task Test_OverridePartial_GeneratesCorrectly()
    {
        // Arrange - Test that override + partial generates override property
        const string source = @"
using Namotion.Interceptor.Attributes;

public partial class BaseClass
{
    public virtual string VirtualProp { get; set; }
}

[InterceptorSubject]
public partial class DerivedClass : BaseClass
{
    public override partial string VirtualProp { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert - Should generate override property implementation
        var generatedSource = generated.SingleSource();
        Assert.Contains("public override partial string VirtualProp", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public Task Test_VirtualInheritanceChain_GeneratesCorrectly()
    {
        // Arrange - Test full inheritance chain with virtual/override
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class BaseEntity
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class Person : BaseEntity
{
    public override partial string Name { get; set; }
    public virtual partial int Age { get; set; }
}

[InterceptorSubject]
public partial class Employee : Person
{
    public override partial int Age { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert - Should generate all three classes correctly
        var generatedSource = generated.AllSources();
        Assert.Contains("public virtual partial string Name", generatedSource);
        Assert.Contains("public override partial string Name", generatedSource);
        Assert.Contains("public virtual partial int Age", generatedSource);
        Assert.Contains("public override partial int Age", generatedSource);
        return Verify(generatedSource).UseDirectory("Snapshots");
    }

    [Fact]
    public void WhenPartialPropertyIsDeclaredNew_ThenGeneratedPropertyCarriesNew()
    {
        // Arrange: a partial property that hides a base member. Without 'new' on the generated
        // declaration the two halves disagree on modifiers, which is CS8800.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class BaseClass { public string Label { get; set; } = ""base""; }

    [InterceptorSubject]
    public partial class DerivedClass : BaseClass
    {
        public new partial string Label { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains("public new partial string Label", generated.SingleSource());
        Assert.DoesNotContain(generated.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS0108");
    }

    [Fact]
    public void WhenPartialPropertyIsDeclaredSealedOverride_ThenGeneratedPropertyCarriesBothModifiers()
    {
        // Arrange: 'sealed' is only legal together with 'override', and the generated half has to
        // repeat both or the declarations disagree (CS8800).
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class BaseClass { public virtual string Label { get; set; } = ""base""; }

    [InterceptorSubject]
    public partial class DerivedClass : BaseClass
    {
        public sealed override partial string Label { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains("public sealed override partial string Label", generated.SingleSource());
    }

    [Fact]
    public void WhenPartialPropertyShadowsBaseMemberWithoutNew_ThenNI0005AndCS0108AreBothReported()
    {
        // Arrange: the shape NI0005 exists for. It co-fires with CS0108, whose only remedy is the
        // 'new' modifier, so NI0005 is only actionable if a 'new' partial property is emittable.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin { get; } }
    public class BaseSubject : IHuman { public string Origin => ""base""; }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Single(generated.GeneratorDiagnostics, diagnostic => diagnostic.Id == "NI0005");
        Assert.Contains(generated.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS0108");
    }

    [Fact]
    public void WhenTheNI0005RemedyIsApplied_ThenTheSubjectStillCompilesAndCS0108IsGone()
    {
        // Arrange: the previous case with 'new' added, which is what NI0005 and CS0108 together
        // ask the user to write.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin { get; } }
    public class BaseSubject : IHuman { public string Origin => ""base""; }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public new partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Single(generated.GeneratorDiagnostics, diagnostic => diagnostic.Id == "NI0005");
        Assert.DoesNotContain(generated.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS0108");
        Assert.Contains("public new partial string Origin", generated.SingleSource());
    }

    [Fact]
    public void Test_ExplicitInterfacePartial_IsNotAllowedInCSharp()
    {
        // Arrange - Test if C# allows explicit interface + partial
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasName
{
    string Name { get; set; }
}

[InterceptorSubject]
public partial class ExplicitImpl : IHasName
{
    partial string IHasName.Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        // The source is invalid by construction: C# forbids 'partial' on an explicit interface
        // implementation (CS0754), and since the generator rightly refuses to emit an implementing
        // part for one, the declaration is also left without an implementation (CS9248). Both ids
        // are asserted, and the count with them, so a generator-caused error would still fail here
        // instead of hiding behind a bare "some error was reported".
        Assert.Equal(2, generated.CompilationErrors.Count);
        Assert.Contains(generated.CompilationErrors, error => error.Id == "CS0754");
        Assert.Contains(generated.CompilationErrors, error => error.Id == "CS9248");
    }

    [Fact]
    public void Test_ImplicitInterfacePartial_Works()
    {
        // Arrange - Implicit interface implementation should work
        const string source = @"
using Namotion.Interceptor.Attributes;

public interface IHasName
{
    string Name { get; set; }
}

[InterceptorSubject]
public partial class ImplicitImpl : IHasName
{
    public partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert - Should compile successfully
        Assert.NotEmpty(generated.Sources);
    }
}
