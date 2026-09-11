using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

/// <summary>
/// One-shot, per-write origin handoff. An origin moves through three stages: pending (set in the
/// slot, waiting for its write), then attempted (consumed into the write context, carried
/// unverified), then finalized (verified or demoted to Local at the terminal write).
/// <see cref="Set"/> stores a pending stamp for exactly
/// one write of one property; the matching write chain consumes it at
/// <c>PropertyWriteContext</c> construction. Nested writes (hooks, INPC handlers, derived
/// recalculations) never inherit it: the slot is either already consumed or targets a
/// different property. The scope captures the previous frame and restores it on dispose
/// (a zero-allocation stack through nested ref structs, like SubjectChangeContextScope),
/// so a cancelled write cannot leak the stamp and a nested stamped write cannot destroy
/// an outer stamp. Same-property re-entry from OnChanging is unsupported (the inner
/// invocation consumes the stamp). Thread-static by design: set and consume happen
/// synchronously within one call frame, never across await. Internal: producers use
/// intent-level APIs (SetValueFromSource, ApplySubjectUpdate, transaction replay).
/// </summary>
internal static class PendingOrigin
{
    /// <summary>
    /// The pending stamp held in a single thread-static slot so Set/TryConsume/Restore perform one
    /// TLS lookup plus field offsets instead of four separate slot accesses, and each reset is a
    /// single default assignment (so no reference can be left behind partially).
    /// </summary>
    internal struct PendingFrame
    {
        public bool HasValue;
        public PropertyReference Target;
        public AttemptedOrigin Attempted;
    }

    [ThreadStatic] private static PendingFrame _frame;

    internal static PendingOriginScope Set(PropertyReference target, ChangeOrigin origin, object? sentValue, IPropertyWriteGuard? guard = null)
    {
        var scope = new PendingOriginScope(_frame);
        _frame = new PendingFrame
        {
            HasValue = true,
            Target = target,
            Attempted = new AttemptedOrigin(origin, sentValue, guard)
        };
        return scope;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryConsume(in PropertyReference property, out AttemptedOrigin attempted)
    {
        if (_frame.HasValue && _frame.Target.Equals(property))
        {
            attempted = _frame.Attempted;
            _frame = default;
            return true;
        }

        attempted = default;
        return false;
    }

    internal static void Restore(in PendingFrame frame)
    {
        _frame = frame;
    }
}

internal readonly ref struct PendingOriginScope
{
    private readonly PendingOrigin.PendingFrame _previousFrame;

    internal PendingOriginScope(in PendingOrigin.PendingFrame previousFrame)
    {
        _previousFrame = previousFrame;
    }

    public void Dispose() => PendingOrigin.Restore(in _previousFrame);
}
