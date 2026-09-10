using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator;

/// <summary>
/// Every rule must also be listed in AnalyzerReleases.Unshipped.md, or RS2008 fails the build.
/// </summary>
internal static class Diagnostics
{
    public const string Category = "Namotion.Interceptor";

    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "NI0001",
        title: "Interceptor subject must be partial",
        messageFormat: "Interceptor subject '{0}' must be declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator emits the subject's implementation as a second partial declaration.");

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        id: "NI0002",
        title: "Containing type of an interceptor subject must be partial",
        messageFormat: "Containing type '{0}' of interceptor subject '{1}' must be declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated file re-declares every containing type.");

    public static readonly DiagnosticDescriptor UnsupportedTypeKind = new(
        id: "NI0003",
        title: "InterceptorSubject is only supported on classes",
        messageFormat: "'{0}' is a {1}, and InterceptorSubject is only supported on classes",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Records and record structs are excluded because the generated interception members break value equality and with-expressions.");

    public static readonly DiagnosticDescriptor GeneratorFailed = new(
        id: "NI0004",
        title: "Interceptor subject generation failed",
        // One sentence with no trailing period, because RS1032 rejects anything else, and the
        // exception message interpolated at {2} usually ends in a period of its own. The generated
        // source is only a file on disk when the consumer sets EmitCompilerGeneratedFiles, so the
        // message must not send them looking for one that is not there.
        messageFormat: "Generating '{0}' failed with {1}: {2} (the full stack trace is in the generated source, which reaches disk only with EmitCompilerGeneratedFiles set)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An unhandled exception in the generator. Please report it.");

    public static readonly DiagnosticDescriptor ShadowsBaseImplementation = new(
        id: "NI0005",
        title: "Property re-declares a member already implemented by the base class",
        messageFormat: "'{0}' re-declares '{1}', which the base class already implements, so the subject and the interface report different values",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reading through the interface resolves to the base class implementation, not this property.");

    public static readonly DiagnosticDescriptor MemberSkipped = new(
        id: "NI0006",
        title: "Unsupported member skipped",
        // The remedy is interpolated at {2} rather than fixed, because the four reasons that reach
        // this rule do not share one: renaming answers a colliding wrapper name and says nothing
        // about an inaccessible member.
        messageFormat: "'{0}' was skipped because {1}, so it is not intercepted; {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "No wrapper and no property metadata is emitted for the member, so a 'WithoutInterceptor' opt-in on it is ignored. When the skipped wrapper name is one the generated interception members occupy, a call to the stripped name binds to the inherited member instead.");

    public static readonly DiagnosticDescriptor ExplicitImplementationAttributesIgnored = new(
        id: "NI0007",
        title: "Attributes on an explicit interface implementation are ignored",
        messageFormat: "Attributes on the explicit implementation of '{0}' are absent from the subject's property metadata; an attribute the library reads, such as Derived or a validation attribute, belongs on the interface member",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Property metadata reflects the interface member, so an attribute the library reads only takes effect when it is declared there. An implementation-local attribute such as SuppressMessage or ExcludeFromCodeCoverage keeps its usual meaning on the implementation and is simply not part of the metadata.");

    public static readonly DiagnosticDescriptor PropertyNameCollision = new(
        id: "NI0008",
        title: "More than one member provides the same property name",
        // The winner and the dropped member are both interpolated: several interfaces colliding on
        // one name otherwise produce byte-identical warnings at one location, and a class-declared
        // property that takes the name is neither of the colliding members.
        messageFormat: "'{0}' is provided by more than one member; the subject exposes {1} and {2} is unreachable",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Subject properties are keyed by simple name, so only one of the colliding members is reachable. A class-declared property always takes the name; between interface members, the first one the generator reaches takes it.");

    public static readonly DiagnosticDescriptor GenericTypeNotSupported = new(
        id: "NI0009",
        title: "Generic interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is generic, which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated declaration does not carry type parameters or constraints.");

    /// <summary>
    /// Reported instead of <see cref="GenericTypeNotSupported"/> when the subject itself is not
    /// generic but a containing type is: Roslyn's <c>INamedTypeSymbol.IsGenericType</c> is true for
    /// a non-generic type nested inside a generic one, so the subject cannot be blamed by name here.
    /// </summary>
    public static readonly DiagnosticDescriptor GenericContainingTypeNotSupported = new(
        id: "NI0009",
        title: "Generic interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is nested in generic containing type '{1}', which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated declaration does not carry type parameters or constraints, and neither can any containing type.");

    public static readonly DiagnosticDescriptor FileTypeNotSupported = new(
        id: "NI0010",
        title: "File-local interceptor subjects are not supported",
        messageFormat: "Interceptor subject '{0}' is file-local, which is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A generated partial declaration cannot join a file-local type.");

    public static readonly DiagnosticDescriptor BaseDoesNotSatisfyContract = new(
        id: "NI0011",
        title: "Base class does not satisfy the subject base contract",
        // Two sentences, so RS1032 requires the trailing period the single-sentence rules omit.
        messageFormat: "Base class '{0}' cannot host a generated subject: it is missing {1}. Use [InterceptorSubject] on the base, or call AddProperties for runtime properties.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated subclass calls members the base class must provide. This checks their shape, not their behaviour.");

    public static readonly DiagnosticDescriptor BaseInterceptionMembersCannotBeShared = new(
        id: "NI0012",
        title: "Base class interception members cannot be shared",
        // The missing members are interpolated at {2}: five different base defects reach this rule,
        // and naming none of them made every one of them produce the same text. Two sentences, so
        // RS1032 requires the trailing period, exactly as in NI0011.
        messageFormat: "Base class '{0}' is missing {2}, so '{1}' emits its own interception members and the base class's own properties stay unintercepted. Add the missing members to the base class, or rebuild the base assembly against the current package version if it predates the shared interception members.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The subject still generates and behaves as it did before those members became shared, but a project that treats warnings as errors fails on this rule.");

    public static readonly DiagnosticDescriptor HidesGeneratedMember = new(
        id: "NI0013",
        title: "Member hides an inherited generated member",
        messageFormat: "'{0}' declares '{1}', which hides the inherited generated member of the same name and can silently capture the generated call",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated property and method bodies call these members by simple name.");

    public static readonly DiagnosticDescriptor HijacksInterfaceImplementation = new(
        id: "NI0014",
        title: "Member hijacks an inherited interface implementation",
        messageFormat: "'{0}' declares '{1}', which takes the IInterceptorSubject.{1} slot from the base class implementation under interface re-implementation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Hijacking Executor leaves the inherited helpers reading a context that is never populated, so interception silently stops.");
}
