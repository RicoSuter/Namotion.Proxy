using System.Collections.Frozen;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// One <see cref="IInterceptorSubject.AddProperties"/> call in flight: the subject, the caller's
/// metadata sequence, and the continuation that publishes the complete property lookup. Core owns
/// the input contract (the sequence is materialized exactly once and duplicate names reject the
/// whole batch before anything publishes); the admitting side decides when to force
/// materialization and when to publish, so it can reject the batch before any state escapes.
/// </summary>
/// <remarks>
/// The input sequence must be synchronous, stable, and free of topology and metadata side effects:
/// it is enumerated exactly once, after callback admission, and enumerating it must not add or
/// remove properties, change ownership, or re-enter the subject. An iterator that violates this
/// receives no replay and no rollback. The publication continuation is invoked zero times (the
/// batch was rejected) or exactly once, synchronously, with the complete merged lookup; it must
/// be exception-free and only assign the lookup. A continuation that mutates other state and then
/// throws violates the publication contract and no rollback is attempted.
/// </remarks>
public sealed class SubjectPropertyRegistration
{
    private readonly IEnumerable<SubjectPropertyMetadata> _properties;
    private readonly Action<IReadOnlyDictionary<string, SubjectPropertyMetadata>> _publishProperties;
    private SubjectPropertyMetadata[]? _materialized;
    private bool _published;

    /// <summary>
    /// Creates the registration for one <see cref="IInterceptorSubject.AddProperties"/> call.
    /// </summary>
    /// <param name="subject">The subject receiving the properties.</param>
    /// <param name="properties">The caller's metadata sequence; see the class remarks for its contract.</param>
    /// <param name="publishProperties">The continuation that assigns the complete merged lookup;
    /// see the class remarks for its contract.</param>
    public SubjectPropertyRegistration(
        IInterceptorSubject subject,
        IEnumerable<SubjectPropertyMetadata> properties,
        Action<IReadOnlyDictionary<string, SubjectPropertyMetadata>> publishProperties)
    {
        Subject = subject;
        _properties = properties;
        _publishProperties = publishProperties;
    }

    /// <summary>Gets the subject receiving the properties.</summary>
    public IInterceptorSubject Subject { get; }

    /// <summary>
    /// Materializes the input sequence, on the first call only, and validates that no name in the
    /// batch collides with an existing property or with another batch entry. A duplicate rejects
    /// the whole batch before any getter runs and before anything is published.
    /// </summary>
    /// <exception cref="InvalidOperationException">A property name is already defined on the
    /// subject or appears twice in the batch.</exception>
    public IReadOnlyList<SubjectPropertyMetadata> GetProperties()
    {
        if (_materialized is not null)
        {
            return _materialized;
        }

        var materialized = _properties as SubjectPropertyMetadata[] ?? _properties.ToArray();

        var existingProperties = Subject.Properties;
        HashSet<string>? batchNames = null;
        foreach (var metadata in materialized)
        {
            if (existingProperties.ContainsKey(metadata.Name) || !(batchNames ??= []).Add(metadata.Name))
            {
                throw new InvalidOperationException(
                    $"A property named '{metadata.Name}' is already defined on the subject " +
                    $"'{Subject.GetType().Name}' or appears twice in the batch. The whole batch " +
                    "was rejected and nothing was published.");
            }
        }

        _materialized = materialized;
        return materialized;
    }

    /// <summary>
    /// Builds the complete merged lookup from the subject's current properties and the materialized
    /// batch and invokes the publication continuation with it, exactly once. An empty batch
    /// publishes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch is invalid (see
    /// <see cref="GetProperties"/>) or was already published.</exception>
    public void Publish()
    {
        var batch = GetProperties();
        if (batch.Count == 0)
        {
            return;
        }

        if (_published)
        {
            throw new InvalidOperationException(
                "The property batch was already published; the publication continuation is invoked at most once.");
        }

        var existingProperties = Subject.Properties;
        var merged = new Dictionary<string, SubjectPropertyMetadata>(existingProperties.Count + batch.Count);
        foreach (var pair in existingProperties)
        {
            merged.Add(pair.Key, pair.Value);
        }

        // Recheck rather than assign blindly: GetProperties validated the names against the state
        // it materialized from, so a collision here means an unsupported reentrant mutation (for
        // example a getter that added properties) and must fail loudly rather than silently
        // replace a property.
        for (var index = 0; index < batch.Count; index++)
        {
            var metadata = batch[index];
            if (merged.ContainsKey(metadata.Name))
            {
                throw new InvalidOperationException(
                    $"A property named '{metadata.Name}' was added to the subject " +
                    $"'{Subject.GetType().Name}' while this batch was being admitted, which the " +
                    "input contract forbids. The batch was not published.");
            }

            merged.Add(metadata.Name, metadata);
        }

        _published = true;
        _publishProperties(merged.ToFrozenDictionary());
    }
}
