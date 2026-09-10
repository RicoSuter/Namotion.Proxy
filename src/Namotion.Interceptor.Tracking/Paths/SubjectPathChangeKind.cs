namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>The kind of observable transition delivered by a path subscription.</summary>
public enum SubjectPathChangeKind
{
    /// <summary>The current resolved leaf itself was written while the chain was intact; <c>SubjectPathChange&lt;TValue&gt;.Cause</c> is that leaf write.</summary>
    ValueChange,

    /// <summary>The observed state changed for any other reason (a structural write, or a revalidation triggered by an off-path write after a dormant divergence).</summary>
    PathChange
}
