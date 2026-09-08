using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Members on a subject's own chain that collide with the generated half: ones the emitted root-mode
/// members hide, ones that capture a call meant for inherited members, and ones that take an
/// IInterceptorSubject slot from the root.
/// </summary>
internal static class SubjectMemberConflicts
{
    /// <summary>
    /// The root-mode member names that need a 'new' modifier because the ancestor chain already
    /// exposes an accessible member the emitted one hides. This is C#'s hiding rule, not the
    /// contract's match: CS0108 fires for a same-name member of a DIFFERENT kind too, while a blanket
    /// 'new' produces CS0109 when nothing is hidden. Both are build errors under
    /// TreatWarningsAsErrors, so the modifier has to be decided per member.
    /// </summary>
    public static IReadOnlyList<string> FindHiddenInterceptionMembers(
        INamedTypeSymbol? baseType,
        INamedTypeSymbol subject,
        Compilation compilation,
        bool emitsNotifyPropertyChanged)
    {
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object)
        {
            return [];
        }

        var hidden = new List<string>();

        foreach (var accessorHelper in GeneratedMemberTable.AccessorHelpers)
        {
            var isHidden = SymbolExtensions.HidableMembers(baseType, subject, compilation, accessorHelper.Name)
                .Any(member => IsHiddenByEmittedMember(member, accessorHelper));

            if (isHidden)
            {
                hidden.Add(accessorHelper.Name);
            }
        }

        if (!emitsNotifyPropertyChanged)
        {
            return hidden;
        }

        // The emitted PropertyChanged is an event, and every member kind except a method hides by name
        // alone, so any inherited member of that name is hidden by it. A base that implements
        // INotifyPropertyChanged explicitly declares a private member, which the accessibility filter
        // drops, and that is correct: an explicit implementation neither hides nor is found by member
        // lookup.
        if (SymbolExtensions.HidableMembers(baseType, subject, compilation, MemberNames.PropertyChanged).Any())
        {
            hidden.Add(MemberNames.PropertyChanged);
        }

        // The emitted raise is a method, so only a member C#'s hiding rule really hides counts. A
        // RaisePropertyChanged(PropertyChangedEventArgs) overload hides nothing, and a 'new' for it
        // would be CS0109, which is a build error under TreatWarningsAsErrors just like the CS0108 it
        // is meant to prevent. Parameter types are compared here, unlike in AccessorHelpers, because
        // that overload is an ordinary shape on an MVVM base rather than a contrivance.
        var raiseIsHidden = SymbolExtensions.HidableMembers(baseType, subject, compilation, MemberNames.RaisePropertyChanged)
            .Any(member => member is not IMethodSymbol method || GeneratedMemberTable.HasRaisePropertyChangedParameters(method));

        if (raiseIsHidden)
        {
            hidden.Add(MemberNames.RaisePropertyChanged);
        }

