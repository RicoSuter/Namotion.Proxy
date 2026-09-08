using System.Collections.Generic;

namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// What the generator knows about the class a subject inherits from. All of it is resolved from the
/// nearest subject ancestor rather than the immediate base, because a plain class may sit in
/// between, except <see cref="HasInpc"/>, whose second disjunct is asked of the subject itself.
/// </summary>
/// <param name="TypeName">The ancestor's fully qualified name, or null when there is no subject ancestor.</param>
/// <param name="HasInterceptorSubject">Whether that ancestor carries the attribute.</param>
/// <param name="HasInpc">Whether the INotifyPropertyChanged members are already inherited.</param>
/// <param name="HasCallableRaisePropertyChanged">Whether an unqualified RaisePropertyChanged(name) call from the subject binds to an inherited member. False for a chain that only implements IRaisePropertyChanged explicitly, where the interface form is the only one that compiles.</param>
/// <param name="EmitsInterceptionMembers">True in root mode, where the subject itself, not the base class, emits the whole IInterceptorSubject block.</param>
/// <param name="HiddenMemberNames">Root mode members that need a 'new' modifier because the ancestor already exposes that name.</param>
internal sealed record SubjectBaseClass(
    string? TypeName,
    bool HasInterceptorSubject,
    bool HasInpc,
    bool HasCallableRaisePropertyChanged,
    bool EmitsInterceptionMembers,
    IReadOnlyList<string> HiddenMemberNames);
