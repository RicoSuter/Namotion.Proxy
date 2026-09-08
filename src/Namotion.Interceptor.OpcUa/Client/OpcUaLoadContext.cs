using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Per-load staged context. All ownership claims and property bindings are queued during
/// discovery and applied by <see cref="Commit"/>. A failure before <see cref="Commit"/> leaves the
/// model at its pre-load state, except for the dynamic properties and attributes that discovery
/// adds eagerly, and <see cref="Dispose"/> then detaches the staged subjects so the registry sheds
/// them. A failure inside <see cref="Commit"/> releases the claims it established and restores the
/// bindings it applied.
/// Unrelated to <c>Namotion.Interceptor.Tracking.Transactions.SubjectTransaction</c>,
/// which captures property-change scopes for the tracking layer.
/// </summary>
internal sealed class OpcUaLoadContext : IDisposable
{
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;
    private readonly uint _maxReferencesPerNode;
    private readonly int _maxBrowseContinuations;
    private readonly ILogger _logger;
    private readonly Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> _browseCache = new();
    private readonly List<(PropertyReference Property, NodeId NodeId, MonitoredItem MonitoredItem)> _pendingClaims = new();
    private readonly Dictionary<PropertyReference, int> _pendingClaimIndices = new(PropertyReference.Comparer);
    private readonly List<(RegisteredSubjectProperty Property, object? Value)> _pendingBindings = new();
    private readonly List<(IInterceptorSubject Subject, IInterceptorSubjectContext ParentContext)> _stagedSubjects = new();
    private bool _committed;

    public OpcUaLoadContext(
        ISession session,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        uint maxReferencesPerNode,
        int maxBrowseContinuations,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Session = session;
        _ownership = ownership;
        _source = source;
        _maxReferencesPerNode = maxReferencesPerNode;
        _maxBrowseContinuations = maxBrowseContinuations;
        _logger = logger;
        CancellationToken = cancellationToken;
    }

    public ISession Session { get; }
    public List<MonitoredItem> MonitoredItems { get; } = new();
    public HashSet<IInterceptorSubject> LoadedSubjects { get; } = new();
    public Dictionary<NodeId, IInterceptorSubject> SubjectsByNodeId { get; } = new();
    public CancellationToken CancellationToken { get; }

    public NodeId? ResolveNodeId(ExpandedNodeId expandedNodeId)
    {
        return ExpandedNodeId.ToNodeId(expandedNodeId, Session.NamespaceUris);
    }

