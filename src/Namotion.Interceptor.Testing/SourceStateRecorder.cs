using System.Collections.Concurrent;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Records the state transitions a source raises, so that a test can assert on the transitions that
/// happened instead of on whichever state happens to be current when it looks.
/// </summary>
/// <remarks>
/// <see cref="ISubjectSource.StateChanged"/> is raised inside the source's transition lock, so a
/// subscriber observes every transition however short-lived the state it enters is, and a recorded
/// transition stays recorded whatever the source does next. That is what makes waiting for a
/// recorded transition race-free: polling <see cref="ISubjectSource.State"/> instead can miss a
/// transient state such as <see cref="SourceState.Synchronizing"/> entirely, because the state can
/// be entered and left again between two samples.
/// <para>
/// The handler only enqueues, which is what the observe-only contract on that event requires.
/// </para>
/// </remarks>
public sealed class SourceStateRecorder : IDisposable
{
    private readonly ISubjectSource _source;
    private readonly ConcurrentQueue<SourceEvent> _transitions = new();

    private SourceStateRecorder(ISubjectSource source)
    {
        _source = source;
        source.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Starts recording the transitions of <paramref name="source"/>. Subscribe before the code under
    /// test can make the source transition.
    /// </summary>
    public static SourceStateRecorder SubscribeTo(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SourceStateRecorder(source);
    }

    /// <summary>
    /// Gets the transitions recorded so far, oldest first.
    /// </summary>
    public IReadOnlyList<SourceEvent> Transitions => _transitions.ToArray();

    /// <summary>
    /// Waits until the recorded transitions enter <paramref name="states"/> in that order, and returns
    /// the transitions that matched. Other transitions may sit between them.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// Thrown when the states are not reached in order within <paramref name="timeout"/>. The message
    /// lists every transition that was recorded instead.
    /// </exception>
    public async Task<IReadOnlyList<SourceEvent>> WaitForStatesAsync(
        TimeSpan timeout, string message, params SourceState[] states)
    {
        if (states.Length == 0)
        {
            throw new ArgumentException("At least one state is required.", nameof(states));
        }

        IReadOnlyList<SourceEvent>? matched = null;
        try
        {
            await AsyncTestHelpers
                .WaitUntilAsync(() => (matched = TryMatchStates(states)) is not null, timeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"{message} Expected the states {string.Join(" -> ", states)} in order, " +
                $"but the recorded transitions were: {this}.",
                exception);
        }

        return matched!;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _source.StateChanged -= OnStateChanged;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var transitions = _transitions.ToArray();
        return transitions.Length == 0
            ? "(none)"
            : string.Join(", ", transitions.Select(transition =>
                $"{transition.OldState} -> {transition.NewState} at {transition.Timestamp:HH:mm:ss.fff}"));
    }

    private IReadOnlyList<SourceEvent>? TryMatchStates(SourceState[] states)
    {
        var matched = new List<SourceEvent>(states.Length);
        foreach (var transition in _transitions)
        {
            if (transition.NewState != states[matched.Count])
            {
                continue;
            }

            matched.Add(transition);
            if (matched.Count == states.Length)
            {
                return matched;
            }
        }

        return null;
    }

    private void OnStateChanged(object? sender, SourceEvent sourceEvent)
    {
        _transitions.Enqueue(sourceEvent);
    }
}
