namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// Per-subscription choice of how much of the path a leaf write revalidates. Structural
/// (upper-segment) writes always run the full from-root validating walk and retrack in both modes, and
/// <see cref="SubjectPathSubscription{TValue}.Current"/> is always a fresh full walk regardless of the
/// mode.
/// </summary>
public enum SubjectPathValidation
{
    /// <summary>
    /// Every processed callback, leaf or structural, revalidates the whole path from the root. A
    /// divergence created while a segment was dormant (a structural change that dispatched no callback)
    /// is healed on the next delivered callback of any kind.
    /// </summary>
    Full,

    /// <summary>
    /// A write to the resolved leaf re-reads only the leaf on its cached subject and skips the
    /// from-root walk; structural writes still revalidate and retrack. Faster for deep paths with
    /// high-frequency leaf writes, but a divergence created while a segment was dormant is not healed
    /// by a leaf write, only by a later structural write (a bounded consistency carve-out: a stale
    /// off-path leaf can keep delivering until the next structural callback).
    /// </summary>
    LeafOnly
}