    public List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        IReadOnlyCollection<ReferenceDescription> references)
    {
        return Session.DistinctByResolvedNodeId(references, _logger);
    }

    public async Task<Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>> BrowseAsync(
        IReadOnlyCollection<NodeId> nodeIds)
    {
        var view = new Dictionary<NodeId, IReadOnlyList<ReferenceDescription>>(nodeIds.Count);
        List<NodeId>? missing = null;
        foreach (var nodeId in nodeIds)
        {
            if (view.ContainsKey(nodeId))
            {
                continue;
            }

            if (_browseCache.TryGetValue(nodeId, out var cached))
            {
                view[nodeId] = cached;
            }
            else
            {
                (missing ??= new List<NodeId>(nodeIds.Count)).Add(nodeId);
            }
        }

        if (missing is { Count: > 0 })
        {
            var results = await Session.BrowseNodesAsync(
                missing,
                _maxReferencesPerNode,
                _maxBrowseContinuations,
                _logger,
                CancellationToken).ConfigureAwait(false);

            foreach (var (nodeId, refs) in results)
            {
                _browseCache[nodeId] = refs;
                view[nodeId] = refs;
            }
        }

        return view;
    }

    /// <summary>
    /// Queues a source-ownership claim and its associated monitored item. Both are
    /// applied atomically during <see cref="Commit"/>: the monitored item is only added
    /// to <see cref="MonitoredItems"/> on successful claim, so a property that's owned
    /// by a different source by the time Commit runs never gets monitored. Duplicate
    /// claims for the same property (graph-shaped address spaces where the same
    /// PropertyReference is reached via multiple paths) are deduped so a property
    /// never gets monitored twice; when the duplicate carries a different NodeId,
    /// the smaller NodeId wins so the outcome is reproducible across loads regardless
    /// of browse order. On rollback, the entry is discarded.
    /// </summary>
    public void QueueClaim(PropertyReference property, NodeId nodeId, MonitoredItem monitoredItem)
    {
        if (_pendingClaimIndices.TryGetValue(property, out var index))
        {
            var existing = _pendingClaims[index];
            if (existing.NodeId != nodeId)
            {
                _logger.LogWarning(
                    "Duplicate claim for {Subject}.{Property} with different NodeId (existing: {ExistingNodeId}, new: {NewNodeId}). Keeping the smaller NodeId for deterministic outcome.",
                    property.Subject.GetType().Name, property.Name, existing.NodeId, nodeId);
                if (nodeId.CompareTo(existing.NodeId) < 0)
                {
                    _pendingClaims[index] = (property, nodeId, monitoredItem);
                }
            }
            return;
        }
        _pendingClaimIndices[property] = _pendingClaims.Count;
        _pendingClaims.Add((property, nodeId, monitoredItem));
    }

    /// <summary>
    /// Queues a property binding that <see cref="Commit"/> applies in queue order through
    /// <c>SetValueFromSource</c>. On rollback, the entry is discarded.
    /// </summary>
    public void QueueBinding(RegisteredSubjectProperty property, object? value)
    {
        _pendingBindings.Add((property, value));
    }

    /// <summary>
    /// Registers a newly constructed subject and adds the parent context as fallback so the
    /// subject can resolve services (registry, interceptors) during discovery. Uses the immediate
    /// parent context rather than the root context, so that in the ordinary tree case the link
    /// matches the one <c>ContextInheritanceHandler</c> removes when the subject's last property
    /// reference goes away. The handler adds no link of its own for a staged subject, because its
    /// add is gated on the subject not already being context-attached, so this link is the only
    /// one. <see cref="Dispose"/> undoes it for every staged subject that nothing references.
    /// </summary>
    public void RegisterStagedSubject(IInterceptorSubject subject, IInterceptorSubjectContext parentContext)
    {
        // Record the rollback entry BEFORE the side effect. If AddFallbackContext throws
        // or _stagedSubjects.Add throws after the side effect, the link could leak;
        // recording first ensures rollback always sees what was actually added.
        _stagedSubjects.Add((subject, parentContext));
        try
        {
            subject.Context.AddFallbackContext(parentContext);
        }
        catch
        {
            _stagedSubjects.RemoveAt(_stagedSubjects.Count - 1);
            throw;
        }
    }

    /// <summary>
    /// Commits the load: claims source ownership for every queued property, then applies the
    /// queued bindings in queue order. Claims run first so an observer that sees a new child
    /// appear finds all of the child's leaves already source-owned. A queued claim whose subject
    /// the application detached during the load is dropped.
    /// </summary>
    /// <remarks>
    /// If anything throws, the claims this call established are released while ownership from a
    /// previous load is kept, the bindings this call applied are restored in reverse order unless
    /// the property has been re-set since, and <see cref="MonitoredItems"/> is cleared before the
    /// exception is rethrown.
    /// </remarks>
    public void Commit()
    {
        var committedClaims = new List<PropertyReference>(_pendingClaims.Count);
        var appliedBindings = new List<(RegisteredSubjectProperty Property, object? PreviousValue, object? AssignedValue)>(_pendingBindings.Count);
        try
        {
            foreach (var (property, nodeId, monitoredItem) in _pendingClaims)
            {
                if (property.Subject.TryGetRegisteredSubject() is null)
                {
                    _logger.LogDebug(
                        "Skipping claim for {Subject}.{Property}: the subject was detached during the load.",
                        property.Subject.GetType().Name, property.Name);
                    continue;
                }

                // A reload re-claims properties this source already owns from a previous
                // successful load (ClaimSource is idempotent-true for the same source).
                // Track only claims newly established by THIS Commit so the rollback below
                // cannot strip pre-existing ownership it never created, which would leave
                // application writes unrouted until the next successful retry.
                var alreadyOwned = property.TryGetSource(out var existingSource) &&
                    ReferenceEquals(existingSource, _source);

                if (!_ownership.ClaimSource(property))
                {
                    _logger.LogError(
                        "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                        property.Subject.GetType().Name, property.Name);
                    continue;
                }
                if (!alreadyOwned)
                {
                    committedClaims.Add(property);
                }
                property.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);
                MonitoredItems.Add(monitoredItem);
            }

            foreach (var (property, value) in _pendingBindings)
            {
                appliedBindings.Add((property, property.GetValue(), value));
                property.SetValueFromSource(_source, null, null, value);
            }

            _committed = true;
        }
        catch
        {
            foreach (var property in committedClaims)
            {
                try { _ownership.ReleaseSource(property); }
                catch (Exception releaseException)
                {
                    _logger.LogWarning(releaseException,
                        "Failed to release source ownership for {Subject}.{Property} during Commit rollback.",
                        property.Subject.GetType().Name, property.Name);
                }
            }

            // A property that no longer holds this load's value was re-set by the application
            // since the write, and that value wins over the pre-load one.
            for (var i = appliedBindings.Count - 1; i >= 0; i--)
            {
                var (property, previousValue, assignedValue) = appliedBindings[i];
                if (!ReferenceEquals(property.GetValue(), assignedValue))
                {
                    continue;
                }

                try { property.SetValueFromSource(_source, null, null, previousValue); }
                catch (Exception restoreException)
                {
                    _logger.LogWarning(restoreException,
                        "Failed to restore {Subject}.{Property} during Commit rollback.",
                        property.Subject.GetType().Name, property.Name);
                }
            }

            MonitoredItems.Clear();
            throw;
        }
    }

    public void Dispose()
    {
        if (_committed) return;

        // Only a staged subject that nothing references is shed: one that gained a property
        // reference belongs to the model now, and detaching it would evict it from the registry
        // while that property still points at it. Deepest first, because a nested staged subject
        // reaches the lifecycle interceptor through its parent's fallback chain. The loop repeats
        // because detaching one subject releases its own references and can drop another staged
        // subject to zero. Each detach is guarded so one failure neither strands the rest nor
        // masks the load's own exception.
        bool removedAny;
        do
        {
            removedAny = false;
            for (var i = _stagedSubjects.Count - 1; i >= 0; i--)
            {
                var (staged, parentContext) = _stagedSubjects[i];
                if (staged.GetReferenceCount() != 0)
                {
                    continue;
                }

                try
                {
                    removedAny |= staged.Context.RemoveFallbackContext(parentContext);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to detach staged subject {Subject} from parent context during rollback.",
                        staged.GetType().Name);
                }
            }
        } while (removedAny);

        _stagedSubjects.Clear();
        _pendingClaimIndices.Clear();
        _pendingClaims.Clear();
        _pendingBindings.Clear();
    }
}
