using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Updates.Internal;

/// <summary>
/// Builder for creating a SubjectUpdate. Tracks IDs, subjects, and transformations.
/// Designed to be pooled and reused.
/// </summary>
internal sealed class SubjectUpdateBuilder
{
    private int _nextId;
    private readonly Dictionary<IInterceptorSubject, string> _subjectToId = new();
    private readonly Dictionary<SubjectPropertyUpdate, (RegisteredSubjectProperty Property, IDictionary<string, SubjectPropertyUpdate> Parent)> _propertyUpdates = new();

    public bool HasUnregisteredSubjects { get; set; }

    public ISubjectUpdateProcessor[] Processors { get; private set; } = [];
    
    public Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> Subjects { get; private set; } = new();

    public HashSet<IInterceptorSubject> ProcessedSubjects { get; } = [];

    public HashSet<IInterceptorSubject> PathVisited { get; } = [];

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

        // A later batch change can overwrite a reference whose subject has already detached.
        if (HasUnregisteredSubjects)
        {
            ValidateSubjectId(update.Root, update.Subjects);
            foreach (var properties in update.Subjects.Values)
            {
                foreach (var property in properties.Values)
                    ValidateReferences(property, update.Subjects);
            }
        }

        return update;
    }

    private static void ValidateReferences(SubjectPropertyUpdate property, Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
    {
        if (property.Kind == SubjectPropertyUpdateKind.Object)
            ValidateSubjectId(property.Id, subjects);
        else if (property.Kind is SubjectPropertyUpdateKind.Collection or SubjectPropertyUpdateKind.Dictionary)
        {
            if (property.Items is not null)
            {
                foreach (var item in property.Items)
                    ValidateSubjectId(item.Id, subjects);
            }
            if (property.Operations is not null)
            {
                foreach (var operation in property.Operations)
                {
                    if (operation.Action == SubjectCollectionOperationType.Insert)
                        ValidateSubjectId(operation.Id, subjects);
                }
            }
        }
        if (property.Attributes is not null)
        {
            foreach (var attribute in property.Attributes.Values)
                ValidateReferences(attribute, subjects);
        }
    }

    private static void ValidateSubjectId(string? subjectId, Dictionary<string, Dictionary<string, SubjectPropertyUpdate>> subjects)
    {
        if (subjectId is not null && !subjects.ContainsKey(subjectId))
            throw new NotSupportedException($"Subject '{subjectId}' selected for an update has no Registry metadata. Register the subject or exclude its referencing property with an ISubjectUpdateProcessor.");
    }

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
