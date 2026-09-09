using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

/// <summary>
/// Registers subjects and their property edges as they enter the object graph.
/// </summary>
/// <remarks>
/// Runs before <see cref="LifecycleInterceptor"/>'s handler slot, which walks down into a newly
/// attached subtree, so a subject is registered before the descent reaches its children. That holds
/// at every level, so while attaching, any handler running at or behind this one finds every
/// ancestor of a subject already registered. Detach does not mirror that; see the design doc and
/// "Handler Order" in docs/design/tracking-lifecycle.md.
///
/// A projection only: no registry state participates in ownership or reachability.
/// </remarks>
[RunsBefore(typeof(LifecycleInterceptor))]
public class SubjectRegistry : ISubjectRegistry, ISubjectIdRegistry, ISubjectIdRegistryWriter, ILifecycleHandler, IPropertyLifecycleHandler
{
    private static readonly ImmutableDictionary<IInterceptorSubject, RegisteredSubject> EmptyKnownSubjects =
        ImmutableDictionary.Create<IInterceptorSubject, RegisteredSubject>(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<IInterceptorSubject, RegisteredSubject> _knownSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IInterceptorSubject> _subjectIdToSubject = new();
    private ImmutableDictionary<IInterceptorSubject, RegisteredSubject>? _knownSubjectsSnapshot;

    /// <inheritdoc />
    public IReadOnlyDictionary<IInterceptorSubject, RegisteredSubject> KnownSubjects
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var snapshot = Volatile.Read(ref _knownSubjectsSnapshot);
            return snapshot ?? GetKnownSubjectsSlow();
        }
    }

    private ImmutableDictionary<IInterceptorSubject, RegisteredSubject> GetKnownSubjectsSlow()
    {
        lock (_knownSubjects)
        {
            var snapshot = _knownSubjectsSnapshot;
            if (snapshot is not null)
                return snapshot;

            snapshot = EmptyKnownSubjects.AddRange(_knownSubjects);
            Volatile.Write(ref _knownSubjectsSnapshot, snapshot);
            return snapshot;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegisteredSubject? TryGetRegisteredSubject(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _knownSubjects.GetValueOrDefault(subject);
        }
    }

    /// <inheritdoc />
    string ISubjectIdRegistryWriter.GetOrAddSubjectId(IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            var existing = subject.TryGetSubjectId();
            if (existing is not null)
                return existing;

            var id = SubjectRegistryExtensions.GenerateSubjectId();
            SubjectRegistryExtensions.HasSubjectIds = true;
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;

            // Only populate reverse index for attached subjects; the lifecycle
            // attach handler will register IDs from Data for unattached subjects.
            if (_knownSubjects.ContainsKey(subject))
            {
                _subjectIdToSubject[id] = subject;
            }

            return id;
        }
    }

    /// <inheritdoc />
    void ISubjectIdRegistryWriter.SetSubjectId(IInterceptorSubject subject, string id)
    {
        lock (_knownSubjects)
        {
            if (_subjectIdToSubject.TryGetValue(id, out var existing) && !ReferenceEquals(existing, subject))
            {
                throw new InvalidOperationException(
                    $"Subject ID '{id}' is already in use by a different subject.");
            }

            var oldId = subject.TryGetSubjectId();
            if (oldId is not null && oldId != id)
            {
                throw new InvalidOperationException(
                    $"Subject already has ID '{oldId}'; cannot reassign to '{id}'.");
            }

            SubjectRegistryExtensions.HasSubjectIds = true;
            subject.Data[(null, SubjectRegistryExtensions.SubjectIdKey)] = id;

            // Only populate reverse index for attached subjects; the lifecycle
            // attach handler will register IDs from Data for unattached subjects.
            if (_knownSubjects.ContainsKey(subject))
            {
                _subjectIdToSubject[id] = subject;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetSubjectById(string subjectId, out IInterceptorSubject subject)
    {
        lock (_knownSubjects)
        {
            return _subjectIdToSubject.TryGetValue(subjectId, out subject!);
        }
    }

    /// <inheritdoc />
    void ILifecycleHandler.HandleLifecycleChange(SubjectLifecycleChange change)
    {
        lock (_knownSubjects)
        {
            if (change.IsContextAttach || change.IsPropertyReferenceAdded)
            {
                if (!_knownSubjects.TryGetValue(change.Subject, out var registeredSubject))
                {
                    registeredSubject = RegisterSubject(change.Subject);
                }

                if (change.IsContextAttach)
                {
                    // Auto-register pre-assigned subject ID in reverse index;
                    // skip silently on conflict to avoid aborting the lifecycle.
                    var subjectId = change.Subject.TryGetSubjectId();
                    if (subjectId is not null)
                    {
                        if (!_subjectIdToSubject.TryGetValue(subjectId, out var existingSubject)
                            || ReferenceEquals(existingSubject, change.Subject))
                        {
                            _subjectIdToSubject[subjectId] = change.Subject;
                        }
                    }
                }

                if (change is { IsPropertyReferenceAdded: true, Property: { } property })
                {
                    if (!_knownSubjects.TryGetValue(property.Subject, out var parentRegisteredSubject))
                    {
                        parentRegisteredSubject = RegisterSubject(property.Subject);
                    }

                    var registeredProperty = parentRegisteredSubject.TryGetProperty(property.Name) ??
                        throw new InvalidOperationException($"Property '{property.Name}' not found.");

                    registeredSubject.AddParent(registeredProperty, change.Index);
                    registeredProperty.AddChild(new SubjectPropertyChild
                    {
                        Index = change.Index,
                        Subject = change.Subject,
                    });
                }

                return;
            }

            if (change.IsPropertyReferenceRemoved || change.IsContextDetach)
            {
                var registeredSubject = _knownSubjects.GetValueOrDefault(change.Subject);
                if (registeredSubject is not null)
                {
                    if (change is { IsPropertyReferenceRemoved: true, Property: not null })
                    {
                        var property = _knownSubjects
                            .GetValueOrDefault(change.Property.Value.Subject)?
                            .TryGetProperty(change.Property.Value.Name);

                        if (property is not null)
                        {
                            registeredSubject.RemoveParent(property, change.Index);
                         
                            property.RemoveChild(new SubjectPropertyChild
                            {
                                Subject = change.Subject,
                                Index = change.Index
                            });
                        }
                    }
                    
                    if (change.IsContextDetach)
                    {
                        // Remove stale parent references from children and clear
                        // children lists before this subject leaves _knownSubjects.
                        foreach (var property in registeredSubject.Properties)
                        {
                            if (!property.CanContainSubjects)
                                continue;

                            foreach (var child in property.Children)
                            {
                                var childRegistered = _knownSubjects.GetValueOrDefault(child.Subject);
                                childRegistered?.RemoveParentsByProperty(property);
                            }

                            property.ClearChildren();
                        }

                        _knownSubjects.Remove(change.Subject);
                        Volatile.Write(ref _knownSubjectsSnapshot, null);

                        // Clean up subject ID reverse index
                        if (_subjectIdToSubject.Count > 0)
                        {
                            var subjectId = change.Subject.TryGetSubjectId();
                            if (subjectId is not null)
                            {
                                _subjectIdToSubject.Remove(subjectId);
                            }
                        }
                    }
                }
            }
        }
    }

    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
        var property = TryGetRegisteredProperty(change.Property);
        if (property is null &&
            change.Subject.Properties.TryGetValue(change.Property.Name, out var metadata))
        {
            // A property admitted through AddProperties publishes its metadata and this callback
            // before any manual Registry insert can run, so the projection is created here, which
            // is also what makes it exist before the property's initial structural edge
            // notifications resolve it.
            property = TryGetRegisteredSubject(change.Subject)?
                .GetOrAddPropertyProjection(change.Property.Name, metadata.Type, metadata.Attributes);
        }

        if (property is not null)
        {
            // handle property initializers from attributes
            foreach (var attribute in property.ReflectionAttributes.OfType<ISubjectPropertyInitializer>())
            {
                attribute.InitializeProperty(property);
            }

            // handle property initializers from context; the subject is attached here, because
            // this runs from inside its own lifecycle change
            foreach (var initializer in change.Subject.GetContext().GetServices<ISubjectPropertyInitializer>())
            {
                initializer.InitializeProperty(property);
            }
        }
    }

    private RegisteredSubject RegisterSubject(IInterceptorSubject subject)
    {
        var registeredSubject = new RegisteredSubject(subject);
        _knownSubjects[subject] = registeredSubject;
        Volatile.Write(ref _knownSubjectsSnapshot, null);
        return registeredSubject;
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    void IPropertyLifecycleHandler.RefreshCollectionProperty(PropertyReference property, object? value)
    {
        RegisteredSubjectProperty? registeredProperty;
        lock (_knownSubjects)
        {
            registeredProperty = _knownSubjects
                .GetValueOrDefault(property.Subject)?
                .TryGetProperty(property.Name);
        }

        // Call outside lock — RefreshCollectionIndices updates parent entries;
        // holding _knownSubjects would risk deadlock.
        registeredProperty?.RefreshCollectionIndices(value, registry: this);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegisteredSubjectProperty? TryGetRegisteredProperty(PropertyReference property)
    {
        return TryGetRegisteredSubject(property.Subject)?.TryGetProperty(property.Name);
    }
}
