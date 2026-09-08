namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// Why a synchronization wait completed: whether every source on the awaited branch delivered its
/// initial load, and whether they are all still live.
/// </summary>
/// <remarks>
/// Answers per source, not per property: whether every in-scope source completed its initial load,
/// not whether every property holds a value from the external system. See the source monitoring
/// documentation.
/// <para>
/// Do not reorder the members. Worst-wins aggregation is a minimum over them, and Incomplete at zero
/// makes the CLR default the most pessimistic answer.
/// </para>
/// </remarks>
public enum SourceSynchronizationResult
{
    /// <summary>
    /// At least one in-scope source stopped having never synchronized, so part of the branch never
    /// received data and may still hold CLR defaults.
    /// </summary>
    Incomplete = 0,

    /// <summary>
    /// Every in-scope source synchronized at least once, but at least one has since stopped, so the
    /// values are real and possibly out of date.
    /// </summary>
    Stale = 1,

    /// <summary>
    /// Every in-scope source completed its initial load and is still live. Also the answer for a
    /// branch with no source in scope, which is why this is not proof that a source exists.
    /// </summary>
    Synchronized = 2
}
