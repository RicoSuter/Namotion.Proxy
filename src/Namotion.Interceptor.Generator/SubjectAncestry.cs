using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Which class above a subject is itself a subject, whether it will really receive generated
/// interception members, and whether the chain already carries the INotifyPropertyChanged half of it.
/// </summary>
internal static class SubjectAncestry
{
    /// <summary>
    /// The first ancestor that is a subject, skipping ordinary classes in between. Plain classes
    /// between two subjects are common enough to matter and reading the immediate base instead makes
    /// the generator emit a second copy of everything it already inherited.
    /// </summary>
    public static INamedTypeSymbol? FindNearestSubjectAncestor(INamedTypeSymbol typeSymbol)
        => SymbolExtensions.EnumerateChain(typeSymbol.BaseType)
            .FirstOrDefault(ancestor =>
                HasInterceptorSubjectAttribute(ancestor) || DeclaresInterceptorSubject(ancestor));

    public static bool HasInterceptorSubjectAttribute(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        return type
            .GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.InterceptorSubjectAttribute));
    }

    /// <summary>
    /// Whether the type itself declares IInterceptorSubject, directly or through an interface it
    /// declares. Deliberately not AllInterfaces and deliberately no BaseType recursion: those report
    /// interfaces inherited from a base class, which would stop the ancestor walk at a plain
    /// intermediate whenever the real subject ancestor comes from a metadata reference, that is, in
    /// every cross-assembly hierarchy.
    /// </summary>
    private static bool DeclaresInterceptorSubject(INamedTypeSymbol type)
    {
        return type.Interfaces.Any(declared =>
            IsInterceptorSubject(declared) || declared.AllInterfaces.Any(IsInterceptorSubject));
    }

    private static bool IsInterceptorSubject(INamedTypeSymbol interfaceType)
    {
        return interfaceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }

    /// <summary>
    /// Whether any ancestor, not only the nearest subject one, is an in-source subject that will
    /// actually receive generated interception members. Asked of the whole chain because a hand-written class in
    /// between is exactly what pushes this subject back into root mode, and the generated ancestor
    /// above it still owns the members this one is about to re-emit.
    /// </summary>
    public static bool HasGeneratedSubjectAncestor(INamedTypeSymbol typeSymbol, CancellationToken cancellationToken)
        => SymbolExtensions.EnumerateChain(typeSymbol.BaseType)
            .Any(ancestor => HasInterceptorSubjectAttribute(ancestor) &&
                             WillBeGeneratedInThisCompilation(ancestor, cancellationToken));

    /// <summary>
    /// Whether an attributed ancestor declared in this compilation will actually receive generated
    /// interception members. Carrying the attribute is not enough: NI0001 suppresses generation for a subject that
    /// is not partial.
    /// </summary>
    public static bool WillBeGeneratedInThisCompilation(INamedTypeSymbol ancestor, CancellationToken cancellationToken)
    {
        if (ancestor.DeclaringSyntaxReferences.Length == 0)
        {
            return false;
        }

        foreach (var syntaxReference in ancestor.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not ClassDeclarationSyntax declaration ||
                !declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the class chain above the type already provides the INotifyPropertyChanged members, so
    /// the subject must not declare its own.
    /// </summary>
    /// <remarks>
    /// The interface clause is deliberately asked of the TYPE, not of its subject ancestor: a base
    /// implementing IRaisePropertyChanged by hand is not a subject ancestor at all, and dropping this
    /// makes its subclass re-declare those members (ManualInpcPersonBase in
    /// Namotion.Interceptor.Tracking.Tests, live test). The attribute alone is a promise, not evidence.
    /// </remarks>
    public static bool InheritsNotifyPropertyChanged(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (SymbolExtensions.ImplementsInterface(typeSymbol, KnownTypes.IRaisePropertyChanged))
        {
            return true;
        }

        var subjectAncestor = FindNearestSubjectAncestor(typeSymbol);
        if (subjectAncestor is null || !HasInterceptorSubjectAttribute(subjectAncestor))
        {
            return false;
        }

        // An ancestor generated in this compilation implements IRaisePropertyChanged in code that does
        // not exist as a symbol yet, and one built by an older generator may expose the raise as a
        // plain member without the interface.
        return WillBeGeneratedInThisCompilation(subjectAncestor, cancellationToken) ||
               HasCallableRaisePropertyChanged(typeSymbol, compilation, cancellationToken);
    }

    /// <summary>
    /// Whether a simple-name RaisePropertyChanged(name) call in the type's own body binds to a member
    /// above it. Emitting that form when it does not is CS0103 inside a generated file the consumer
    /// cannot edit, which is why the emitter falls back to the interface form.
    /// </summary>
    /// <remarks>
    /// The whole chain is walked, not just the nearest subject ancestor: a generated ancestor emits no
    /// raise of its own when its own base already provided those members, so the member that answers the
    /// call can sit several classes further up. An explicit interface implementation is not found here
    /// and must not be: its name is qualified and it is private, so no simple-name call can reach it.
    /// </remarks>
    public static bool HasCallableRaisePropertyChanged(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (typeSymbol.BaseType is not null &&
            SymbolExtensions.AccessibleMembers(typeSymbol.BaseType, typeSymbol, compilation, MemberNames.RaisePropertyChanged)
                .OfType<IMethodSymbol>()
                .Any(GeneratedMemberTable.HasRaisePropertyChangedParameters))
        {
            return true;
        }

        // An ancestor generated in this compilation has no member symbol yet. It emits one exactly
        // when nothing above it provides them, which is the same question asked here.
        return SymbolExtensions.EnumerateChain(typeSymbol.BaseType).Any(ancestor =>
            HasInterceptorSubjectAttribute(ancestor) &&
            WillBeGeneratedInThisCompilation(ancestor, cancellationToken) &&
            !InheritsNotifyPropertyChanged(ancestor, compilation, cancellationToken));
    }
}
