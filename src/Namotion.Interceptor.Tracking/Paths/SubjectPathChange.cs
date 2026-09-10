using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>An observable transition of a watched path. <see cref="Cause"/> is the real property write that triggered it (supplementary provenance, not something a consumer must decode).</summary>
public readonly struct SubjectPathChange<TValue>
{
    internal SubjectPathChange(
        SubjectPathChangeKind kind,
        SubjectPathValue<TValue> oldState,
        SubjectPathValue<TValue> newState,
        in SubjectPropertyChange cause)
    {
        Kind = kind;
        OldState = oldState;
        NewState = newState;
        Cause = cause;
    }

    public SubjectPathChangeKind Kind { get; }

    /// <summary>The observed state (which may be unresolved) before this event.</summary>
    public SubjectPathValue<TValue> OldState { get; }

    /// <summary>The observed state (which may be unresolved) after this event: a fresh walk, or after a divergent retrack the retrack's reads; never copied from the causing write payload.</summary>
    public SubjectPathValue<TValue> NewState { get; }

    /// <summary>The real write that triggered this event. <c>Cause.Origin</c> is the trigger's origin verbatim and is deliberately not provenance for <see cref="NewState"/>.</summary>
    public SubjectPropertyChange Cause { get; }
}
