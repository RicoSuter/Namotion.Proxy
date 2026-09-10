using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal class OpcUaSubjectLoader
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    private readonly IInterceptorSubject _subject;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;

    public OpcUaSubjectLoader(
        IInterceptorSubject subject,
        OpcUaClientConfiguration configuration,
        SourceOwnershipManager ownership,
        OpcUaSubjectClientSource source,
        ILogger logger)
    {
        _subject = subject;
        _configuration = configuration;
        _ownership = ownership;
        _source = source;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        var monitoredItems = new List<MonitoredItem>();
        var loadedSubjects = new HashSet<IInterceptorSubject>();
        var subjectsByNodeId = new Dictionary<NodeId, IInterceptorSubject>();
        await LoadSubjectAsync(subject, node, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
        return monitoredItems;
    }

    private async Task LoadSubjectAsync(IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        if (!loadedSubjects.Add(subject))
        {
            return;
        }

        var registeredSubject = subject.TryGetRegisteredSubject();
        if (registeredSubject is null)
        {
            return;
        }

        var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);
        var nodeReferences = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        var distinctReferences = DistinctByResolvedNodeId(nodeReferences, session);

        foreach (var (nodeReference, resolvedNodeId) in distinctReferences)
        {
            var property = await FindSubjectPropertyAsync(registeredSubject, nodeReference, session, cancellationToken).ConfigureAwait(false);
            if (property is null)
            {
                var dynamicPropertyName = nodeReference.BrowseName.Name;

                var propertyExists = false;
                foreach (var childProperty in registeredSubject.Properties)
                {
                    if (childProperty.Name == dynamicPropertyName)
                    {
                        propertyExists = true;
                        break;
                    }
                }

                if (propertyExists)
                {
                    continue;
                }

                var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                    await _configuration.ShouldAddDynamicProperty(nodeReference, cancellationToken).ConfigureAwait(false);

                if (!addAsDynamic)
                {
                    continue;
                }

                // Infer CLR type from OPC UA variable metadata if possible
                var inferredType = await _configuration.TypeResolver!.TryGetTypeForNodeAsync(session, nodeReference, cancellationToken).ConfigureAwait(false);
                if (inferredType is null)
                {
                    _logger.LogWarning(
                        "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                        dynamicPropertyName, nodeReference.NodeId);

                    continue;
                }

                object? value = null;
                property = registeredSubject.AddProperty(
                    dynamicPropertyName,
                    inferredType,
                    _ => value,
                    (_, o) => value = o,
                    _configuration.TypeResolver!.GetDynamicPropertyAttributes(nodeReference, session));
            }

            // Resolve the mapping once; a null mapping means the property is not exposed (the old
            // ResolvePropertyName returned null in exactly that case). The reference branch reuses
            // it for the NodeClass check instead of resolving the same property a second time.
            if (_configuration.Mapper.TryGetMapping(property, _subject, out var mapping))
            {
                if (property.IsSubjectReference)
                {
                    // Check if this should be treated as a VariableNode
                    if (mapping.NodeClass == Mapping.OpcUaNodeClass.Variable)
                    {
                        await LoadVariableNodeForSubjectAsync(property, resolvedNodeId, session, monitoredItems, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await LoadSubjectReferenceAsync(property, nodeReference, resolvedNodeId, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (property.IsSubjectCollection)
                {
                    await LoadSubjectCollectionAsync(property, resolvedNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                }
                else if (property.IsSubjectDictionary)
                {
                    await LoadSubjectDictionaryAsync(property, resolvedNodeId, monitoredItems, session, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    MonitorValueNode(resolvedNodeId, property, monitoredItems);
                    var visitedNodes = new HashSet<NodeId>();
                    await LoadAttributeNodesAsync(property, resolvedNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task LoadAttributeNodesAsync(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<NodeId> visitedNodes,
        CancellationToken cancellationToken)
    {
        // Guard against cycles in OPC UA hierarchy
        if (!visitedNodes.Add(parentNodeId))
            return;

        // Browse children of the variable node
        var rawChildNodes = await BrowseNodeAsync(parentNodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);

        var processedBrowseNames = new HashSet<string>();

        // First pass: match known attributes from C# model
        foreach (var attribute in property.Attributes)
        {
            if (!_configuration.Mapper.TryGetMapping(attribute, _subject, out var mapping))
                continue;

            var attributeBrowseName = mapping.BrowseName ?? attribute.BrowseName;
            NodeId? matchingNodeId = null;
            foreach (var (childNode, childNodeId) in childNodes)
            {
                if (childNode.BrowseName.Name == attributeBrowseName)
                {
                    matchingNodeId = childNodeId;
                    break;
                }
            }

            if (matchingNodeId is null)
                continue;

            processedBrowseNames.Add(attributeBrowseName);
            MonitorValueNode(matchingNodeId, attribute, monitoredItems);

            // Recursive: attributes can have attributes
            await LoadAttributeNodesAsync(attribute, matchingNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
        }

        // Second pass: add dynamic attributes (same pattern as ShouldAddDynamicProperty)
        foreach (var (childNode, childNodeId) in childNodes)
        {
            if (childNode.NodeClass != NodeClass.Variable)
                continue;

            var browseName = childNode.BrowseName.Name;
            if (!processedBrowseNames.Add(browseName))
                continue;

            // Safety net for name collisions: a lifecycle handler from another source (e.g. HomeBlaze's
            // [StateAttribute]) may have registered a registry attribute under the same key as a standard
            // OPC UA browse-name child (e.g. Server.ServerStatus.State). Skip rather than crash on
            // duplicate AddAttribute; the existing registration wins.
            if (property.TryGetAttribute(browseName) is not null)
            {
                _logger.LogWarning(
                    "Skipping OPC UA child '{AttributeName}' on '{PropertyName}' (parent {ParentNodeId}, child {ChildNodeId}): an attribute with this name was already registered by another source (e.g. a lifecycle handler).",
                    browseName, property.Name, parentNodeId, childNode.NodeId);
                continue;
            }

            var addAsDynamic = _configuration.ShouldAddDynamicAttribute is not null &&
                await _configuration.ShouldAddDynamicAttribute(childNode, cancellationToken).ConfigureAwait(false);
            if (!addAsDynamic)
                continue;

            var inferredType = await _configuration.TypeResolver!.TryGetTypeForNodeAsync(session, childNode, cancellationToken).ConfigureAwait(false);
            if (inferredType is null)
            {
                _logger.LogWarning(
                    "Could not infer type for dynamic attribute '{AttributeName}' on property '{PropertyName}' (NodeId: {NodeId}). Skipping attribute.",
                    browseName, property.Name, childNode.NodeId);
                continue;
            }

            // Same closure pattern as dynamic properties
            object? value = null;
            var dynamicAttribute = property.AddAttribute(
                browseName,
                inferredType,
                _ => value,
                (_, o) => value = o,
                _configuration.TypeResolver!.GetDynamicPropertyAttributes(childNode, session));

            MonitorValueNode(childNodeId, dynamicAttribute, monitoredItems);

            // Recursive with cycle detection
            await LoadAttributeNodesAsync(dynamicAttribute, childNodeId, session, monitoredItems, visitedNodes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadSubjectReferenceAsync(RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        NodeId nodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        if (subjectsByNodeId.TryGetValue(nodeId, out var reusedSubject))
        {
            // The cache wins over property.Children: if a sibling reference loaded earlier in
            // this call already bound a subject to this NodeId, every other property pointing
            // at the same node must resolve to that same instance to preserve DAG identity.
            property.SetValueFromSource(_source, null, null, reusedSubject);
            return;
        }

        var existingChildren = property.Children;
        var existingChild = existingChildren.IsEmpty ? default : existingChildren[0];
        var isNewSubject = existingChild.Subject is null;
        var subjectToLoad = existingChild.Subject
            ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, session, cancellationToken).ConfigureAwait(false);

        if (isNewSubject)
        {
            // Assign before loading, as the collection and dictionary paths below do: the
            // assignment is what attaches the subject, and the load is registry-driven, so it
            // silently does nothing for a subject that is not in the graph yet.
            property.SetValueFromSource(_source, null, null, subjectToLoad);
        }

        // Pre-attached children participate in the dedup cache too: any later sibling
        // resolving to the same NodeId will route to this instance via the cache-hit
        // branch above, rather than creating a parallel subject.
        subjectsByNodeId.TryAdd(nodeId, subjectToLoad);

        await LoadSubjectAsync(subjectToLoad, nodeReference, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadSubjectCollectionAsync(RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        var rawChildNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);

        var childCount = childNodes.Count;
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childCount);

        var existingChildren = property.Children;

        for (var i = 0; i < childCount; i++)
        {
            var (childNode, nodeId) = childNodes[i];
            var childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;

            if (childSubject is null)
            {
                subjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, i, session, cancellationToken).ConfigureAwait(false);

            subjectsByNodeId.TryAdd(nodeId, childSubject);

            children.Add((childNode, childSubject));
        }

        var collection = DefaultSubjectFactory.Instance
            .CreateSubjectCollection(property.Type, children.Select(c => c.Subject));

        property.SetValueFromSource(_source, null, null, collection);

        // TODO(perf): Consider parallelizing child subject loading with Task.WhenAll.
        // Requires making monitoredItems and loadedSubjects thread-safe (e.g., ConcurrentBag, ConcurrentHashSet).
        foreach (var child in children)
        {
            await LoadSubjectAsync(child.Subject, child.Node, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadSubjectDictionaryAsync(
        RegisteredSubjectProperty property,
        NodeId childNodeId,
        List<MonitoredItem> monitoredItems,
        ISession session,
        HashSet<IInterceptorSubject> loadedSubjects,
        Dictionary<NodeId, IInterceptorSubject> subjectsByNodeId,
        CancellationToken cancellationToken)
    {
        var rawChildNodes = await BrowseNodeAsync(childNodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);
        var existingChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
        var entries = new Dictionary<object, IInterceptorSubject>();
        var nodesByKey = new Dictionary<object, ReferenceDescription>();

        foreach (var (childNode, nodeId) in childNodes)
        {
            var key = childNode.BrowseName.Name; // Use BrowseName as dictionary key
            var childSubject = existingChildren.GetValueOrDefault(key);

            if (childSubject is null)
            {
                subjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, key, session, cancellationToken).ConfigureAwait(false);

            subjectsByNodeId.TryAdd(nodeId, childSubject);

            entries[key] = childSubject;
            nodesByKey[key] = childNode;
        }

        var dictionary = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
        property.SetValueFromSource(_source, null, null, dictionary);

        foreach (var entry in entries)
        {
            var childNode = nodesByKey[entry.Key];
            await LoadSubjectAsync(entry.Value, childNode, session, monitoredItems, loadedSubjects, subjectsByNodeId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RegisteredSubjectProperty?> FindSubjectPropertyAsync(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        // Use Mapper for property lookup (supports attributes, path provider, and fluent config).
        // _subject is the connected root, so path-based mappers resolve paths consistently with the
        // forward direction (which also resolves relative to _subject).
        return await _configuration.Mapper.TryGetPropertyAsync(
            new OpcUaLookupKey(nodeReference, session, _subject), registeredSubject, cancellationToken).ConfigureAwait(false);
    }

    private void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, List<MonitoredItem> monitoredItems)
    {
        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property, _subject);
        property.Reference.SetPropertyData(_source.OpcUaNodeIdKey, nodeId);

        if (!_ownership.ClaimSource(property.Reference))
        {
            _logger.LogError(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        monitoredItems.Add(monitoredItem);
    }

    private async Task LoadVariableNodeForSubjectAsync(
        RegisteredSubjectProperty property,
        NodeId nodeId,
        ISession session,
        List<MonitoredItem> monitoredItems,
        CancellationToken cancellationToken)
    {
        var children = property.Children;
        var childSubject = (children.IsEmpty ? default : children[0]).Subject?.TryGetRegisteredSubject();
        if (childSubject == null)
        {
            return;
        }

        // Find [OpcUaValue] property and monitor the node for it
        var valuePropertyResult = childSubject.TryGetValueProperty(_configuration.Mapper, _subject);
        var valueProperty = valuePropertyResult?.Property;
        if (valueProperty != null)
        {
            MonitorValueNode(nodeId, valueProperty, monitoredItems);
        }

        // Also load HasProperty children as regular variable nodes
        var rawChildNodes = await BrowseNodeAsync(nodeId, session, cancellationToken).ConfigureAwait(false);
        var childNodes = DistinctByResolvedNodeId(rawChildNodes, session);
        foreach (var (childNode, childNodeId) in childNodes)
        {
            // Find matching property in child subject (excluding the value property)
            foreach (var childProperty in childSubject.Properties)
            {
                if (childProperty == valueProperty)
                {
                    continue; // Skip the value property
                }

                var childPropertyName = childProperty.ResolvePropertyName(_configuration.Mapper, _subject);
                if (childPropertyName == childNode.BrowseName.Name)
                {
                    MonitorValueNode(childNodeId, childProperty, monitoredItems);
                    break;
                }
            }
        }
    }

    // Dedup duplicate browse entries (e.g. same target via HasComponent + HasProperty).
    // Resolves each ExpandedNodeId via the session's NamespaceTable to produce a canonical
    // NodeId for the dedup key: ExpandedNodeId compares unequal when the same target is
    // expressed with NamespaceIndex vs NamespaceUri. References with an unresolvable
    // namespace URI (ToNodeId returns null) are skipped, since they cannot be addressed
    // for monitoring or further browsing.
    private List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        IReadOnlyCollection<ReferenceDescription> references,
        ISession session)
    {
        var seen = new HashSet<NodeId>(references.Count);
        var result = new List<(ReferenceDescription, NodeId)>(references.Count);
        foreach (var reference in references)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                _logger.LogWarning(
                    "Skipping browse reference '{BrowseName}' with unresolvable NodeId '{NodeId}': namespace URI is not registered in the session's NamespaceTable.",
                    reference.BrowseName?.Name, reference.NodeId);
                continue;
            }
            if (!seen.Add(nodeId))
            {
                continue;
            }
            result.Add((reference, nodeId));
        }
        return result;
    }

    private async Task<ReferenceDescriptionCollection> BrowseNodeAsync(
        NodeId nodeId,
        ISession session,
        CancellationToken cancellationToken)
    {
        var browseDescriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = NodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            }
        };

        var results = new ReferenceDescriptionCollection();

        var response = await session.BrowseAsync(
            null,
            null,
            _configuration.MaxReferencesPerNode,
            browseDescriptions,
            cancellationToken).ConfigureAwait(false);

        if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
        {
            results.AddRange(response.Results[0].References);

            var continuationPoint = response.Results[0].ContinuationPoint;
            while (continuationPoint is { Length: > 0 })
            {
                var nextResponse = await session.BrowseNextAsync(
                    null, false,
                    [continuationPoint], cancellationToken).ConfigureAwait(false);

                if (nextResponse.Results.Count > 0 && StatusCode.IsGood(nextResponse.Results[0].StatusCode))
                {
                    var r0 = nextResponse.Results[0];
                    if (r0.References is { Count: > 0 } nextReferences)
                    {
                        foreach (var reference in nextReferences)
                        {
                            results.Add(reference);
                        }
                    }
                    continuationPoint = r0.ContinuationPoint;
                }
                else
                {
                    break;
                }
            }
        }

        return results;
    }
}
