namespace Namotion.Interceptor.Generator.Tests;

public class GeneratorShapeTests
{
    [Fact]
    public void WhenSubjectIsInGlobalNamespace_ThenGeneratedCodeCompiles()
    {
        // Arrange
        const string source = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class GlobalSubject
{
    public partial string Name { get; set; }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.DoesNotContain("YourDefaultNamespace", generated.SingleSource());
        Assert.Empty(generated.CompilationErrors);
    }

    [Fact]
    public void WhenSubjectIsInternal_ThenGeneratedCodeCompiles()
    {
        // Arrange (case S)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    internal partial class InternalSubject
    {
        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Contains("internal partial class InternalSubject", generated.SingleSource());
        Assert.Empty(generated.CompilationErrors);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("record struct")]
    [InlineData("struct")]
    [InlineData("interface")]
    public void WhenSubjectIsNestedInNonClassType_ThenGeneratedCodeCompiles(string containerKeyword)
    {
        // Arrange (cases P, Q, R)
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    public partial {containerKeyword} Outer
    {{
        [InterceptorSubject]
        public partial class Nested
        {{
            public partial string Name {{ get; set; }}
        }}
    }}
}}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains($"partial {containerKeyword} Outer", generated.SingleSource());
    }

    [Fact]
    public void WhenBaseListIsOnADifferentPartialDeclarationThanTheAttribute_ThenTheBaseClassIsStillDetected()
    {
        // Arrange: properties, methods and interfaces are all collected across every partial
        // declaration, but the base class used to be read off the attributed declaration's base
        // list alone. Put ": BaseSubject" on the other half and the generated file lost the base
        // entirely: it re-declared the INotifyPropertyChanged members the base already provides,
        // and DefaultProperties shadowed the base's without concatenating it, so the subject
        // reported only its own properties.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class BaseSubject
    {
        public partial string BaseName { get; set; }
    }

    [InterceptorSubject]
    public partial class DerivedSubject
    {
        public partial string DerivedName { get; set; }
    }

    public partial class DerivedSubject : BaseSubject
    {
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var derivedSource = Assert
            .Single(generated.Sources, generatedSource => generatedSource.HintName.Contains("DerivedSubject"))
            .SourceText.ToString();

        Assert.Contains("public partial class DerivedSubject : IInterceptorSubject\n", derivedSource);
        Assert.Contains("public new static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties", derivedSource);
        Assert.Contains(".Concat(global::Repro.BaseSubject.DefaultProperties)", derivedSource);
        Assert.DoesNotContain("public event PropertyChangedEventHandler? PropertyChanged;", derivedSource);
        Assert.DoesNotContain("void IRaisePropertyChanged.RaisePropertyChanged", derivedSource);
        Assert.DoesNotContain(generated.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS0108");
    }

    [Fact]
    public void WhenInterfaceHasDefaultIndexer_ThenIndexerIsSkipped()
    {
        // Arrange (case E)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBag { string this[int i] => ""x""; }

    [InterceptorSubject]
    public partial class Bag : IBag { }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain("this[]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasStaticProperty_ThenPropertyIsSkipped()
    {
        // Arrange (case V)
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
        Assert.DoesNotContain(@"[""Version""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasPrivateDefaultMember_ThenMemberIsSkipped()
    {
        // Arrange (case W)
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
        Assert.DoesNotContain(@"[""Hidden""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceHasInternalDefaultMember_ThenMemberIsKept()
    {
        // Arrange (regression guard: internal members are reachable from generated code)
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasInternal
    {
        double Value { get; set; }
        internal string Status => ""s"";
        protected internal string Label => ""l"";
    }

    [InterceptorSubject]
    public partial class Thing : IHasInternal
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Status""]", generatedSource);
        Assert.Contains(@"[""Label""]", generatedSource);
    }

    [Fact]
    public void WhenExplicitImplementationTargetsProtectedMember_ThenMemberIsSkipped()
    {
        // Arrange (a protected interface member cannot be reached even through an explicit
        // implementation: Roslyn reports the implementation itself as Private, but the
        // accessibility that governs reachability is the implemented member's)
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
        Assert.DoesNotContain(@"[""Secret""]", generated.SingleSource());
    }

    [Fact]
    public void WhenClassExplicitImplementationTargetsProtectedMember_ThenMemberIsSkipped()
    {
        // Arrange: the class-declared sibling of WhenExplicitImplementationTargetsProtectedMember_ThenMemberIsSkipped.
        // The implementation lives directly on the subject class instead of a sub-interface, so it
        // is discovered by CollectProperties, not ExtractInterfaceDefaultProperties.
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
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenClassExplicitImplementationTargetsPrivateProtectedMember_ThenMemberIsSkipped()
    {
        // Arrange: same shape with `private protected`, which is subject to the same CS1540 rule.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBase { private protected string Probe { get; set; } }

    [InterceptorSubject]
    public partial class Thing : IBase
    {
        string IBase.Probe { get => ""c""; set { } }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenClassExplicitImplementationOfGetterOnlyProtectedMember_ThenMemberIsSkipped()
    {
        // Arrange: the getter-only form. The interface member itself has no setter to fall back
        // to, so an inaccessible getter must skip the property entirely, not emit a null-getter shape.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBase { protected string Probe { get; } }

    [InterceptorSubject]
    public partial class Thing : IBase
    {
        string IBase.Probe => ""c"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenClassExplicitImplementationOfGetterOnlyPrivateProtectedMember_ThenMemberIsSkipped()
    {
        // Arrange: getter-only form with `private protected`.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IBase { private protected string Probe { get; } }

    [InterceptorSubject]
    public partial class Thing : IBase
    {
        string IBase.Probe => ""c"";
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsInternalInReferencedAssembly_ThenMemberIsSkipped()
    {
        // Arrange: the "same assembly" premise the accessibility check used to rely on does not
        // hold when the interface is declared in a referenced assembly; the generated code lives
        // in a different assembly with no InternalsVisibleTo, so an internal member is unreachable
        // there (CS0122) even though it would be reachable if declared locally.
        const string librarySource = @"
public interface IFace
{
    internal string Probe => ""p"";
}";
        const string mainSource = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Thing : IFace
{
}";

        // Act
        var generated = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsInternalInReferencedAssemblyWithInternalsVisibleTo_ThenMemberIsKept()
    {
        // Arrange: same shape as the previous case, but the library grants InternalsVisibleTo to
        // the generated assembly (named "TestAssembly" by GeneratorTestHost), which the compiler's
        // own accessibility check must honor and expose the member.
        const string librarySource = @"
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo(""TestAssembly"")]

public interface IFace
{
    internal string Probe => ""p"";
}";
        const string mainSource = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Thing : IFace
{
}";

        // Act
        var generated = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);

        // Assert
        Assert.Contains(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberIsProtectedInternalInReferencedAssembly_ThenMemberIsSkipped()
    {
        // Arrange: cross-assembly, "protected internal" fails both halves of its own rule: the
        // internal half fails because the generated assembly has no InternalsVisibleTo, and the
        // protected half fails because the generated code accesses the member through a cast to
        // the interface type, not through the subject type itself (CS1540).
        const string librarySource = @"
public interface IFace
{
    protected internal string Probe => ""p"";
}";
        const string mainSource = @"
using Namotion.Interceptor.Attributes;

[InterceptorSubject]
public partial class Thing : IFace
{
}";

        // Act
        var generated = GeneratorTestHost.RunWithLibraryReferenceExpectingCleanCompilation(librarySource, mainSource);

        // Assert
        Assert.DoesNotContain(@"[""Probe""]", generated.SingleSource());
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasPrivateSetter_ThenSetterIsDroppedButGetterIsKept()
    {
        // Arrange: the property itself is public (interface members default to public), but its
        // setter is individually private, so only the getter is reachable from generated code.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivateSetter
    {
        double Value { get; set; }
        string Probe { get => ""a""; private set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivateSetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.Contains("(o) => ((global::Repro.IHasPrivateSetter)o).Probe", generatedSource);
        Assert.DoesNotContain(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasPrivateGetter_ThenGetterIsDroppedButSetterIsKept()
    {
        // Arrange: mirror of the previous case, with the private accessor on the getter instead.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasPrivateGetter
    {
        double Value { get; set; }
        string Probe { private get => ""a""; set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasPrivateGetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.DoesNotContain("(o) => ((global::Repro.IHasPrivateGetter)o).Probe", generatedSource);
        Assert.Contains(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasProtectedSetter_ThenSetterIsDroppedButGetterIsKept()
    {
        // Arrange: same reasoning as the protected-member case, but scoped to a single accessor:
        // generated code accesses the member through a cast to the interface type, which is never
        // a valid qualifying expression for protected access (CS1540).
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasProtectedSetter
    {
        double Value { get; set; }
        string Probe { get => ""a""; protected set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasProtectedSetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.Contains("(o) => ((global::Repro.IHasProtectedSetter)o).Probe", generatedSource);
        Assert.DoesNotContain(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenInterfaceDefaultMemberHasInternalSetter_ThenSetterIsKept()
    {
        // Arrange (regression guard): an internal setter in the same assembly stays reachable,
        // same as a whole internal member does.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public interface IHasInternalSetter
    {
        double Value { get; set; }
        string Probe { get => ""a""; internal set { } }
    }

    [InterceptorSubject]
    public partial class Thing : IHasInternalSetter
    {
        public partial double Value { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains(@"[""Probe""]", generatedSource);
        Assert.Contains("(o) => ((global::Repro.IHasInternalSetter)o).Probe", generatedSource);
        Assert.Contains(".Probe = ", generatedSource);
    }

    [Fact]
    public void WhenWithoutInterceptorMethodReturnsGenericTypeNestedInAnotherType_ThenWrapperNamesTheEnclosingType()
    {
        // Arrange: the wrapper's return type used to be rebuilt as
        // "{ContainingNamespace}.{Name}<...>", which drops every enclosing type and emits
        // "Repro.Inner<int>", a type that does not exist (CS0234).
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    public class Outer { public class Inner<T> { } }

    [InterceptorSubject]
    public partial class Maker
    {
        public partial string Name { get; set; }

        public Outer.Inner<int> BuildWithoutInterceptor() => new Outer.Inner<int>();
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        Assert.Contains("public global::Repro.Outer.Inner<int> Build()", generated.SingleSource());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodReturnsGenericTypeInGlobalNamespace_ThenWrapperNamesItWithGlobalPrefix()
    {
        // Arrange: a global-namespace generic used to render its containing namespace as the
        // literal "<global namespace>", which does not parse and destroyed the whole generated
        // file, taking every unrelated property of the same subject down with it.
        const string source = @"
using Namotion.Interceptor.Attributes;

public class Box<T> { }

[InterceptorSubject]
public partial class Maker
{
    public partial string Name { get; set; }

    public Box<int> BuildWithoutInterceptor(Box<string> template) => new Box<int>();
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains("public global::Box<int> Build(global::Box<string> template)", generatedSource);
        Assert.DoesNotContain("global namespace", generatedSource);
    }

    [Fact]
    public void WhenWithoutInterceptorMethodReturnsGenericTypeNestedInTheSubject_ThenWrapperNamesTheSubject()
    {
        // Arrange: the same drop-the-enclosing-type defect, with the subject itself as the
        // enclosing type. "Current" is here so the property path, which names the identical type
        // through SymbolDisplayFormat.FullyQualifiedFormat, is pinned alongside the method path.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Holder
    {
        public class Item<T> { }

        public partial Item<int> Current { get; set; }

        public Item<int> BuildWithoutInterceptor() => new Item<int>();
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains("public global::Repro.Holder.Item<int> Build()", generatedSource);
        Assert.Contains("private global::Repro.Holder.Item<int> _Current;", generatedSource);
    }

    [Fact]
    public void WhenMethodIsNamedExactlyWithoutInterceptor_ThenMethodIsSkipped()
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
        Assert.DoesNotContain("public void ()", generated.SingleSource());
    }

    [Fact]
    public void WhenWithoutInterceptorMethodIsUnsupportedShape_ThenMethodIsSkipped()
    {
        // Arrange (case Y: static, generic, by-reference parameters, and explicit interface implementation)
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
        var generatedSource = generated.SingleSource();
        Assert.DoesNotContain("public void Static(", generatedSource);
        Assert.DoesNotContain("public void Generic(", generatedSource);
        Assert.DoesNotContain("public void Ref(", generatedSource);
        Assert.DoesNotContain("public void Do(", generatedSource);
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesInParameter_ThenWrapperIsGeneratedWhileRefAndOutStaySkipped()
    {
        // Arrange: an "in" argument is passable by value, so the wrapper compiles. A "ref" or "out"
        // argument is not (CS1620), so those two shapes stay skipped.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Machine
    {
        public partial string Name { get; set; }

        public void SendWithoutInterceptor(in int value) { }
        public void PushWithoutInterceptor(ref int value) { }
        public void PullWithoutInterceptor(out int value) { value = 0; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains("public void Send(int value)", generatedSource);
        Assert.Contains("SendWithoutInterceptor((int)p[0]!)", generatedSource);
        Assert.DoesNotContain("public void Push(", generatedSource);
        Assert.DoesNotContain("public void Pull(", generatedSource);

        var skipped = generated.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "NI0006").ToList();
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, diagnostic => Assert.DoesNotContain("SendWithoutInterceptor", diagnostic.GetMessage()));
        Assert.Contains(skipped, diagnostic => diagnostic.GetMessage().Contains("PushWithoutInterceptor"));
        Assert.Contains(skipped, diagnostic => diagnostic.GetMessage().Contains("PullWithoutInterceptor"));
    }

    [Fact]
    public void WhenWithoutInterceptorMethodTakesRefReadonlyParameter_ThenWrapperIsGeneratedWhileRefAndOutStaySkipped()
    {
        // Arrange: a "ref readonly" argument accepts a temporary, so the wrapper compiles and only
        // warns with CS9193, which the generated file suppresses. A plain "ref" or an "out" argument
        // is a hard error (CS1620), so those shapes stay skipped, and so does a method that mixes a
        // "ref readonly" parameter with a plain "ref" one.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Machine
    {
        public partial string Name { get; set; }

        public void SendWithoutInterceptor(ref readonly int value) { }
        public void PushWithoutInterceptor(ref int value) { }
        public void PullWithoutInterceptor(out int value) { value = 0; }
        public void MixWithoutInterceptor(ref readonly int first, ref int second) { }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingCleanCompilation(source);

        // Assert
        var generatedSource = generated.SingleSource();
        Assert.Contains("#pragma warning disable CS9193", generatedSource);
        Assert.Contains("public void Send(int value)", generatedSource);
        Assert.Contains("SendWithoutInterceptor((int)p[0]!)", generatedSource);
        Assert.DoesNotContain("public void Push(", generatedSource);
        Assert.DoesNotContain("public void Pull(", generatedSource);
        Assert.DoesNotContain("public void Mix(", generatedSource);

        var skipped = generated.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "NI0006").ToList();
        Assert.Equal(3, skipped.Count);
        Assert.All(skipped, diagnostic => Assert.DoesNotContain("SendWithoutInterceptor", diagnostic.GetMessage()));
        Assert.Contains(skipped, diagnostic => diagnostic.GetMessage().Contains("PushWithoutInterceptor"));
        Assert.Contains(skipped, diagnostic => diagnostic.GetMessage().Contains("PullWithoutInterceptor"));
        Assert.Contains(skipped, diagnostic => diagnostic.GetMessage().Contains("MixWithoutInterceptor"));
    }

    [Fact]
    public void WhenAnInterceptedMethodParameterIsAnEscapedKeyword_ThenGeneratedCodeCompiles()
    {
        // Arrange: the wrapper restates the parameter list, so dropping the escape emits the
        // keyword "event" where an identifier belongs.
        const string source = """
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Service
                {
                    public void DoWorkWithoutInterceptor(string @event)
                    {
                    }

                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Empty(generated.CompilationErrors);
    }

    [Fact]
    public void WhenTheSubjectClassNameIsAnEscapedKeyword_ThenGeneratedCodeCompiles()
    {
        // Arrange: the class name is restated on the generated partial declaration, on every cast
        // and on each generated constructor.
        const string source = """
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class @class
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Empty(generated.CompilationErrors);
    }

    [Fact]
    public void WhenAContainingTypeNameIsAnEscapedKeyword_ThenGeneratedCodeCompiles()
    {
        // Arrange: the generated file reopens every containing type by name.
        const string source = """
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public partial class @class
                {
                    [InterceptorSubject]
                    public partial class Service
                    {
                        public partial string Name { get; set; }
                    }
                }
            }
            """;

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert
        Assert.Empty(generated.CompilationErrors);
    }

    [Fact]
    public void WhenTheOnlyDeclaredConstructorIsStatic_ThenBothConstructorsAreGenerated()
    {
        // Arrange: a static constructor is not an instance constructor, so nothing can chain to it
        // and the subject still needs a generated parameterless one.
        const string source = @"
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Machine
    {
        public static readonly string Fallback;

        static Machine()
        {
            Fallback = ""fallback"";
        }

        public partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Contains("public Machine()", generated.SingleSource());
        Assert.Contains("public Machine(IInterceptorSubjectContext context) : this()", generated.SingleSource());
    }

    [Fact]
    public void WhenChainedConstructorSetsRequiredMembers_ThenGeneratedContextConstructorRepeatsTheAttribute()
    {
        // Arrange (case RM): the generated context constructor chains to the declared parameterless
        // one with ": this()", and C# rejects that chain with CS9039 unless the chaining constructor
        // repeats [SetsRequiredMembers].
        const string source = @"
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class Machine
    {
        public required partial string Name { get; set; }

        [SetsRequiredMembers]
        public Machine()
        {
            Name = ""default"";
        }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        Assert.Contains(
            "        [global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]" + Environment.NewLine +
            "        public Machine(IInterceptorSubjectContext context) : this()",
            generated.SingleSource());
    }

    [Fact]
    public void WhenBaseConstructorSetsRequiredMembers_ThenEveryDerivedGeneratedConstructorRepeatsTheAttribute()
    {
        // Arrange (case RM3): each generated parameterless constructor chains implicitly to its base
        // one, and CS9039 applies to that chain too. The leaf sits two levels below the attributed
        // constructor: while it is extracted, the attributed constructor the generator emits for the
        // middle subject does not exist yet, so the middle type only shows an implicit, unattributed
        // one and the base has to be found through it.
        const string source = @"
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class BaseMachine
    {
        public required partial string Id { get; set; }

        [SetsRequiredMembers]
        public BaseMachine()
        {
            Id = ""base-default"";
        }
    }

    [InterceptorSubject]
    public partial class MiddleMachine : BaseMachine
    {
        public partial string Name { get; set; }
    }

    [InterceptorSubject]
    public partial class LeafMachine : MiddleMachine
    {
        public partial string Label { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert
        var generatedSources = generated.AllSources();
        Assert.Contains(
            "        [global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]" + Environment.NewLine +
            "        public MiddleMachine()",
            generatedSources);
        Assert.Contains(
            "        [global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]" + Environment.NewLine +
            "        public MiddleMachine(IInterceptorSubjectContext context) : this()",
            generatedSources);
        Assert.Contains(
            "        [global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]" + Environment.NewLine +
            "        public LeafMachine()",
            generatedSources);
        Assert.Contains(
            "        [global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]" + Environment.NewLine +
            "        public LeafMachine(IInterceptorSubjectContext context) : this()",
            generatedSources);
    }

    [Fact]
    public void WhenDerivedSubjectDeclaresItsOwnRequiredMember_ThenGeneratedConstructorsOmitTheAttribute()
    {
        // Arrange: the base constructor sets the base's required member, but nothing sets the one the
        // derived subject adds. A generated constructor carrying the attribute would turn the CS9039
        // this shape has always produced into a member that is silently null after 'new DerivedMachine()'.
        const string source = @"
using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Attributes;
namespace Repro
{
    [InterceptorSubject]
    public partial class BaseMachine
    {
        public required partial string Id { get; set; }

        [SetsRequiredMembers]
        public BaseMachine()
        {
            Id = ""base-default"";
        }
    }

    [InterceptorSubject]
    public partial class DerivedMachine : BaseMachine
    {
        public required partial string Name { get; set; }
    }
}";

        // Act
        var generated = GeneratorTestHost.Run(source);

        // Assert: the derived subject keeps the compile error and has to declare the initializing
        // constructor itself.
        var derivedSource = generated.Sources
            .Single(generatedSource => generatedSource.HintName == "Repro.DerivedMachine.g.cs")
            .SourceText
            .ToString();
        Assert.DoesNotContain("SetsRequiredMembers", derivedSource);
        Assert.Contains(generated.CompilationErrors, diagnostic => diagnostic.Id == "CS9039");
    }

    [Theory]
    [InlineData("")]
    [InlineData("public Machine() { }")]
    public void WhenNoChainedConstructorSetsRequiredMembers_ThenGeneratedContextConstructorOmitsTheAttribute(
        string constructorDeclaration)
    {
        // Arrange (case RM2): both a generated and a plain declared parameterless constructor leave
        // the required member uninitialized, for the generator-emitted constructor as much as for
        // the caller.
        var source = $@"
using Namotion.Interceptor.Attributes;
namespace Repro
{{
    [InterceptorSubject]
    public partial class Machine
    {{
        public required partial string Name {{ get; set; }}

        {constructorDeclaration}
    }}
}}";

        // Act
        var generated = GeneratorTestHost.RunExpectingNoWarnings(source);

        // Assert: claiming the attribute here would tell the compiler the required member is
        // initialized when nothing initializes it, and every caller would lose the diagnostic.
        Assert.DoesNotContain("SetsRequiredMembers", generated.SingleSource());
        Assert.Contains("public Machine(IInterceptorSubjectContext context) : this()", generated.SingleSource());
    }
}
