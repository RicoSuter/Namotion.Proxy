using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Everything the generator needs to know about the class a subject inherits from: which ancestor
/// owns the shared interception members, and whether that ancestor exposes enough of it to be inherited from.
/// </summary>
internal static class SubjectBaseContract
{
    /// <summary>
    /// Resolves everything the emitter needs to know about the base class, and reports NI0011 to
    /// NI0014. A null result means generation is suppressed and the caller must emit nothing.
    /// </summary>
    public static SubjectBaseClass? Resolve(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // Resolved from the symbol, not from the attributed declaration's base list: the base list
        // may sit on a partial declaration other than the attributed one, and the symbol's BaseType
        // chain is strictly base classes, so an interface in the base list is never mistaken for one.
        var subjectAncestor = SubjectAncestry.FindNearestSubjectAncestor(typeSymbol);

        var baseClassTypeName = subjectAncestor?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var baseClassHasInterceptorSubject = SubjectAncestry.HasInterceptorSubjectAttribute(subjectAncestor);

        var baseClassHasInpc = SubjectAncestry.InheritsNotifyPropertyChanged(typeSymbol, compilation, cancellationToken);
        var hasCallableRaisePropertyChanged = SubjectAncestry.HasCallableRaisePropertyChanged(typeSymbol, compilation, cancellationToken);

        // Root mode emits the whole IInterceptorSubject block; derived mode emits only its own
        // Properties line. Asked of the NEAREST subject ancestor, never of "some ancestor".
        var emitsInterceptionMembers = true;
        IReadOnlyList<string> hiddenMembers = [];

        if (subjectAncestor is not null)
        {
            // An ancestor generated in this very compilation cannot be contract-checked: its
            // interception members live in source the generator has not emitted yet, so the symbol shows none
            // of it.
            var ancestorIsGeneratedHere =
                baseClassHasInterceptorSubject &&
                SubjectAncestry.WillBeGeneratedInThisCompilation(subjectAncestor, cancellationToken);

            if (ancestorIsGeneratedHere ||
                SatisfiesContract(subjectAncestor, typeSymbol, compilation, out var missingMembers))
            {
                emitsInterceptionMembers = false;

                foreach (var (declarer, memberName) in SubjectMemberConflicts.FindHidingMembers(typeSymbol, subjectAncestor, compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HidesGeneratedMember, location, declarer.ToDisplayString(), memberName));
                }

                foreach (var (declarer, memberName) in SubjectMemberConflicts.FindHijackingMembers(typeSymbol, subjectAncestor, compilation))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.HijacksInterfaceImplementation, location, declarer.ToDisplayString(), memberName));
                }
            }
            else if (HasUsableDefaultProperties(subjectAncestor, typeSymbol, compilation))
            {
                // Only emitsInterceptionMembers flips. The ancestor stays the base-class fact source, so
                // DefaultProperties still concatenates with it and the INPC decision is unchanged.
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BaseInterceptionMembersCannotBeShared,
                    location,
                    subjectAncestor.ToDisplayString(),
                    typeSymbol.ToDisplayString(),
                    string.Join(", ", missingMembers)));
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.BaseDoesNotSatisfyContract,
                    location,
                    subjectAncestor.ToDisplayString(),
                    string.Join(", ", missingMembers)));

                return null;
            }
        }

        // Asked of every root-mode subject with a base class, not only the NI0012 one: an MVVM base
        // carrying PropertyChanged and RaisePropertyChanged collides just as well. The walk starts at
        // the immediate base because hiding is decided against the nearest declaration of the name
        // anywhere above; a generated ancestor has no symbol yet, hence the table lookup.
        if (emitsInterceptionMembers)
        {
            hiddenMembers = SubjectAncestry.HasGeneratedSubjectAncestor(typeSymbol, cancellationToken)
                ? GeneratedMemberTable.RootModeMemberNames
                : SubjectMemberConflicts.FindHiddenInterceptionMembers(typeSymbol.BaseType, typeSymbol, compilation, !baseClassHasInpc);
        }

        return new SubjectBaseClass(
            baseClassTypeName,
            baseClassHasInterceptorSubject,
            baseClassHasInpc,
            hasCallableRaisePropertyChanged,
            emitsInterceptionMembers,
            hiddenMembers);
    }

    /// <summary>
    /// The members a class must expose to host a generated subclass, tabulated in
    /// docs/generator.md. Generated root mode satisfies it by construction.
    /// </summary>
    /// <remarks>
    /// Only the <see cref="KnownTypes.IRaisePropertyChanged"/> clause is not required for the generated
    /// code to compile; failing it costs that shape derived mode, not the build.
    /// </remarks>
    private static bool SatisfiesContract(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        out IReadOnlyList<string> missingMembers)
    {
        var missing = new List<string>();

        if (!SymbolExtensions.ImplementsInterface(ancestor, KnownTypes.IInterceptorSubject))
        {
            missing.Add(KnownTypes.IInterceptorSubject);
        }

        if (!SymbolExtensions.ImplementsInterface(ancestor, KnownTypes.IRaisePropertyChanged) &&
            !SymbolExtensions.ImplementsInterface(subject, KnownTypes.IRaisePropertyChanged))
        {
            missing.Add(KnownTypes.IRaisePropertyChanged);
        }

        foreach (var accessorHelper in GeneratedMemberTable.AccessorHelpers)
        {
            if (!HasAccessibleMethod(ancestor, subject, compilation, accessorHelper))
            {
                missing.Add(accessorHelper.Declaration);
            }
        }

        if (!HasUsableDefaultProperties(ancestor, subject, compilation))
        {
            missing.Add("public static IReadOnlyDictionary<string, SubjectPropertyMetadata> DefaultProperties");
        }

        missingMembers = missing;
        return missing.Count == 0;
    }

    /// <summary>
    /// A static DefaultProperties, field or property, that is accessible and of a type the emitted
    /// .Concat(...) accepts. Compared against the constructed symbol rather than a display string,
    /// because the wrong type produces CS1929 in a generated file. See docs/generator.md.
    /// </summary>
    private static bool HasUsableDefaultProperties(INamedTypeSymbol ancestor, INamedTypeSymbol subject, Compilation compilation)
    {
        var expectedType = GetPropertyMetadataDictionaryType(compilation);
        if (expectedType is null)
        {
            return false;
        }

        foreach (var candidate in SymbolExtensions.EnumerateChain(ancestor))
        {
            foreach (var member in candidate.GetMembers(MemberNames.DefaultProperties))
            {
                var memberType = member switch
                {
                    IPropertySymbol property => property.Type,
                    IFieldSymbol field => field.Type,
                    _ => null
                };

                if (memberType is null ||
                    !member.IsStatic ||
                    !compilation.IsSymbolAccessibleWithin(member, subject))
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(memberType, expectedType) ||
                    memberType.AllInterfaces.Contains(expectedType, SymbolEqualityComparer.Default))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// IReadOnlyDictionary&lt;string, SubjectPropertyMetadata&gt; as the compilation sees it, or null
    /// when either half is unreferenced, in which case nothing the generator emits would compile
    /// anyway.
    /// </summary>
    private static INamedTypeSymbol? GetPropertyMetadataDictionaryType(Compilation compilation)
    {
        var dictionaryType = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2");
        var propertyMetadataType = compilation.GetTypeByMetadataName(KnownTypes.SubjectPropertyMetadata);

        if (dictionaryType is null || propertyMetadataType is null)
        {
            return null;
        }

        return dictionaryType.Construct(compilation.GetSpecialType(SpecialType.System_String), propertyMetadataType);
    }

    private static bool HasAccessibleMethod(
        INamedTypeSymbol ancestor,
        INamedTypeSymbol subject,
        Compilation compilation,
        AccessorHelperShape accessorHelper)
        => SymbolExtensions.AccessibleMembers(ancestor, subject, compilation, accessorHelper.Name)
            .OfType<IMethodSymbol>()
            .Any(method =>
                method.TypeParameters.Length == accessorHelper.TypeParameterCount &&
                method.Parameters.Length == accessorHelper.ParameterCount &&
                (!accessorHelper.RequiresParameterArray ||
                 method.Parameters[method.Parameters.Length - 1].IsParams) &&
                (!accessorHelper.RequiresLeadingString ||
                 method.Parameters[0].Type.SpecialType == SpecialType.System_String) &&
                HasExpectedReturnType(method, accessorHelper, compilation));

    /// <summary>
    /// Whether the base helper returns what the generated call sites consume. Nullability annotations
    /// are deliberately not compared: the emitted code accepts both forms, and a base compiled without
    /// a nullable context would otherwise fail the contract for no reason.
    /// </summary>
    /// <remarks>
    /// The dictionary case additionally requires a reference type, unlike
    /// <see cref="HasUsableDefaultProperties"/>: this side is the left operand of '??', which rejects a
    /// value type with CS0019, while the other side only feeds .Concat.
    /// </remarks>
    private static bool HasExpectedReturnType(
        IMethodSymbol method,
        AccessorHelperShape accessorHelper,
        Compilation compilation)
    {
        switch (accessorHelper.ReturnKind)
        {
            case AccessorHelperReturnKind.OwnTypeParameter:
                return SymbolEqualityComparer.Default.Equals(method.ReturnType, method.TypeParameters[0]);

            case AccessorHelperReturnKind.Boolean:
                return method.ReturnType.SpecialType == SpecialType.System_Boolean;

            case AccessorHelperReturnKind.Object:
                return method.ReturnType.SpecialType == SpecialType.System_Object;

            default:
                var expectedType = GetPropertyMetadataDictionaryType(compilation);
                return expectedType is not null &&
                       method.ReturnType.IsReferenceType &&
                       (SymbolEqualityComparer.Default.Equals(method.ReturnType, expectedType) ||
                        method.ReturnType.AllInterfaces.Contains(expectedType, SymbolEqualityComparer.Default));
        }
    }
}
