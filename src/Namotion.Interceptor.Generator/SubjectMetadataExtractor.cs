using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Namotion.Interceptor.Generator.Models;

namespace Namotion.Interceptor.Generator;

internal static class SubjectMetadataExtractor
{
    private const string InterceptedMethodPostfix = "WithoutInterceptor";

    /// <summary>
    /// Extracts metadata from a type declaration with the InterceptorSubject attribute.
    /// </summary>
    public static ExtractionResult Extract(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        var location = typeDeclaration.Identifier.GetLocation();

        // Guards run fundamentally-unsupported shapes before fixable ones, so a subject with more
        // than one problem points the user at the one they cannot work around by adding a modifier.
        if (typeDeclaration is not ClassDeclarationSyntax)
        {
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.UnsupportedTypeKind, location,
                typeSymbol.Name, typeDeclaration.Keyword.ValueText));
            return new ExtractionResult(null, diagnostics);
        }

        if (typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.FileTypeNotSupported, location, typeSymbol.Name));
            return new ExtractionResult(null, diagnostics);
        }

        // Arity, not IsGenericType: Roslyn reports IsGenericType = true for a non-generic type
        // nested inside a generic one, which would misname the subject here as generic when it is
        // really the containing type (checked below) that carries the type parameters.
        if (typeSymbol.Arity > 0)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.GenericTypeNotSupported, location, typeSymbol.Name));
            return new ExtractionResult(null, diagnostics);
        }

        for (var parent = typeDeclaration.Parent; parent is TypeDeclarationSyntax outer; parent = parent.Parent)
        {
            if (outer.TypeParameterList is not null)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.GenericContainingTypeNotSupported, location,
                    typeSymbol.Name, outer.Identifier.ValueText));
                return new ExtractionResult(null, diagnostics);
            }
        }

        if (!typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NotPartial, location, typeSymbol.Name));
            return new ExtractionResult(null, diagnostics);
        }

        for (var parent = typeDeclaration.Parent; parent is TypeDeclarationSyntax outer; parent = parent.Parent)
        {
            if (!outer.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.ContainingTypeNotPartial, location,
                    outer.Identifier.ValueText, typeSymbol.Name));
                return new ExtractionResult(null, diagnostics);
            }
        }

        // Text rather than ValueText: ValueText drops the '@' that escapes a keyword used as a
        // name, and the bare keyword does not parse. The name is re-emitted as the partial
        // declaration, as every cast and as each generated constructor's name. Property names
        // below stay on ValueText, because they are also emitted as string literals, as nameof
        // arguments and as parts of derived names such as _Name and OnNameChanged, where the
        // escape would be wrong.
        var className = typeDeclaration.Identifier.Text;

        // Use the symbol rather than the syntax modifiers: a top-level class without a modifier
        // defaults to internal, a nested one to private.
        var accessModifier = GetAccessModifierFromAccessibility(typeSymbol.DeclaredAccessibility);

        // From the symbol, because 'sealed' may sit on any partial declaration, not necessarily
        // the attributed one. DetectConstructorState already scans every declaration for the same
        // reason.
        var isSealed = typeSymbol.IsSealed;

        var containingTypes = GetContainingTypes(typeDeclaration);
        var namespaceName = GetNamespace(typeDeclaration);
        var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var baseClass = SubjectBaseContract.Resolve(
            typeSymbol, semanticModel.Compilation, location, diagnostics, cancellationToken);

        if (baseClass is null)
        {
            return new ExtractionResult(null, diagnostics);
        }

        // Collect all partial type declarations
        var allTypeDeclarations = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<TypeDeclarationSyntax>()
            .ToArray();

        // Collect properties from all partial declarations
        var classProperties = DeduplicateByName(
            CollectProperties(typeSymbol, semanticModel, location, diagnostics, cancellationToken),
            typeSymbol.ToDisplayString(),
            location,
            diagnostics);

        ReportPropertiesShadowingABaseImplementation(typeSymbol, classProperties, location, diagnostics);

        // Collect interface properties with default implementations
        var interfaceProperties = ExtractInterfaceDefaultProperties(
            typeSymbol, classProperties, semanticModel.Compilation, location, diagnostics);

        // Combine class properties with interface default properties
        var properties = classProperties.Concat(interfaceProperties).ToList();

        // Collect methods from all partial declarations
        var methods = CollectMethods(typeSymbol, semanticModel, location, diagnostics, cancellationToken);

        // Detect constructor state
        var (needsGeneratedParameterlessConstructor, hasOrWillHaveParameterlessConstructor,
            parameterlessConstructorSetsRequiredMembers) = DetectConstructorState(typeSymbol, allTypeDeclarations);

        var constructors = CollectConstructors(allTypeDeclarations, semanticModel, cancellationToken);

        return new ExtractionResult(
            new SubjectMetadata(
                className,
                accessModifier,
                isSealed,
                namespaceName,
                fullTypeName,
                containingTypes,
                needsGeneratedParameterlessConstructor,
                hasOrWillHaveParameterlessConstructor,
                parameterlessConstructorSetsRequiredMembers,
                constructors,
                baseClass,
                properties,
                methods),
            diagnostics);
    }

    private static string? GetNamespace(TypeDeclarationSyntax typeDeclaration)
    {
        // Walk up past containing types to find namespace
        SyntaxNode? current = typeDeclaration.Parent;
        while (current is TypeDeclarationSyntax)
        {
            current = current.Parent;
        }

        // null means the global namespace: the generated file must not declare one.
        return (current as NamespaceDeclarationSyntax)?.Name.ToString() ??
               (current as FileScopedNamespaceDeclarationSyntax)?.Name.ToString();
    }

    private static ContainingType[] GetContainingTypes(SyntaxNode node)
    {
        var types = new List<ContainingType>();
        var parent = node.Parent;
        while (parent is TypeDeclarationSyntax typeDeclaration)
        {
            // Text keeps the escape on a verbatim identifier: the generated file reopens every
            // containing type by name.
            types.Insert(0, new ContainingType(
                GetTypeKeyword(typeDeclaration),
                typeDeclaration.Identifier.Text));
            parent = parent.Parent;
        }
        return types.ToArray();
    }

    /// <summary>
    /// "record" alone is correct for a record class, because record defaults to a class, but a
    /// record struct needs both tokens or the partial declarations conflict.
    /// </summary>
    private static string GetTypeKeyword(TypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration is not RecordDeclarationSyntax recordDeclaration)
        {
            return typeDeclaration.Keyword.ValueText;
        }

        var classOrStructKeyword = recordDeclaration.ClassOrStructKeyword.ValueText;
        return string.IsNullOrEmpty(classOrStructKeyword)
            ? recordDeclaration.Keyword.ValueText
            : $"{recordDeclaration.Keyword.ValueText} {classOrStructKeyword}";
    }

    /// <summary>
    /// The shape guard a property must pass to become a subject property, shared between a
    /// property declared in the class body and one adopted from an interface default
    /// implementation: an indexer has no usable name and is parameterised, and a static member
    /// cannot be read from an instance (the emitted accessor lambda always takes an instance).
    /// Kept as one rule both paths consult, rather than one each, because the two paths have
    /// already drifted apart once before, on accessibility.
    /// </summary>
    /// <remarks>
    /// Neither shape is reported: NI0006 speaks to a member that could plausibly have become a
    /// subject property and did not, and neither an indexer nor a static member was ever a
    /// candidate. A class-declared indexer has always been ignored in silence (it parses as
    /// <c>IndexerDeclarationSyntax</c>, which the property filter excludes before this guard runs),
    /// so reporting the interface-default form was also an inconsistency between the two paths.
    /// This rule outranks the explicit-implementation opt-in below: a <c>static abstract</c>
    /// interface member forces a static implementation on the subject, and no edit the author can
    /// make would turn it into a property.
    /// </remarks>
    private static bool IsNeverASubjectProperty(IPropertySymbol property)
    {
        return property.IsIndexer || property.IsStatic;
    }

    private static IReadOnlyList<PropertyMetadata> CollectProperties(
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var properties = new List<PropertyMetadata>();

        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken);
            if (declaration is not TypeDeclarationSyntax typeDeclarationSyntax)
            {
                continue;
            }

            var declarationModel = semanticModel.Compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree);

            foreach (var property in typeDeclarationSyntax.Members.OfType<PropertyDeclarationSyntax>())
            {
                var typeInfo = declarationModel.GetTypeInfo(property.Type, cancellationToken);
                var fullyQualifiedName = typeInfo.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "object";
                var propertyName = property.Identifier.ValueText;

                // Resolved once and reused below for the explicit-implementation accessibility
                // check: an indexer cannot reach this path (it parses as IndexerDeclarationSyntax,
                // which the PropertyDeclarationSyntax filter above already excludes), but a static
                // property can, and the same rule that skips one on the interface-default path must
                // skip it here too, or it emits a cast-through-the-class accessor that fails CS0176.
                var declaredPropertySymbol = declarationModel.GetDeclaredSymbol(property, cancellationToken);
                if (declaredPropertySymbol is not null && IsNeverASubjectProperty(declaredPropertySymbol))
                {
                    continue;
                }

                var explicitInterfaceTypeName = property.ExplicitInterfaceSpecifier is { } explicitSpecifier
                    ? declarationModel
                        .GetTypeInfo(explicitSpecifier.Name, cancellationToken)
                        .Type?
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null;

                var accessModifier = GetAccessModifier(property.Modifiers);
                var isPartial = property.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) &&
                                property.ExplicitInterfaceSpecifier is null;
                var isVirtual = property.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
                var isOverride = property.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));
                var isNew = property.Modifiers.Any(m => m.IsKind(SyntaxKind.NewKeyword));
                var isSealed = property.Modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword));
                var isDerived = HasDerivedAttribute(property, declarationModel, cancellationToken);
                var isRequired = property.Modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword));

                var hasGetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true ||
                                property.ExpressionBody != null;
                var hasSetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
                var hasInit = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

                var getterAccessModifier = GetAccessorModifier(property.AccessorList, SyntaxKind.GetAccessorDeclaration);
                var setterAccessModifier = GetAccessorModifier(property.AccessorList, SyntaxKind.SetAccessorDeclaration) ??
                                           GetAccessorModifier(property.AccessorList, SyntaxKind.InitAccessorDeclaration);

                // A class-declared explicit interface implementation is reached through the same
                // cast-through-the-interface pattern as an interface default property, so the
                // implemented member's accessibility governs reachability here too, not the
                // (always-effectively-private) accessibility of the implementation itself.
                if (property.ExplicitInterfaceSpecifier is not null)
                {
                    var implementedMember = declaredPropertySymbol?.ExplicitInterfaceImplementations.FirstOrDefault();
                    if (implementedMember is not null)
                    {
                        var (isGetterAccessible, isSetterAccessible) = GetAccessorAccessibility(
                            semanticModel.Compilation, implementedMember, typeSymbol, implementedMember.ContainingType);
                        // Reported, unlike the same outcome on the interface-default path: writing
                        // an explicit implementation on the subject itself is an opt-in to it
                        // becoming a subject property, in the author's own file, and silently
                        // dropping it would leave them with no way to find out.
                        if (!isGetterAccessible && !isSetterAccessible)
                        {
                            diagnostics.Add(Diagnostic.Create(
                                Diagnostics.MemberSkipped, location,
                                $"{typeSymbol.Name}.{implementedMember.ContainingType.Name}.{implementedMember.Name}",
                                "the member is not accessible from generated code",
                                "declare an accessor the subject's generated half can reach"));
                            continue;
                        }

                        // The emitted metadata reflects the interface member's PropertyInfo, not
                        // this declaration's, so anything declared here never reaches the runtime.
                        if (property.AttributeLists.Count > 0)
                        {
                            diagnostics.Add(Diagnostic.Create(
                                Diagnostics.ExplicitImplementationAttributesIgnored, location, propertyName));
                        }

                        hasGetter = hasGetter && isGetterAccessible;
                        hasSetter = hasSetter && isSetterAccessible;
                        hasInit = hasInit && isSetterAccessible;
                    }
                }

                properties.Add(new PropertyMetadata(
                    propertyName,
                    fullyQualifiedName,
                    accessModifier,
                    isPartial,
                    isVirtual,
                    isOverride,
                    isNew,
                    isSealed,
                    isDerived,
                    isRequired,
                    hasGetter,
                    hasSetter,
                    hasInit,
                    IsFromInterface: false,
                    getterAccessModifier,
                    setterAccessModifier,
                    InterfaceTypeName: null,
                    ExplicitInterfaceTypeName: explicitInterfaceTypeName));
            }
        }

        return properties;
    }

    /// <summary>
    /// Two declarations can share a name when a class declares a property and also explicitly
    /// implements the same interface member. Emitting both produces duplicate dictionary keys,
    /// so the non-explicit declaration wins, matching what the runtime resolves.
    /// </summary>
    private static IReadOnlyList<PropertyMetadata> DeduplicateByName(
        IReadOnlyList<PropertyMetadata> properties,
        string subjectDisplayName,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var result = new List<PropertyMetadata>();
        var indexByName = new Dictionary<string, int>();
        var explicitImplementationsByName = new Dictionary<string, List<PropertyMetadata>>();

        foreach (var property in properties)
        {
            if (property.ExplicitInterfaceTypeName is not null)
            {
                if (!explicitImplementationsByName.TryGetValue(property.Name, out var explicitImplementations))
                {
                    explicitImplementations = new List<PropertyMetadata>();
                    explicitImplementationsByName[property.Name] = explicitImplementations;
                }

                explicitImplementations.Add(property);
            }

            if (!indexByName.TryGetValue(property.Name, out var index))
            {
                indexByName[property.Name] = result.Count;
                result.Add(property);
                continue;
            }

            if (result[index].ExplicitInterfaceTypeName is not null &&
                property.ExplicitInterfaceTypeName is null)
            {
                result[index] = property;
            }
        }

        // Reported only once every declaration has been seen, because the winner the message names
        // is not knowable mid-loop: a class-declared property takes the name whether it is written
        // before or after the explicit implementations. Iterating the deduplicated result rather
        // than the dictionary keeps the diagnostic order deterministic.
        foreach (var winner in result)
        {
            // Two explicit implementations of one simple name (typically one generic interface at
            // two instantiations) is the class-declared form of the NI0008 collision: whatever
            // claims the name, at least one interface member is dropped. A class property colliding
            // with a single explicit implementation is not: only one of the two comes from an
            // interface, and the class property is the documented winner.
            if (!explicitImplementationsByName.TryGetValue(winner.Name, out var explicitImplementations) ||
                explicitImplementations.Count < 2)
            {
                continue;
            }

            var winnerDescription = winner.ExplicitInterfaceTypeName is not null
                ? DescribeExplicitImplementation(winner)
                : $"the class property {subjectDisplayName}.{winner.Name}";

            foreach (var droppedProperty in explicitImplementations)
            {
                if (ReferenceEquals(droppedProperty, winner))
                {
                    continue;
                }

                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.PropertyNameCollision, location,
                    winner.Name, winnerDescription, DescribeExplicitImplementation(droppedProperty)));
            }
        }

        return result;
    }

    /// <summary>
    /// Names an explicitly implemented member the way a compiler error message would, so the
    /// "global::" the emitter needs in generated code does not leak into a diagnostic.
    /// </summary>
    private static string DescribeExplicitImplementation(PropertyMetadata property)
    {
        const string globalPrefix = "global::";

        var interfaceTypeName = property.ExplicitInterfaceTypeName!;
        if (interfaceTypeName.StartsWith(globalPrefix))
        {
            interfaceTypeName = interfaceTypeName.Substring(globalPrefix.Length);
        }

        return $"{interfaceTypeName}.{property.Name}";
    }

    private static IReadOnlyList<MethodMetadata> CollectMethods(
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        Location location,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var methods = new List<MethodMetadata>();

        foreach (var syntaxReference in typeSymbol.DeclaringSyntaxReferences)
        {
            var declaration = syntaxReference.GetSyntax(cancellationToken);
            if (declaration is not TypeDeclarationSyntax typeDeclarationSyntax)
            {
                continue;
            }

            var declarationModel = semanticModel.Compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree);

            foreach (var method in typeDeclarationSyntax.Members.OfType<MethodDeclarationSyntax>())
            {
                var fullMethodName = method.Identifier.Text;
                if (!fullMethodName.EndsWith(InterceptedMethodPostfix))
                {
                    continue;
                }

                // The postfix is an explicit opt-in to interception, so a method that carries it and
                // still gets dropped is worth reporting: the user asked for a wrapper and silently
                // did not get one.

                // A method named exactly "WithoutInterceptor" would yield an empty wrapper name.
                if (fullMethodName.Length == InterceptedMethodPostfix.Length)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.MemberSkipped, location,
                        $"{typeSymbol.Name}.{fullMethodName}",
                        $"the name has no prefix before '{InterceptedMethodPostfix}'",
                        "rename it so a name remains once the postfix is stripped"));
                    continue;
                }

                // The emitter drops static and generic shapes, and cannot route an explicit interface
                // implementation through the executor. The wrapper forwards its parameters by value,
                // which a plain "ref" or an "out" parameter rejects (CS1620), while "in" and
                // "ref readonly" accept it, so only the first two are skipped. A by-reference return
                // type is skipped outright: GetFullTypeName cannot name a RefTypeSyntax, and the
                // wrapper would otherwise compile with a "void" return that silently dereferences the
                // ref return into a copy and discards it.
                if (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
                    method.TypeParameterList is not null ||
                    method.ExplicitInterfaceSpecifier is not null ||
                    method.ReturnType is RefTypeSyntax ||
                    method.ParameterList.Parameters.Any(HasUnsupportedByReferenceModifier))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.MemberSkipped, location,
                        $"{typeSymbol.Name}.{fullMethodName}",
                        "the method shape is not supported (static, generic, a by-reference parameter other than 'in' or 'ref readonly', a by-reference return type, or an explicit interface implementation)",
                        $"change the method shape, or drop the '{InterceptedMethodPostfix}' postfix if it should not be intercepted"));
                    continue;
                }

                var methodName = fullMethodName.Substring(0, fullMethodName.Length - InterceptedMethodPostfix.Length);

                // The capture is silent: in derived mode the only compiler signal is a CS0108 that a
                // consumer without TreatWarningsAsErrors never sees, and an AddProperties wrapper
                // produces none at all. NI0013 scans declared members rather than emitted ones.
                if (GeneratedMemberTable.CollidesWithGeneratedMember(methodName))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.MemberSkipped, location,
                        $"{typeSymbol.Name}.{fullMethodName}",
                        $"the wrapper would be named '{methodName}', which is a generated subject interception member",
                        "rename the method so that stripping the postfix leaves a name the generated interception members do not use"));
                    continue;
                }

                var returnType = GetFullTypeName(method.ReturnType, declarationModel);

                // Text keeps the escape on a verbatim identifier; the wrapper restates the name
                // in declaration and argument position, where the unescaped keyword cannot parse.
                var parameters = method.ParameterList.Parameters
                    .Select(p => new ParameterMetadata(
                        p.Identifier.Text,
                        GetFullTypeName(p.Type, declarationModel) ?? "object"))
                    .ToList();

                methods.Add(new MethodMetadata(
                    methodName,
                    fullMethodName,
                    returnType ?? "void",
                    parameters));
            }
        }

        return methods;
    }

    /// <summary>
    /// A "ref readonly" parameter carries both the "ref" and the "readonly" modifier, and unlike a
    /// plain "ref" it binds to the temporary the wrapper forwards, which only warns with CS9193.
    /// The generated file suppresses that warning, so only plain "ref" and "out" remain unsupported.
    /// </summary>
    private static bool HasUnsupportedByReferenceModifier(ParameterSyntax parameter)
    {
        var modifiers = parameter.Modifiers;
        return modifiers.Any(SyntaxKind.OutKeyword) ||
               (modifiers.Any(SyntaxKind.RefKeyword) && !modifiers.Any(SyntaxKind.ReadOnlyKeyword));
    }

    /// <summary>
    /// Extracts properties with default implementations from all interfaces implemented by the type.
    /// </summary>
    private static IReadOnlyList<PropertyMetadata> ExtractInterfaceDefaultProperties(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        Compilation compilation,
        Location location,
        List<Diagnostic> diagnostics)
    {
        var interfaceProperties = new List<PropertyMetadata>();
        var classPropertyNames = new HashSet<string>(classProperties.Select(p => p.Name));

        // Keyed by simple name, valued by the member that took it, so a collision can name the
        // winner instead of leaving several identical warnings at one location.
        var winnerByPropertyName = new Dictionary<string, string>();

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            foreach (var member in interfaceType.GetMembers())
            {
                if (member is not IPropertySymbol property)
                {
                    continue;
                }

                // A property has a default implementation if any accessor is not abstract. This
                // runs before every other guard so that the guards below only ever fire on a
                // member the subject could plausibly have adopted: an abstract interface member is
                // implemented by the class itself, so nothing about it is skipped and reporting on
                // it would put a warning on every interface a subject implements.
                var hasDefaultImplementation =
                    property.GetMethod is { IsAbstract: false } ||
                    property.SetMethod is { IsAbstract: false };
                if (!hasDefaultImplementation)
                {
                    continue;
                }

                // A static property with a body is not abstract, so it passes the default
                // implementation test above, but it cannot be read from an instance.
                if (IsNeverASubjectProperty(property))
                {
                    continue;
                }

                // For an explicit implementation, IPropertySymbol.Name is the fully qualified
                // "Namespace.IHuman.Gender". The implemented member carries the simple name, and
                // its containing type is the interface the accessor must cast through: reflection
                // on the declaring interface does not find the member, and the implemented one
                // dispatches correctly in every direction.
                var explicitImplementation = property.ExplicitInterfaceImplementations.FirstOrDefault();
                var resolvedName = explicitImplementation?.Name ?? property.Name;
                var accessorInterface = explicitImplementation?.ContainingType ?? interfaceType;

                // Skip properties already declared in the class. The class declaration is the
                // implementation, so nothing diverges and nothing is reported.
                if (classPropertyNames.Contains(resolvedName))
                {
                    continue;
                }

                // Skip properties already processed from another interface (diamond inheritance)
                if (winnerByPropertyName.TryGetValue(resolvedName, out var winnerDescription))
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.PropertyNameCollision, location,
                        resolvedName, winnerDescription, $"{accessorInterface.ToDisplayString()}.{resolvedName}"));
                    continue;
                }

                // Roslyn reports an explicit implementation as Private regardless of the implemented
                // member's real visibility, so the accessibility that matters is the implemented
                // member's. Ask the compiler directly whether generated code (living inside
                // typeSymbol, accessing the member through a cast to accessorInterface) can reach
                // it, instead of hand-rolling the rule: a hardcoded "same assembly" premise breaks
                // as soon as the interface lives in a referenced assembly (internal and protected
                // internal members are then unreachable, CS0122/CS1540, unless InternalsVisibleTo
                // says otherwise), and passing accessorInterface as the qualifying type correctly
                // rejects protected members, which are never reachable through this cast pattern.
                // This runs after the cheap name-based filters above so the compiler is not asked
                // about a member that would be discarded anyway.
                var accessibilityMember = explicitImplementation ?? property;
                var (isGetterAccessible, isSetterAccessible) = GetAccessorAccessibility(
                    compilation, accessibilityMember, typeSymbol, accessorInterface);

                // Skipped in silence. An interface member that generated code cannot see is scoped
                // by its own author as a helper rather than offered as a property, and the interface
                // may well be third-party, leaving the subject author with no remedy to follow. The
                // class-declared explicit implementation in CollectProperties is the opposite case,
                // written by the subject's own author, and stays reported.
                if (!isGetterAccessible && !isSetterAccessible)
                {
                    continue;
                }

                // The emitted metadata reflects the implemented member's PropertyInfo, not the
                // explicit implementation's, so anything declared on the implementation (a Derived
                // or validation attribute in particular) never reaches the runtime.
                if (explicitImplementation is not null && property.GetAttributes().Length > 0)
                {
                    diagnostics.Add(Diagnostic.Create(
                        Diagnostics.ExplicitImplementationAttributesIgnored, location, resolvedName));
                }

                winnerByPropertyName[resolvedName] = $"{accessorInterface.ToDisplayString()}.{resolvedName}";

                var fullyQualifiedTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var accessModifier = GetAccessModifierFromAccessibility(property.DeclaredAccessibility);
                var interfaceTypeName = accessorInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                var hasGetter = property.GetMethod != null && isGetterAccessible;
                var hasSetter = property.SetMethod is { IsInitOnly: false } && isSetterAccessible;
                var hasInit = property.SetMethod?.IsInitOnly == true && isSetterAccessible;

                // Interface default properties cannot be partial, virtual is implicit
                interfaceProperties.Add(new PropertyMetadata(
                    resolvedName,
                    fullyQualifiedTypeName,
                    accessModifier,
                    IsPartial: false,
                    IsVirtual: true,  // Interface default implementations are implicitly virtual
                    IsOverride: false,
                    IsNew: false,
                    IsSealed: false,
                    IsDerived: HasDerivedAttribute(property),
                    IsRequired: false,
                    hasGetter,
                    hasSetter,
                    hasInit,
                    IsFromInterface: true,
                    GetterAccessModifier: null,
                    SetterAccessModifier: null,
                    InterfaceTypeName: interfaceTypeName));
            }
        }

        return interfaceProperties;
    }

    /// <summary>
    /// Reports a class-declared property whose name matches an interface member that resolves to an
    /// implementation outside this type, so that reading through the interface and reading through
    /// the subject return different values.
    /// </summary>
    /// <remarks>
    /// Interface implementation is fixed where the interface joins the base list, so a property
    /// declared further down the hierarchy does not take over the slot. This must not fire on the
    /// ordinary shape, where the subject itself declares support for the interface and its own
    /// property is the implementation, nor on an override, which shares the base member's slot.
    /// </remarks>
    private static void ReportPropertiesShadowingABaseImplementation(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<PropertyMetadata> classProperties,
        Location location,
        List<Diagnostic> diagnostics)
    {
        // Without a base class the subject's own declarations own every interface slot it has.
        if (typeSymbol.BaseType is not { } baseType || baseType.SpecialType == SpecialType.System_Object)
        {
            return;
        }

        foreach (var property in classProperties)
        {
            // An explicit implementation is by definition the implementation, and an override
            // shares the slot of the base member it overrides.
            if (property.ExplicitInterfaceTypeName is not null || property.IsOverride)
            {
                continue;
            }

            foreach (var interfaceType in typeSymbol.AllInterfaces)
            {
                // The message claims the base class already implements this member, so that must
                // be checked directly: an interface the subject itself lists (not one the base type
                // carries) is the subject's own to implement, even when its own same-named property
                // fails to bind to it (a type or accessor mismatch, say) and the interface's default
                // body ends up as the resolved implementation instead. That default is not the base
                // class's doing, so it must not be blamed as one.
                if (!baseType.AllInterfaces.Contains(interfaceType, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var interfaceMember = interfaceType
                    .GetMembers(property.Name)
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault();

                if (interfaceMember is null)
                {
                    continue;
                }

                var implementation = typeSymbol.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation is null ||
                    SymbolEqualityComparer.Default.Equals(implementation.ContainingType, typeSymbol))
                {
                    continue;
                }

                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.ShadowsBaseImplementation, location,
                    typeSymbol.Name, property.Name));
                break;
            }
        }
    }

    /// <summary>
    /// Resolves per-accessor reachability of an interface member from generated code living inside
    /// <paramref name="typeSymbol"/>, accessed through a receiver cast to <paramref name="throughType"/>.
    /// Shared by the interface default-implementation path and the class explicit-implementation
    /// path, since both reach the member through the same "cast to the interface" pattern and are
    /// governed by the same accessibility rule.
    /// </summary>
    private static (bool IsGetterAccessible, bool IsSetterAccessible) GetAccessorAccessibility(
        Compilation compilation,
        IPropertySymbol member,
        INamedTypeSymbol typeSymbol,
        ITypeSymbol throughType)
    {
        if (!compilation.IsSymbolAccessibleWithin(member, typeSymbol, throughType))
        {
            return (false, false);
        }

        // A getter or setter can be individually less accessible than the property itself
        // (e.g. `string Probe { get; private set; }`); generated code accesses whichever accessor
        // it emits directly, so each one needs its own reachability check.
        var isGetterAccessible = member.GetMethod is { } getMethod &&
            compilation.IsSymbolAccessibleWithin(getMethod, typeSymbol, throughType);
        var isSetterAccessible = member.SetMethod is { } setMethod &&
            compilation.IsSymbolAccessibleWithin(setMethod, typeSymbol, throughType);

        // Both false here (with the property-level check above having passed) is believed
        // unreachable: C# forbids an accessor modifier on both accessors at once, and requires any
        // accessor modifier to be strictly more restrictive than the property, so the accessor
        // without a modifier is accessible by construction whenever the property-level check
        // passed. Kept defensive rather than assumed, in case that invariant stops holding.
        return (isGetterAccessible, isSetterAccessible);
    }

    private static string GetAccessModifierFromAccessibility(Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "public"  // Interface members default to public
        };
    }

    /// <summary>
    /// Detects the constructor state for the class.
    /// Returns a tuple of:
    /// - NeedsGeneratedParameterlessConstructor: true if no constructor exists and we need to generate one
    /// - HasOrWillHaveParameterlessConstructor: true if we have or will generate a parameterless constructor
    /// - ParameterlessConstructorSetsRequiredMembers: true if the emitted constructors have to carry [SetsRequiredMembers]
    /// </summary>
    private static (bool NeedsGeneratedParameterlessConstructor, bool HasOrWillHaveParameterlessConstructor, bool ParameterlessConstructorSetsRequiredMembers) DetectConstructorState(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax[] allTypeDeclarations)
    {
        // A static constructor is not an instance constructor, so nothing can chain to it and it
        // never stands in for the parameterless one the emitted constructors need.
        var firstConstructor = allTypeDeclarations
            .SelectMany(c => c.Members)
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(constructor => !constructor.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)));

        // No constructor at all: generate a parameterless one. A first constructor with parameters
        // means there is no parameterless one to chain to, so nothing is generated.
        var needsGeneratedParameterlessConstructor = firstConstructor is null;
        var hasOrWillHaveParameterlessConstructor = firstConstructor is null or { ParameterList.Parameters.Count: 0 };

        return (
            needsGeneratedParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor,
            hasOrWillHaveParameterlessConstructor && ChainedConstructorSetsRequiredMembers(typeSymbol));
    }

    /// <summary>
    /// Whether the parameterless constructor the emitted constructors chain to carries
    /// [SetsRequiredMembers]. CS9039 rejects a constructor that chains to one with the attribute unless
    /// it repeats it, for the implicit base chain as much as for ": this()", so the emitted constructors
    /// mirror it. Read off symbols because the constructor may sit in another partial file than the one
    /// this extraction's semantic model belongs to.
    /// </summary>
    /// <remarks>
    /// The walk passes through implicitly declared constructors: on a subject the generator replaces
    /// each of those with an explicit one that mirrors its own base, and that emitted constructor is
    /// not visible in this compilation's symbols. Passing through a non-subject one is safe too,
    /// because an implicit constructor above an attributed one is already CS9039 in its own right.
    /// Constructors read from metadata are never implicit, so a referenced assembly ends the walk on
    /// its real attribute state. The walk stops at a type that declares required members of its own,
    /// because an emitted constructor claiming to set them would be lying; that subject keeps the
    /// CS9039 and has to declare the initializing constructor itself.
    /// </remarks>
    private static bool ChainedConstructorSetsRequiredMembers(INamedTypeSymbol typeSymbol)
    {
        for (var type = typeSymbol; type is not null; type = type.BaseType)
        {
            var constructor = type.InstanceConstructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0);
            if (constructor is null)
            {
                return false;
            }

            if (!constructor.IsImplicitlyDeclared)
            {
                return constructor.GetAttributes().Any(attribute =>
                    SymbolExtensions.IsTypeOrInheritsFrom(attribute.AttributeClass, KnownTypes.SetsRequiredMembersAttribute));
            }

            if (type.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects the declared instance constructors across all partial declarations, omitting the
    /// ones a mirror could not reproduce faithfully. Parameter types are resolved through each
    /// declaration's own semantic model rather than taken from syntax text, because the generated
    /// partial half does not repeat the declaring file's using directives, so a name like
    /// "List&lt;Foo&gt;" may not resolve there.
    /// </summary>
    private static IReadOnlyList<SubjectConstructor> CollectConstructors(
        TypeDeclarationSyntax[] allTypeDeclarations,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var constructors = new List<SubjectConstructor>();

        foreach (var typeDeclaration in allTypeDeclarations)
        {
            var declarationModel = semanticModel.Compilation.GetSemanticModel(typeDeclaration.SyntaxTree);

            foreach (var constructor in typeDeclaration.Members.OfType<ConstructorDeclarationSyntax>())
            {
                // A static constructor has no accessibility and cannot be chained to.
                if (constructor.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                {
                    continue;
                }

                // An obsolete constructor is never mirrored: the mirror's ": this(...)" chain
                // would reference it from the generated file, raising CS0618 (or CS0619 with
                // error: true) in code the consumer does not own. It is still collected, because
                // the emit side matches each mirror it is about to write against the declared
                // signatures; an obsolete constructor already carrying a mirror's shape would
                // otherwise collide with it as CS0111 in a file the consumer cannot edit.
                // Resolved through the semantic model so an alias, the "Attribute" suffix and
                // qualified spellings are all caught, not just the literal identifier "Obsolete".
                var isObsolete = SymbolExtensions.HasAttribute(
                    constructor.AttributeLists, KnownTypes.ObsoleteAttribute, declarationModel, cancellationToken);

                var parameters = CollectConstructorParameters(constructor, declarationModel);
                if (parameters is null)
                {
                    continue;
                }

                // Carried because a generated constructor chaining to this one has to repeat the
                // attribute; without it the chain is CS9039.
                var setsRequiredMembers = SymbolExtensions.HasAttribute(
                    constructor.AttributeLists, KnownTypes.SetsRequiredMembersAttribute, declarationModel, cancellationToken);

                constructors.Add(new SubjectConstructor(
                    GetAccessModifier(constructor.Modifiers),
                    parameters,
                    isObsolete,
                    setsRequiredMembers));
            }
        }

        return constructors;
    }

    private static IReadOnlyList<SubjectConstructorParameter>? CollectConstructorParameters(
        ConstructorDeclarationSyntax constructor,
        SemanticModel declarationModel)
    {
        var parameters = new List<SubjectConstructorParameter>(constructor.ParameterList.Parameters.Count);

        foreach (var parameter in constructor.ParameterList.Parameters)
        {
            // ref, out, in, params or scoped: the metadata carries no parameter modifier, so a
            // mirror would either not compile or silently change the calling convention. Such a
            // constructor is never mirrored.
            if (parameter.Modifiers.Count > 0)
            {
                return null;
            }

            var parameterType = parameter.Type is not null
                ? declarationModel.GetTypeInfo(parameter.Type).Type
                : null;
            if (parameterType is null)
            {
                // Only reachable with error code, since a constructor parameter cannot omit its
                // type; dropping the whole constructor beats carrying a wrong signature.
                return null;
            }

            // A pointer or function pointer type compiles only in an unsafe context, which the
            // generated mirror does not have; "unsafe" sits on the constructor, so the modifier
            // check above never sees it. Arrays are unwrapped because int*[] carries the same
            // requirement.
            if (ContainsPointerType(parameterType))
            {
                return null;
            }

            // Text rather than ValueText: the latter drops the escape from a verbatim identifier,
            // so a parameter named "@event" would be re-emitted as the keyword "event".
            parameters.Add(new SubjectConstructorParameter(
                parameterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parameter.Identifier.Text));
        }

        return parameters;
    }

    private static bool ContainsPointerType(ITypeSymbol type)
    {
        while (type is IArrayTypeSymbol array)
        {
            type = array.ElementType;
        }

        return type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;
    }

    private static string GetAccessModifier(SyntaxTokenList modifiers)
    {
        var hasPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var hasInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var hasPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

        return (hasPublic, hasProtected, hasInternal, hasPrivate) switch
        {
            (true, _, _, _) => "public",
            (_, true, true, _) => "protected internal",
            (_, true, _, true) => "private protected",
            (_, true, _, _) => "protected",
            (_, _, true, _) => "internal",
            _ => "private"
        };
    }

    private static string? GetAccessorModifier(AccessorListSyntax? accessorList, SyntaxKind accessorKind)
    {
        var accessor = accessorList?.Accessors.FirstOrDefault(a => a.IsKind(accessorKind));
        if (accessor == null)
        {
            return null;
        }

        var modifiers = accessor.Modifiers;
        if (modifiers.Count == 0)
        {
            return null;
        }

        return string.Join(" ", modifiers.Select(m => m.ValueText));
    }

    private static bool HasDerivedAttribute(PropertyDeclarationSyntax property, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return SymbolExtensions.HasAttribute(property.AttributeLists, KnownTypes.DerivedAttribute, semanticModel, cancellationToken);
    }

    private static bool HasDerivedAttribute(IPropertySymbol property)
    {
        return property.GetAttributes()
            .Any(a => SymbolExtensions.IsTypeOrInheritsFrom(a.AttributeClass, KnownTypes.DerivedAttribute));
    }

    /// <summary>
    /// Names the type exactly as the property path does. A hand-built generic name of the form
    /// "{ContainingNamespace}.{Name}&lt;...&gt;" drops every enclosing type and renders the global
    /// namespace as the literal "&lt;global namespace&gt;", which does not parse; the fully
    /// qualified format handles both, so there is nothing left to special-case for generics.
    /// </summary>
    private static string? GetFullTypeName(TypeSyntax? type, SemanticModel semanticModel)
    {
        if (type == null)
        {
            return null;
        }

        return semanticModel.GetTypeInfo(type).Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
