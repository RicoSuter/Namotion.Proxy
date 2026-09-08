namespace Namotion.Interceptor.Generator.Models;

internal sealed record PropertyMetadata(
    string Name,
    string FullTypeName,
    string AccessModifier,
    bool IsPartial,
    bool IsVirtual,
    bool IsOverride,
    bool IsNew,
    bool IsSealed,
    bool IsDerived,
    bool IsRequired,
    bool HasGetter,
    bool HasSetter,
    bool HasInit,
    bool IsFromInterface,
    string? GetterAccessModifier,
    string? SetterAccessModifier,
    string? InterfaceTypeName = null,
    string? ExplicitInterfaceTypeName = null);
