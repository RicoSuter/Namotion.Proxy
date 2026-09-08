using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The delivery decision, for a connector that needs to repeat it at the point it actually writes.
/// </summary>
public static class ChangeDelivery
{
    /// <summary>
    /// Whether the model has committed something newer than this change, under the given rule, so that
    /// writing it out would put the destination behind the model.
    /// </summary>
    /// <remarks>
    /// A processor decides this once, when it hands a batch to the write handler. A connector whose
    /// destination is also written by someone else, under a lock the connector takes to write, can be
    /// preempted between those two points and would then write a change that has just been superseded.
    /// Asking again while holding that lock closes the window, for one property data lookup per change.
    /// <para>
    /// Only useful where the connector and the competing writer serialize on something. Where they do
    /// not, this answers about a moment that has already passed by the time the write lands.
    /// </para>
    /// </remarks>
    /// <param name="change">The change about to be written.</param>
    /// <param name="rule">The same rule the processor was constructed with. Answering under a different
    /// rule than the processor uses would apply two different definitions of stale to one stream.</param>
    /// <returns>True when a later commit already carries the settled value in this change's place, so the
    /// change must be dropped rather than written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rule is <see cref="ChangeDeliveryRule.Unspecified"/>
    /// or not a defined value. The decision has no safe default, so it throws rather than picking one.</exception>
    public static bool IsSuperseded(in SubjectPropertyChange change, ChangeDeliveryRule rule)
    {
        return !ChangeDeliveryFilter.IsCurrent(in change, rule);
    }
}
