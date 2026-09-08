using HomeBlaze.Abstractions;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History;

/// <summary>
/// Single source of truth for whether a property is recorded and whether the UI offers the history
/// action. Reads the HomeBlaze registry attribute (there is no IsState member on
/// <see cref="RegisteredSubjectProperty"/>: "is state" is the presence of the HB:State attribute, and
/// "has children" is <see cref="RegisteredSubjectProperty.CanContainSubjects"/>), then defers to
/// <see cref="HistoryColumns.IsRecordable"/> for the type half.
///
/// It lives here rather than in HomeBlaze.History.Abstractions because it is the one piece of
/// eligibility that needs the object graph, and that assembly is deliberately graph-free.
/// </summary>
public static class HistoryEligibility
{
    /// <summary>
    /// Returns true if the property is a recordable scalar [State] property.
    /// </summary>
    public static bool HasHistory(this RegisteredSubjectProperty property)
    {
        if (property.TryGetAttribute(KnownAttributes.State) is null) return false; // not [State]
        if (property.CanContainSubjects) return false;                             // structural (v1.1)
        return HistoryColumns.IsRecordable(property.Type);
    }
}
