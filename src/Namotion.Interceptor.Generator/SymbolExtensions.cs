using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Namotion.Interceptor.Generator;

internal static class SymbolExtensions
{
    public static bool HasAttribute(
        SyntaxList<AttributeListSyntax> attributeLists,
        string baseTypeName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        return attributeLists
            .SelectMany(al => al.Attributes)
            .Any(attribute =>
            {
                var attributeType = semanticModel.GetTypeInfo(attribute, cancellationToken).Type as INamedTypeSymbol;
                return attributeType is not null && IsTypeOrInheritsFrom(attributeType, baseTypeName);
            });
    }

    /// <summary>
    /// Whether the type implements the named interface, including through a base class and through
    /// interface inheritance. AllInterfaces already covers both, so no recursion is needed.
    /// </summary>
    public static bool ImplementsInterface(ITypeSymbol? type, string interfaceTypeName)
    {
        if (type is null)
        {
            return false;
        }

        if (type.TypeKind == TypeKind.Interface &&
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName)
        {
            return true;
        }

        return type.AllInterfaces.Any(i =>
            i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == interfaceTypeName);
    }

    public static bool IsTypeOrInheritsFrom(ITypeSymbol? type, string fullTypeName)
    {
        while (type is not null)
        {
            if (type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == fullTypeName)
            {
                return true;
            }
            type = type.BaseType;
        }
        return false;
    }

    /// <summary>
    /// The type and every class above it, stopping before object.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateChain(INamedTypeSymbol? type)
    {
        for (var current = type; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
        {
            yield return current;
        }
    }

    /// <summary>
    /// The members of a given name that member lookup from <paramref name="accessingType"/> would find
    /// on the chain starting at <paramref name="baseType"/>. Statics are dropped because none of the
    /// call sites the generator emits can reach one, and inaccessible members because they neither
    /// hide nor bind. Contrast <see cref="HidableMembers"/>, which must see statics.
    /// </summary>
    public static IEnumerable<ISymbol> AccessibleMembers(
        INamedTypeSymbol baseType,
        INamedTypeSymbol accessingType,
        Compilation compilation,
        string name)
        => EnumerateChain(baseType)
            .SelectMany(type => type.GetMembers(name))
            .Where(member => !member.IsStatic && compilation.IsSymbolAccessibleWithin(member, accessingType));

    /// <summary>
    /// The members of a given name on the base chain that a member emitted into
    /// <paramref name="accessingType"/> can hide. Same as <see cref="AccessibleMembers"/> except that
    /// statics are kept: C# hiding is not staticness-sensitive, so a static base member of one of those names is hidden by the emitted instance member and produces the same CS0108 an instance one
    /// would. Accessibility still applies, because an inaccessible member is neither hidden nor found
    /// by member lookup.
    /// </summary>
    public static IEnumerable<ISymbol> HidableMembers(
        INamedTypeSymbol baseType,
        INamedTypeSymbol accessingType,
        Compilation compilation,
        string name)
        => EnumerateChain(baseType)
            .SelectMany(type => type.GetMembers(name))
            .Where(member => compilation.IsSymbolAccessibleWithin(member, accessingType));
}
