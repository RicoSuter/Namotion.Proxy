using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builder for creating a SubjectUpdate. Tracks IDs, subjects, and transformations.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateBuilder
{
    private const string LoggerCategory = "Namotion.Interceptor.Connectors.Updates";

    private int _nextId;
    private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SubjectPropertyUpdate, (RegisteredSubjectProperty Property, IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

    /// <summary>
    /// Set when a subject reached while building carried no Registry metadata, which means the update
    /// holds an id no payload will ever be written for.
    /// </summary>
    public bool HasUnregisteredSubjects { get; set; }

    public ISubjectUpdateProcessor[] Processors { get; private set; } = [];
    
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = new();

    public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = new(ReferenceEqualityComparer.Instance);

    public HashSet<IInterceptorSubject> PathVisited { get; } = new(ReferenceEqualityComparer.Instance);

    public void Initialize(IInterceptorSubject rootSubject, ISubjectUpdateProcessor[] processors)
    {
        Processors = processors;
        GetOrCreateId(rootSubject); // Ensure root subject gets ID "1"
    }

    public string GetOrCreateId(IInterceptorSubject subject)
    {
        if (!_subjectToId.TryGetValue(subject, out var id))
        {
            id = (++_nextId).ToString();
            _subjectToId[subject] = id;
        }
        return id;
    }

    /// <summary>
    /// Gets an existing ID for a subject, or creates a new one.
    /// Returns true if a new ID was created, false if the subject already had an ID.
    /// </summary>
    public (string Id, bool IsNew) GetOrCreateIdWithStatus(IInterceptorSubject subject)
    {
        if (_subjectToId.TryGetValue(subject, out var id))
        {
            return (id, false);
        }

        id = (++_nextId).ToString();
        _subjectToId[subject] = id;
        return (id, true);
    }

    public Dictionary<string, SubjectPropertyUpdate> GetOrCreateProperties(string subjectId)
    {
        if (!Subjects.TryGetValue(subjectId, out var properties))
        {
            properties = new Dictionary<string, SubjectPropertyUpdate>();
            Subjects[subjectId] = properties;
        }
        return properties;
    }

    public bool SubjectHasUpdates(IInterceptorSubject subject)
    {
        if (_subjectToId.TryGetValue(subject, out var id))
        {
            return Subjects.TryGetValue(id, out var properties) && properties.Count > 0;
        }
        return false;
    }

    public void TrackPropertyUpdate(
        SubjectPropertyUpdate update,
        RegisteredSubjectProperty property,
        IDictionary<string, SubjectPropertyUpdate> parent)
    {
        if (Processors.Length > 0)
        {
            _propertyUpdates[update] = (property, parent);
        }
    }

    /// <summary>
    /// Builds the final SubjectUpdate, applying all transformations.
    /// </summary>
    public SubjectUpdate Build(IInterceptorSubject subject)
    {
        ApplyTransformations();

        var rootId = GetOrCreateId(subject);
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = Subjects
        };

        for (var i = 0; i < Processors.Length; i++)
        {
            update = Processors[i].TransformSubjectUpdate(subject, update);
        }

        // Only walk the result when the build actually reached a subject without Registry metadata,
        // which a well-formed batch still does whenever a later change detached an earlier reference.
        if (HasUnregisteredSubjects)
        {
            OmitDanglingReferences(subject, update);
        }

        return update;
    }

    /// <summary>
    /// Drops every property whose reference points at a subject the update carries no payload for, and
    /// reports what was dropped. A subject without Registry metadata contributes no payload, so keeping
    /// its id would put a reference on the wire that the receiver rejects, and clearing the reference
    /// instead would tell the receiver to null a property the source never cleared. Dropping the
    /// property leaves the receiver's own value alone, which is the only honest reading of a reference
    /// the wire cannot carry.
    /// <para>
    /// Deliberately not an exception: a complete update is also the snapshot a connector sends on every
    /// handshake, and a model is allowed to project detached subjects from derived properties, so
    /// throwing here would drop every client of such a model at connect time.
    /// </para>
    /// </summary>
    private static void OmitDanglingReferences(IInterceptorSubject rootSubject, SubjectUpdate update)
    {
        List<string>? omittedProperties = null;
        foreach (var properties in update.Subjects.Values)
        {
            OmitDanglingReferences(properties, update.Subjects, ref omittedProperties);
        }

        if (omittedProperties is null)
        {
            return;
        }

        var logger = rootSubject.Context.TryGetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
        if (logger?.IsEnabled(LogLevel.Warning) == true)
        {
            logger.LogWarning(
                "Omitted the update properties {OmittedProperties} of subject {SubjectType} because they reference " +
                "subjects without Registry metadata. Register the referenced subjects or exclude these properties " +
                "with an ISubjectUpdateProcessor.",
                string.Join(", ", omittedProperties), rootSubject.GetType().FullName);
        }
    }

    private static void OmitDanglingReferences(
        Dictionary<string, SubjectPropertyUpdate> properties,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects,
        ref List<string>? omittedProperties)
    {
        List<string>? danglingProperties = null;
        foreach (var (name, property) in properties)
        {
            if (HasDanglingReference(property, subjects))
            {
                (danglingProperties ??= []).Add(name);
            }
            else if (property.Attributes is not null)
            {
                OmitDanglingReferences(property.Attributes, subjects, ref omittedProperties);
            }
        }

        if (danglingProperties is null)
        {
            return;
        }

        foreach (var name in danglingProperties)
        {
            properties.Remove(name);
        }

        (omittedProperties ??= []).AddRange(danglingProperties);
    }

    private static bool HasDanglingReference(
        SubjectPropertyUpdate property,
        Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
    {
        switch (property.Kind)
        {
            case SubjectPropertyUpdateKind.Object:
                return IsDangling(property.Id, subjects);

            case SubjectPropertyUpdateKind.Collection:
            case SubjectPropertyUpdateKind.Dictionary:
                if (property.Items is not null)
                {
                    foreach (var item in property.Items)
                    {
                        if (IsDangling(item.Id, subjects))
                            return true;
                    }
                }

                if (property.Operations is not null)
                {
                    foreach (var operation in property.Operations)
                    {
                        // Only an insert carries a payload; a remove or a move needs nothing but its index.
                        if (operation.Action == SubjectCollectionOperationType.Insert && IsDangling(operation.Id, subjects))
                            return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsDangling(string? subjectId, Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
        => subjectId is not null && !subjects.ContainsKey(subjectId);

    /// <summary>
    /// Clears the builder for reuse. Call before returning to pool.
    /// </summary>
    public void Clear()
    {
        _nextId = 0;
        HasUnregisteredSubjects = false;
        _subjectToId.Clear();
        _propertyUpdates.Clear();
        ProcessedSubjects.Clear();
        PathVisited.Clear();
        Subjects = new(); // create a fresh dictionary, old one transferred to result
        Processors = [];
    }

    private void ApplyTransformations()
    {
        if (Processors.Length == 0)
            return;

        foreach (var (update, info) in _propertyUpdates)
        {
            for (var i = 0; i < Processors.Length; i++)
            {
                var transformed = Processors[i].TransformSubjectPropertyUpdate(info.Property, update);
                if (transformed != update)
                {
                    // Use AttributeName for attributes, Name for regular properties
                    var key = info.Property.IsAttribute
                        ? info.Property.AttributeMetadata.AttributeName
                        : info.Property.Name;

                    info.Parent[key] = transformed;
                }
            }
        }
    }
}
