using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// Runs every item of a loop even when one throws, then reports the failures together, so a single
/// failure cannot skip the remaining iterations.
/// </summary>
internal static class ExceptionAggregation
{
    /// <summary>
    /// Applies <paramref name="action"/> to every item, isolating each one's exception, then throws
    /// what was collected (see <see cref="ThrowIfAny"/>).
    /// </summary>
    internal static void ForEach<T>(IEnumerable<T> items, Action<T> action)
    {
        List<Exception>? exceptions = null;

        foreach (var item in items)
        {
            try
            {
                action(item);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        ThrowIfAny(exceptions);
    }

    /// <summary>
    /// Throws the single collected exception directly, or an <see cref="AggregateException"/> when
    /// more than one was collected. Does nothing when <paramref name="exceptions"/> is null or empty.
    /// </summary>
    internal static void ThrowIfAny(List<Exception>? exceptions)
    {
        if (exceptions is { Count: 1 })
        {
            // ExceptionDispatchInfo preserves the original stack trace; a bare `throw exceptions[0]`
            // resets it to this rethrow site, hiding where the exception actually came from.
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }

        if (exceptions is { Count: > 1 })
        {
            throw new AggregateException(exceptions);
        }
    }
}
