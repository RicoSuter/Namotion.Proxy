using System.Collections.Generic;

namespace Namotion.Interceptor.Generator.Models;

internal sealed record SubjectMetadata(
    string ClassName,
    string AccessModifier,
    bool IsSealed,
    string? NamespaceName,
    string FullTypeName,
    ContainingType[] ContainingTypes,
    bool NeedsGeneratedParameterlessConstructor,
    bool HasOrWillHaveParameterlessConstructor,
    bool ParameterlessConstructorSetsRequiredMembers,
    IReadOnlyList<SubjectConstructor> Constructors,
    SubjectBaseClass BaseClass,
    IReadOnlyList<PropertyMetadata> Properties,
    IReadOnlyList<MethodMetadata> Methods);

/// <summary>
/// One declared instance constructor, carried so the emitter can mirror it with a trailing
/// context parameter. Parameter types are fully qualified because the generated partial half
/// does not repeat the declaring file's using directives, so a syntax-text name may not
/// resolve there.
/// </summary>
internal sealed record SubjectConstructor(
    string Accessibility,
    IReadOnlyList<SubjectConstructorParameter> Parameters,
    bool IsObsolete,
    bool SetsRequiredMembers);

internal sealed record SubjectConstructorParameter(
    string FullyQualifiedTypeName,
    string Name);