        return hidden;
    }

    /// <summary>
    /// A method is hidden only when its signature matches the emitted one, so an unrelated overload of
    /// an interception member name hides nothing and must not attract a 'new'. Everything else hides by name
    /// alone: a base property or field named GetPropertyValue is hidden by the emitted method.
    /// </summary>
    private static bool IsHiddenByEmittedMember(ISymbol member, AccessorHelperShape accessorHelper)
    {
        if (member is not IMethodSymbol method)
        {
            return true;
        }

        return method.TypeParameters.Length == accessorHelper.TypeParameterCount &&
               method.Parameters.Length == accessorHelper.ParameterCount;
    }

    /// <summary>
    /// Members named like an inherited generated member. Deliberately name-only, any kind, no signature
    /// test, statics included: a 'new' member of the same shape captures the call with no diagnostic, an
    /// applicable overload of a different signature wins overload resolution without hiding anything,
    /// and C# hiding is not staticness-sensitive. On intermediate classes only members accessible from
    /// the subject count, since a private one neither hides nor binds.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHidingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        foreach (var type in EnumerateBetween(subject, contractProvider))
        {
            foreach (var name in GeneratedMemberTable.GeneratedMemberNames)
            {
                foreach (var member in type.GetMembers(name))
                {
                    if (!SymbolEqualityComparer.Default.Equals(type, subject) &&
                        !compilation.IsSymbolAccessibleWithin(member, subject))
                    {
                        continue;
                    }

                    yield return (type, name);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Public members, and explicit interface implementations, that would take an IInterceptorSubject
    /// slot from the root under interface re-implementation. Context is the severe one: hijacking it
    /// leaves the inherited helpers reading a context that is never populated, so interception stops
    /// silently and the unguarded IInterceptorExecutor casts in DynamicSubjectFactory and
    /// RegisteredSubject throw.
    /// </summary>
    /// <remarks>
    /// The walk runs the whole chain rather than stopping at the contract provider, because the
    /// provider is exactly where a hand-written hijacker sits. From the provider upward the report is
    /// conditional on <see cref="TakesSlotFromAbove"/>; below it that answer is always yes, and asking
    /// it there would go wrong for a provider generated in this compilation, whose symbol implements
    /// nothing yet. An override is skipped along with a static: it occupies the slot it already had.
    /// </remarks>
    public static IEnumerable<(INamedTypeSymbol Declarer, string MemberName)> FindHijackingMembers(
        INamedTypeSymbol subject,
        INamedTypeSymbol contractProvider,
        Compilation compilation)
    {
        var hijackableMembers = GeneratedMemberTable.GetHijackableInterfaceMembers(compilation);
        if (hijackableMembers.Length == 0)
        {
            yield break;
        }

        var isAtOrAboveContractProvider = false;

        foreach (var type in SymbolExtensions.EnumerateChain(subject))
        {
            isAtOrAboveContractProvider |= SymbolEqualityComparer.Default.Equals(type, contractProvider);

            foreach (var (name, interfaceMember) in hijackableMembers)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member.IsStatic || member.IsOverride)
                    {
                        continue;
                    }

                    var isPublicMatch = member.Name == name &&
                                        member.DeclaredAccessibility == Accessibility.Public &&
                                        IsImplicitImplementationOf(member, interfaceMember);

                    if (!isPublicMatch && !IsExplicitInterceptorSubjectImplementation(member, name))
                    {
                        continue;
                    }

                    if (isAtOrAboveContractProvider && !TakesSlotFromAbove(type, interfaceMember))
                    {
                        break;
                    }

                    yield return (type, name);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Whether a member declared at or above the contract provider really displaces an implementation
    /// its own base already provides. This is what keeps the ordinary shape quiet: a hand-written
    /// subject root derives from object, so there is nothing above it whose slot its members could take.
    /// </summary>
    /// <remarks>
    /// An explicit implementation is deliberately not exempt: CS0540 forces its declaring class to list
    /// the interface, which makes that class the contract provider, so the exemption fired on every base
    /// there is and hid the only form a hand-written ancestor can express.
    /// </remarks>
    private static bool TakesSlotFromAbove(INamedTypeSymbol declarer, ISymbol interfaceMember)
        => declarer.BaseType?.FindImplementationForInterfaceMember(interfaceMember) is not null;

    /// <summary>
    /// Whether a public member really is an implicit implementation of the interface member, which is
    /// what taking the slot requires. Name alone is not enough and reporting on it is a hard break: a
    /// partial string Data, a get-only object Data and a bool-returning AddProperties all compile, keep
    /// the root's implementations, and are perfectly ordinary names on a domain model.
    /// </summary>
    private static bool IsImplicitImplementationOf(ISymbol member, ISymbol interfaceMember)
    {
        switch (interfaceMember)
        {
            case IPropertySymbol interfaceProperty:
                return member is IPropertySymbol property &&
                       SymbolEqualityComparer.Default.Equals(property.Type, interfaceProperty.Type) &&
                       ParametersMatch(property.Parameters, interfaceProperty.Parameters) &&
                       (interfaceProperty.GetMethod is null || IsPubliclyCallable(property.GetMethod)) &&
                       (interfaceProperty.SetMethod is null || IsPubliclyCallable(property.SetMethod));

            case IMethodSymbol interfaceMethod:
                return member is IMethodSymbol method &&
                       SymbolEqualityComparer.Default.Equals(method.ReturnType, interfaceMethod.ReturnType) &&
                       method.TypeParameters.Length == interfaceMethod.TypeParameters.Length &&
                       ParametersMatch(method.Parameters, interfaceMethod.Parameters);

            default:
                return false;
        }
    }

    /// <summary>
    /// An accessor only implements an interface accessor when it is itself public: a
    /// "public string P { get; private set; }" does not implement a settable interface property.
    /// </summary>
    private static bool IsPubliclyCallable(IMethodSymbol? accessor)
    {
        return accessor is { DeclaredAccessibility: Accessibility.Public };
    }

    /// <summary>
    /// The 'params' modifier is deliberately not compared: it is not part of the signature, so a plain
    /// IEnumerable parameter still implements a params one.
    /// </summary>
    private static bool ParametersMatch(
        ImmutableArray<IParameterSymbol> candidate,
        ImmutableArray<IParameterSymbol> required)
    {
        if (candidate.Length != required.Length)
        {
            return false;
        }

        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index].RefKind != required[index].RefKind ||
                !SymbolEqualityComparer.Default.Equals(candidate[index].Type, required[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExplicitInterceptorSubjectImplementation(ISymbol member, string name)
    {
        var explicitProperty = (member as IPropertySymbol)?.ExplicitInterfaceImplementations.FirstOrDefault();
        var explicitMethod = (member as IMethodSymbol)?.ExplicitInterfaceImplementations.FirstOrDefault();
        var implemented = (ISymbol?)explicitProperty ?? explicitMethod;

        return implemented is not null &&
               implemented.Name == name &&
               implemented.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == KnownTypes.IInterceptorSubject;
    }

    /// <summary>
    /// The subject and every class between it and the class providing the contract member, which is
    /// where a capturing member can sit. The provider itself is excluded, because the members it
    /// declares are the very ones the contract check demanded of it: reporting them would fire on every
    /// conforming base. <see cref="FindHijackingMembers"/> is not bounded this way, because a member at
    /// or above the provider can still take an interface slot from something above it.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateBetween(INamedTypeSymbol subject, INamedTypeSymbol provider)
    {
        for (var current = subject;
             current is { SpecialType: not SpecialType.System_Object } &&
             !SymbolEqualityComparer.Default.Equals(current, provider);
             current = current.BaseType!)
        {
            yield return current;
        }
    }
}
