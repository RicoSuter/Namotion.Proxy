namespace Namotion.Interceptor;

/// <summary>
/// Describes what anchors a subject to its attached context.
/// </summary>
public enum SubjectAttachmentAnchorKind
{
    /// <summary>
    /// The subject carries no anchor. It is either unattached, or attached only through an
    /// inherited structural edge.
    /// </summary>
    None,

    /// <summary>
    /// The subject was anchored by a context-taking constructor. A provisional anchor is cleared
    /// automatically when the subject gains an inherited structural edge whose parent reaches an
    /// anchor other than the subject itself; a back edge from the subject's own subtree does not
    /// clear it.
    /// </summary>
    Provisional,

    /// <summary>
    /// The subject was anchored by an explicit attach call. An explicit anchor is never cleared
    /// automatically; it must be detached explicitly.
    /// </summary>
    Explicit
}
