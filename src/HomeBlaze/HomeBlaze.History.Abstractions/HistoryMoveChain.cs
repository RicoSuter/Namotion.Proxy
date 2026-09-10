namespace HomeBlaze.History.Abstractions;

/// <summary>
/// A recorded path move: the instant a property's canonical path changed from <c>FromPath</c> to
/// <c>ToPath</c>.
/// </summary>
public readonly record struct HistoryMove(DateTimeOffset Timestamp, string FromPath, string ToPath);

/// <summary>
/// One leg of a queried path's move chain: the path the property used during [ValidFrom, ValidTo).
/// </summary>
public readonly record struct HistoryChainLeg(string Path, DateTimeOffset ValidFrom, DateTimeOffset ValidTo);

/// <summary>
/// Resolves the chain of paths a property has occupied, so a query against its current path also
/// reads the samples it recorded under earlier ones.
///
/// Pure and store-independent, and shared rather than reimplemented per engine: every store must
/// resolve identical legs from identical moves, otherwise the same query answers differently
/// depending on which one served it. The two hand-written copies this replaced had in fact drifted.
/// </summary>
public static class HistoryMoveChain
{
    /// <summary>
    /// Walks <paramref name="moves"/> backwards from <paramref name="currentPath"/> and returns the
    /// legs, newest first, each scoped to its half-open validity window. With no moves this is a
    /// single unbounded leg [MinValue, MaxValue), so an unmoved path resolves to itself.
    /// </summary>
    public static List<HistoryChainLeg> Resolve(IReadOnlyList<HistoryMove> moves, string currentPath)
    {
        var legs = new List<HistoryChainLeg>();
        var path = currentPath;
        var validTo = DateTimeOffset.MaxValue;

        // Bounded by the move count rather than by distinct paths: a property that moves away and
        // later returns visits the same path twice, in two disjoint validity windows. Stopping at the
        // repeat dropped the earlier window, hiding samples that are still retained and still inside
        // coverage. Each step strictly lowers validTo, so the walk cannot cycle.
        for (var step = 0; step <= moves.Count; step++)
        {
            // Latest move INTO this path before validTo gives the instant the property arrived here.
            // Strictly before, because a leg's window is half-open: a move at exactly validTo starts
            // the next leg, and admitting it here would produce an empty one.
            HistoryMove? arrival = null;
            foreach (var move in moves)
            {
                if (StringComparer.Ordinal.Equals(move.ToPath, path) && move.Timestamp < validTo &&
                    (arrival is null || move.Timestamp > arrival.Value.Timestamp))
                {
                    arrival = move;
                }
            }

            var validFrom = arrival?.Timestamp ?? DateTimeOffset.MinValue;
            legs.Add(new HistoryChainLeg(path, validFrom, validTo));

            if (arrival is null)
            {
                break; // reached the original path
            }

            path = arrival.Value.FromPath;
            validTo = arrival.Value.Timestamp;
        }

        return legs;
    }
}
