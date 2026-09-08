using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator.Tests;

public class DiagnosticTests
{
    [Fact]
    public void WhenSubjectIsNotPartial_ThenNI0001IsReported()
    {
        // Arrange (case K)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public class NotPartial { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0001");
        Assert.Equal(DiagnosticSeverity.Error, generated.GeneratorDiagnostics.Single(d => d.Id == "NI0001").Severity);
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenContainingTypeIsNotPartial_ThenNI0002IsReported()
    {
        // Arrange (case L)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class Outer
    {
        [InterceptorSubject]
        public partial class Nested { }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0002");
        Assert.Empty(generated.Sources);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("record struct")]
    public void WhenAttributeIsOnRecord_ThenNI0003IsReported(string recordKeyword)
    {
        // Arrange (case M)
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    [InterceptorSubject]
    public partial {recordKeyword} NotAClass {{ }}
}}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0003");
        Assert.Empty(generated.Sources);
    }

    [Theory]
    [InlineData("struct")]
    [InlineData("interface")]
    public void WhenAttributeIsOnStructOrInterface_ThenGeneratorIsSilentAndCompilerReportsCS0592(string typeKeyword)
    {
        // Arrange: InterceptorSubjectAttribute's AttributeUsage is Class-only, so the compiler
        // already rejects this with CS0592. The generator's syntax-provider predicate excludes
        // struct and interface declarations entirely for performance, so it never runs its own
        // symbol lookup for them and, unlike for records, never reports NI0003 either.
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    [InterceptorSubject]
    public partial {typeKeyword} NotAClass {{ }}
}}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        Assert.Empty(generated.Sources);
        Assert.Contains(generated.CompilationErrors, d => d.Id == "CS0592");
    }

    [Fact]
    public void WhenSubjectIsGeneric_ThenNI0009IsReported()
    {
        // Arrange (case T)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Box<T>
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0009");
        Assert.Equal("Interceptor subject 'Box' is generic, which is not supported", diagnostic.GetMessage());
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsNestedInGenericContainingType_ThenNI0009NamesContainingTypeNotSubject()
    {
        // Arrange: the subject itself ("Inner") is not generic, but its containing type is. Roslyn
        // reports IsGenericType = true for a non-generic type nested inside a generic one, so the
        // diagnostic must not blame "Inner" for being generic; it must name "Outer" instead.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial class Outer<T>
    {
        [InterceptorSubject]
        public partial class Inner { }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0009");
        Assert.Equal(
            "Interceptor subject 'Inner' is nested in generic containing type 'Outer', which is not supported",
            diagnostic.GetMessage());
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsFileLocal_ThenNI0010IsReported()
    {
        // Arrange (case X)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    file partial class FileLocal
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains(generated.GeneratorDiagnostics, d => d.Id == "NI0010");
        Assert.Empty(generated.Sources);
    }

    [Fact]
    public void WhenSubjectIsNonPartialAndGeneric_ThenNI0009IsReportedInsteadOfNI0001()
    {
        // Arrange: a non-partial generic subject is fundamentally unsupported (NI0009) regardless
        // of the fixable NI0001 (missing partial). Reporting NI0001 first sends the user on a
        // wasted round trip: add partial, rebuild, and only then learn generics are unsupported.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public class NonPartialGenericBox<T> { }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics);
        Assert.Equal("NI0009", diagnostic.Id);
        Assert.Empty(generated.Sources);
    }

    [Theory]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class PlainSubject
    {
        public partial string Name { get; set; }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial class Outer
    {
        [InterceptorSubject]
        public partial class Nested
        {
            public partial string Name { get; set; }
        }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public partial record OuterRecord
    {
        [InterceptorSubject]
        public partial class Nested
        {
            public partial string Name { get; set; }
        }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public abstract class GenericBase<T> { }

    [InterceptorSubject]
    public partial class DerivedFromGenericBase : GenericBase<string>
    {
        public partial string Name { get; set; }
    }
}")]
    public void WhenSubjectIsLegal_ThenNoDiagnosticsAreReportedAndExactlyOneSourceIsGenerated(string source)
    {
        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        Assert.Single(generated.Sources);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsAnIndexer_ThenNI0006IsNotReported()
    {
        // Arrange (case E): an indexer is parameterised and has no usable name, so it was never a
        // candidate to become a subject property. A class-declared indexer has always been ignored
        // in silence; the interface-default path now agrees with it.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBag { string this[int index] => ""x""; }

    [InterceptorSubject]
    public partial class Bag : IBag { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenClassDeclaresAnIndexer_ThenNI0006IsNotReported()
    {
        // Arrange: the class-declared sibling of the previous case, pinning that the two paths
        // agree rather than agreeing by accident of the syntax filter.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Bag
    {
        public string this[int index] => ""x"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsStatic_ThenNI0006IsNotReported()
    {
        // Arrange (case V): a static member cannot be read from an instance, so it was never a
        // candidate, and no edit the user can make to a third-party interface would change that.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasStatic { static string Version => ""1.0""; }

    [InterceptorSubject]
    public partial class Thing : IHasStatic { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsInaccessible_ThenNI0006IsNotReported()
    {
        // Arrange (case W): a private interface default member is an intentional helper shared
        // between the interface's own default implementations, not a would-be subject property.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivate
    {
        double Value { get; set; }
        private string Hidden => ""h"";
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivate
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenInterfaceExplicitlyImplementsAnInaccessibleMember_ThenNI0006IsNotReported()
    {
        // Arrange: the explicit implementation lives in an interface, which may well be
        // third-party, so it is not the subject author's opt-in and carries no actionable remedy.
        // Contrast with the class-declared form below, which stays reported.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { protected string Secret { get; } }
    public interface IMale : IHuman { string IHuman.Secret => ""m""; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenClassExplicitImplementationIsInaccessible_ThenNI0006IsReported()
    {
        // Arrange: the class-declared sibling of the previous case. A protected interface member
        // is unreachable through the cast the emitter uses, so the implementation is dropped by
        // CollectProperties rather than by the interface-default loop.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBase { protected string Probe { get; set; } }

    [InterceptorSubject]
    public partial class Thing : IBase
    {
        string IBase.Probe { get => ""c""; set { } }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Contains("the member is not accessible from generated code", diagnostic.GetMessage());
    }

    [Fact]
    public void WhenInterfaceMembersAreAbstract_ThenNI0006IsNotReported()
    {
        // Arrange: the overwhelmingly common shape. An abstract interface member (indexer
        // included) is implemented by the class itself, so nothing is skipped and the advisory
        // rules must stay silent rather than firing on every interface a subject implements.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBag
    {
        string this[int index] { get; }
        string Name { get; }
    }

    [InterceptorSubject]
    public partial class Bag : IBag
    {
        public string this[int index] => ""x"";

        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenClassDeclaresAStaticProperty_ThenNI0006IsNotReported()
    {
        // Arrange: a static property declared directly in the class body. It is still skipped (the
        // emitted accessor would cast an instance to the class and read the static member through
        // it, which is CS0176), but a static member was never a candidate, so nothing is reported.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Holder
    {
        public static int Total { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        Assert.DoesNotContain(@"[""Total""]", generated.SingleSource());
    }

    [Fact]
    public void WhenClassExplicitlyImplementsAStaticInterfaceMember_ThenNI0006IsNotReported()
    {
        // Arrange: the class-declared, explicit-implementation sibling of the previous case. The
        // member is forced on the subject by a 'static abstract' interface member, so the static
        // rule outranks the explicit-implementation opt-in: there is nothing the user could change.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface ICounter { static abstract int Seed { get; set; } }

    [InterceptorSubject]
    public partial class Counter : ICounter
    {
        static int ICounter.Seed { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
        Assert.DoesNotContain(@"[""Seed""]", generated.SingleSource());
    }

    [Fact]
    public void WhenMethodIsNamedExactlyWithoutInterceptor_ThenNI0006IsReported()
    {
        // Arrange (case O)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Thing
    {
        public void WithoutInterceptor() { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Contains("the name has no prefix before 'WithoutInterceptor'", diagnostic.GetMessage());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodIsUnsupportedShape_ThenNI0006IsReportedPerMethod()
    {
        // Arrange (case Y: static, generic, by-reference parameters, explicit implementation)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFoo
    {
        void DoWithoutInterceptor();
    }

    [InterceptorSubject]
    public partial class Thing : IFoo
    {
        public static void StaticWithoutInterceptor() { }
        public void GenericWithoutInterceptor<T>(T value) { }
        public void RefWithoutInterceptor(ref int value) { }
        void IFoo.DoWithoutInterceptor() { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Equal(4, generated.GeneratorDiagnostics.Count(d => d.Id == "NI0006"));
    }

    [Fact]
    public void WhenWithoutInterceptorMethodReturnsByRef_ThenNI0006IsReportedAndWrapperIsSkipped()
    {
        // Arrange: GetFullTypeName returns null for a RefTypeSyntax return type, and the "?? void"
        // fallback used to swallow that silently: the wrapper compiled with a "void" return type,
        // dereferenced the ref return into a throwaway copy, and discarded it, with no diagnostic
        // telling the caller their return value now goes nowhere.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class RefReturnHolder
    {
        private int _value;

        public ref int GetRefWithoutInterceptor() => ref _value;
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0006");
        Assert.Contains("the method shape is not supported", diagnostic.GetMessage());
        Assert.Contains("by-reference return type", diagnostic.GetMessage());
        Assert.DoesNotContain("GetRef", generated.SingleSource());
    }

    [Fact]
    public void WhenAttributeIsOnExplicitImplementationInInterface_ThenNI0007IsReported()
    {
        // Arrange (case AC)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Label { get; } }
    public interface IMale : IHuman { [Derived] string IHuman.Label => ""m""; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void WhenAttributeIsOnExplicitImplementationInClass_ThenNI0007IsReported()
    {
        // Arrange: the class-declared sibling of the case AC shape. The emitter reflects the
        // interface member's PropertyInfo in both, so an attribute on the implementation is lost
        // in both, and the rule must not stop at the interface-declared form.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Label { get; } }

    [InterceptorSubject]
    public partial class John : IHuman
    {
        [Derived]
        string IHuman.Label => ""m"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0007");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void WhenExplicitImplementationHasNoAttributes_ThenNI0007IsNotReported()
    {
        // Arrange: nothing is lost when there is no attribute to lose.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Label { get; } }
    public interface IMale : IHuman { string IHuman.Label => ""m""; }

    [InterceptorSubject]
    public partial class John : IMale { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }

    [Fact]
    public void WhenTwoInterfaceDefaultMembersCollideOnName_ThenNI0008IsReported()
    {
        // Arrange (case AE)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFoo<T> { string Kind => ""k""; }

    [InterceptorSubject]
    public partial class Impl : IFoo<int>, IFoo<string> { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0008");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "'Kind' is provided by more than one member; the subject exposes Repro.IFoo<int>.Kind and Repro.IFoo<string>.Kind is unreachable",
            diagnostic.GetMessage());
        Assert.Contains(@"[""Kind""]", generated.SingleSource());
    }

    [Fact]
    public void WhenThreeInterfaceDefaultMembersCollideOnName_ThenEachWarningIdentifiesItsOwnLoser()
    {
        // Arrange: three interfaces at one location used to produce two byte-identical warnings,
        // leaving both the winner and the two losers undiscoverable from build output.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFirst { int Value => 1; }
    public interface ISecond { string Value => ""s""; }
    public interface IThird { bool Value => true; }

    [InterceptorSubject]
    public partial class Impl : IFirst, ISecond, IThird { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var messages = generated.GeneratorDiagnostics
            .Where(diagnostic => diagnostic.Id == "NI0008")
            .Select(diagnostic => diagnostic.GetMessage())
            .ToList();

        Assert.Equal(
            [
                "'Value' is provided by more than one member; the subject exposes Repro.IFirst.Value and Repro.ISecond.Value is unreachable",
                "'Value' is provided by more than one member; the subject exposes Repro.IFirst.Value and Repro.IThird.Value is unreachable"
            ],
            messages);
    }

    [Fact]
    public void WhenTwoExplicitImplementationsInClassCollideOnName_ThenNI0008IsReported()
    {
        // Arrange (case AA): the collision is resolved by DeduplicateByName rather than by the
        // interface-default loop, but the user-visible consequence is identical.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFoo<T> { string Kind { get; } }

    [InterceptorSubject]
    public partial class Impl : IFoo<int>, IFoo<string>
    {
        string IFoo<int>.Kind => ""int"";
        string IFoo<string>.Kind => ""string"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0008");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(
            "'Kind' is provided by more than one member; the subject exposes Repro.IFoo<int>.Kind and Repro.IFoo<string>.Kind is unreachable",
            diagnostic.GetMessage());
    }

    [Theory]
    [InlineData(@"
        int IFoo<int>.Kind => 1;
        string IFoo<string>.Kind => ""string"";
        public partial string Kind { get; set; }")]
    [InlineData(@"
        public partial string Kind { get; set; }
        int IFoo<int>.Kind => 1;
        string IFoo<string>.Kind => ""string"";")]
    public void WhenTwoExplicitImplementationsCollideWithAClassProperty_ThenNI0008NamesTheClassPropertyAsWinnerRegardlessOfDeclarationOrder(string members)
    {
        // Arrange: the class property wins either way, so the message must name it rather than
        // claim "the first declaration wins", and both dropped interface members are reported
        // because both are genuinely unreachable.
        var source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IFoo<T> { T Kind { get; } }

    [InterceptorSubject]
    public partial class Impl : IFoo<int>, IFoo<string>
    {" + members + @"
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var messages = generated.GeneratorDiagnostics
            .Where(diagnostic => diagnostic.Id == "NI0008")
            .Select(diagnostic => diagnostic.GetMessage())
            .ToList();

        Assert.Equal(
            [
                "'Kind' is provided by more than one member; the subject exposes the class property Repro.Impl.Kind and Repro.IFoo<int>.Kind is unreachable",
                "'Kind' is provided by more than one member; the subject exposes the class property Repro.Impl.Kind and Repro.IFoo<string>.Kind is unreachable"
            ],
            messages);
    }

    [Fact]
    public void WhenClassDeclaresAndExplicitlyImplementsSameProperty_ThenNI0008IsNotReported()
    {
        // Arrange (case Z): only one of the two declarations comes from an interface, so this is
        // not the "two interface members collide" shape NI0008 describes.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IKind { string Kind { get; } }

    [InterceptorSubject]
    public partial class Impl : IKind
    {
        public partial string Kind { get; set; }

        string IKind.Kind => ""explicit"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0008");
    }

    [Fact]
    public void WhenDerivedRedeclaresBaseImplementedProperty_ThenNI0005IsReported()
    {
        // Arrange (case AD): the base class fixed the interface mapping to the default
        // implementation, so the derived property is reachable only through the subject.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin => ""interface""; }
    public class BaseSubject : IHuman { }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var diagnostic = Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void WhenDerivedRedeclaresPropertyTheBaseClassItselfImplements_ThenNI0005IsReported()
    {
        // Arrange: the same divergence with a concrete base implementation instead of a default
        // interface member, so the rule is not tied to default implementations. The re-declaration
        // is not partial because the emitter cannot repeat a 'new' modifier on a partial property.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin { get; } }
    public class BaseSubject : IHuman { public string Origin => ""base""; }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public new string Origin => ""derived"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Single(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
    }

    [Theory]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin { get; } }

    [InterceptorSubject]
    public partial class Subject : IHuman
    {
        public partial string Origin { get; set; }
    }
}")]
    [InlineData(@"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin { get; } }
    public class BaseSubject { }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject, IHuman
    {
        public partial string Origin { get; set; }
    }
}")]
    public void WhenClassImplementsTheInterfaceMemberItself_ThenNI0005IsNotReported(string source)
    {
        // Arrange: the ordinary implicit-implementation shape, once without a base class and once
        // with one. The class property IS the implementation, so reading through the interface and
        // through the subject agree. This is the shape NI0005 is most at risk of over-firing on.

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
    }

    [Fact]
    public void WhenDerivedOverridesBaseInterfaceImplementation_ThenNI0005IsNotReported()
    {
        // Arrange: an override shares the base member's interface slot, so reading through the
        // interface lands on the derived property and the two readings agree.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHuman { string Origin { get; set; } }
    public class BaseSubject : IHuman { public virtual string Origin { get; set; } = ""base""; }

    [InterceptorSubject]
    public partial class DerivedSubject : BaseSubject
    {
        public override partial string Origin { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
    }

    [Fact]
    public void WhenBaseClassIsUnrelatedToACollidingInterfaceDefault_ThenNI0005IsNotReported()
    {
        // Arrange: Base does not implement IThing at all, so IThing is Sub's own interface to
        // implement. Sub's own "Name" does not actually bind to IThing.Name (the types differ), so
        // the interface's own default body ends up as the resolved implementation for IThing.Name.
        // The gate this pins previously read "implementation.ContainingType != typeSymbol" as proof
        // that some base class already implements the member, but that is equally true when the
        // resolved implementation is the interface's own default and no base class is involved at
        // all: Base never mentions IThing, so blaming it is false.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IThing { string Name => ""default""; }
    public class Base { }

    [InterceptorSubject]
    public partial class Sub : Base, IThing
    {
        public partial object Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(generated.GeneratorDiagnostics, d => d.Id == "NI0005");
    }

    [Fact]
    public void WhenSameDivergenceHasNoBaseClass_ThenNoDiagnosticIsReported()
    {
        // Arrange: the no-base-class mirror of the previous case. Sub declares "Name" directly
        // alongside IThing, "Name" does not bind to IThing.Name because the types differ, and the
        // interface's own default silently becomes the value read through IThing while Sub.Name
        // reads something else. This is a real, verified divergence, but neither NI0005 (its
        // message is specifically about a base class, and there is none here) nor NI0008 (there is
        // only one interface involved, not two colliding members) is the right vehicle for it, so
        // it is pinned here as a known, separately-scoped gap rather than silently left unverified.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IThing { string Name => ""default""; }

    [InterceptorSubject]
    public partial class Sub : IThing
    {
        public partial object Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Empty(generated.GeneratorDiagnostics);
    }
}
