using System.Globalization;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OpcUaSubjectLoader
{
    private readonly IInterceptorSubject _subject;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly SourceOwnershipManager _ownership;
    private readonly OpcUaSubjectClientSource _source;
    private readonly ILogger _logger;
    private readonly OpcUaAttributeLoader _attributeLoader;

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
        _attributeLoader = new OpcUaAttributeLoader(subject, this, configuration, logger);
    }

    internal void MonitorValueNode(NodeId nodeId, RegisteredSubjectProperty property, OpcUaLoadContext context)
    {
        // Early exit only; the authoritative claim is made by Commit.
        if (property.Reference.TryGetSource(out var existing) && existing != _source)
        {
            _logger.LogError(
                "Property {Subject}.{Property} already owned by another source. Skipping OPC UA monitoring.",
                property.Subject.GetType().Name, property.Name);
            return;
        }

        var monitoredItem = MonitoredItemFactory.Create(_configuration, nodeId, property, _subject);
        context.QueueClaim(property.Reference, nodeId, monitoredItem);
    }

    private readonly record struct ChildEntry(
        ReferenceDescription Reference,
        NodeId ResolvedNodeId,
        RegisteredSubjectProperty? Property);

    private readonly record struct SubjectState(
        IInterceptorSubject Subject,
        RegisteredSubject Registered,
        List<ChildEntry> ChildEntries);

    public async Task<IReadOnlyList<MonitoredItem>> LoadSubjectAsync(
        IInterceptorSubject subject,
        ReferenceDescription node,
        ISession session,
        CancellationToken cancellationToken)
    {
        using var context = new OpcUaLoadContext(
            session,
            _ownership,
            _source,
            _configuration.MaxReferencesPerNode,
            _configuration.MaxBrowseContinuationRounds,
            _logger,
            cancellationToken);

        await LoadSubjectsAsync([(node, subject)], context).ConfigureAwait(false);
        context.Commit();
        return context.MonitoredItems;
    }

    private async Task LoadSubjectsAsync(
        IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
        OpcUaLoadContext context)
    {
        if (subjects.Count == 0)
        {
            return;
        }

        // Phase 1: Filter valid subjects, batch-browse
        var (validSubjects, allBrowseResults) = await FilterAndBrowseSubjectsAsync(subjects, context).ConfigureAwait(false);
        if (validSubjects.Count == 0)
        {
            return;
        }

        // Phase 2: Classify children, collect dynamic nodes
        var allDynamicObjectNodes = new Dictionary<NodeId, ReferenceDescription>();
        var allDynamicVariableNodes = new Dictionary<NodeId, ReferenceDescription>();
        var subjectStates = new List<SubjectState>(validSubjects.Count);

        foreach (var (subject, registeredSubject, subjectNodeId) in validSubjects)
        {
            var distinctReferences = context.DistinctByResolvedNodeId(allBrowseResults[subjectNodeId]);

            var childEntries = await ClassifyChildReferencesAsync(
                registeredSubject, distinctReferences,
                allDynamicObjectNodes, allDynamicVariableNodes,
                context).ConfigureAwait(false);

            subjectStates.Add(new SubjectState(subject, registeredSubject, childEntries));
        }

        // Phase 3: Batch resolve types (populates the context's browse cache for reuse by Collections and next-level Subjects)
        var objectTypeMap = await ResolveObjectTypesAsync(allDynamicObjectNodes, context).ConfigureAwait(false);

        var variableTypeMap = allDynamicVariableNodes.Count > 0
            ? await _configuration.TypeResolver!.ResolveVariableTypesAsync(context.Session, allDynamicVariableNodes.Values, context.CancellationToken).ConfigureAwait(false)
            : new Dictionary<NodeId, Type?>();

        // Phase 4: Dispatch properties and load children (Phase 5 attribute discovery runs at the end of LoadChildPropertiesAsync)
        await LoadChildPropertiesAsync(subjectStates, objectTypeMap, variableTypeMap, context).ConfigureAwait(false);
    }

    private async Task<(
        List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId NodeId)> ValidSubjects,
        Dictionary<NodeId, IReadOnlyList<ReferenceDescription>> BrowseResults)>
        FilterAndBrowseSubjectsAsync(
            IReadOnlyList<(ReferenceDescription Node, IInterceptorSubject Subject)> subjects,
            OpcUaLoadContext context)
    {
        var validSubjects = new List<(IInterceptorSubject Subject, RegisteredSubject RegisteredSubject, NodeId NodeId)>(subjects.Count);
        var subjectNodeIds = new List<NodeId>(subjects.Count);

        foreach (var (node, subject) in subjects)
        {
            if (!context.LoadedSubjects.Add(subject))
            {
                continue;
            }

            var registeredSubject = subject.TryGetRegisteredSubject();
            if (registeredSubject is null)
            {
                continue;
            }

            var resolved = context.ResolveNodeId(node.NodeId);
            if (resolved is null)
            {
                _logger.LogWarning(
                    "Skipping subject '{Subject}' (NodeId: {NodeId}): the namespace URI is not registered in the session's NamespaceTable. Its properties keep their current values and the next load reloads it.",
                    subject.GetType().Name, node.NodeId);
                continue;
            }

            validSubjects.Add((subject, registeredSubject, resolved));
            subjectNodeIds.Add(resolved);
        }

        var browseResults = await context.BrowseAsync(subjectNodeIds).ConfigureAwait(false);

        // A NodeId absent from the result was not browsed to completion.
        for (var i = validSubjects.Count - 1; i >= 0; i--)
        {
            var (subject, _, nodeId) = validSubjects[i];
            if (browseResults.ContainsKey(nodeId))
            {
                continue;
            }

            _logger.LogWarning(
                "Skipping subject '{Subject}' (NodeId: {NodeId}): its browse did not complete this load. Its properties keep their current values and the next load reloads it.",
                subject.GetType().Name, nodeId);
            validSubjects.RemoveAt(i);
        }

        return (validSubjects, browseResults);
    }

    private async Task<List<ChildEntry>>
        ClassifyChildReferencesAsync(
            RegisteredSubject registeredSubject,
            List<(ReferenceDescription Reference, NodeId NodeId)> distinctReferences,
            Dictionary<NodeId, ReferenceDescription> dynamicObjectNodes,
            Dictionary<NodeId, ReferenceDescription> dynamicVariableNodes,
            OpcUaLoadContext context)
    {
        var childEntries = new List<ChildEntry>(distinctReferences.Count);
        var stagedDynamicNames = new HashSet<string>();

        for (var i = 0; i < distinctReferences.Count; i++)
        {
            var (nodeReference, resolvedNodeId) = distinctReferences[i];

            var property = await _configuration.Mapper
                .TryGetPropertyAsync(new OpcUaLookupKey(nodeReference, context.Session, _subject), registeredSubject, context.CancellationToken)
                .ConfigureAwait(false);

            if (property is not null)
            {
                childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, property));
                continue;
            }

            if (registeredSubject.TryGetProperty(nodeReference.BrowseName.Name) is not null)
            {
                _logger.LogDebug(
                    "Skipping OPC UA child '{BrowseName}' (NodeId: {NodeId}): a property with this name is already declared on the registered subject but the mapper returned no mapping (likely a BrowseName collision with an unmapped property, or a mapping that targets a different NodeId).",
                    nodeReference.BrowseName.Name, nodeReference.NodeId);
                continue;
            }

            var addAsDynamic = _configuration.ShouldAddDynamicProperty is not null &&
                await _configuration.ShouldAddDynamicProperty(nodeReference, context.CancellationToken).ConfigureAwait(false);

            if (!addAsDynamic)
            {
                continue;
            }

            // A server may return two siblings with the same BrowseName but different NodeIds
            // (allowed when reached via different reference types). They survive
            // DistinctByResolvedNodeId because the NodeIds differ, and the TryGetProperty check
            // above does not catch them because the first is only added later in
            // LoadChildPropertiesAsync. Skip the second here so AddProperty is never called twice
            // with the same name (which would throw a duplicate-key ArgumentException).
            if (!stagedDynamicNames.Add(nodeReference.BrowseName.Name))
            {
                _logger.LogWarning(
                    "Skipping OPC UA child '{BrowseName}' (NodeId: {NodeId}): a sibling reference already staged a dynamic property with this BrowseName under the same parent.",
                    nodeReference.BrowseName.Name, nodeReference.NodeId);
                continue;
            }

            childEntries.Add(new ChildEntry(nodeReference, resolvedNodeId, null));
            if (nodeReference.NodeClass == NodeClass.Object)
            {
                dynamicObjectNodes.TryAdd(resolvedNodeId, nodeReference);
            }
            else if (nodeReference.NodeClass == NodeClass.Variable)
            {
                dynamicVariableNodes.TryAdd(resolvedNodeId, nodeReference);
            }
        }

        return childEntries;
    }

    private async Task<Dictionary<NodeId, Type>> ResolveObjectTypesAsync(Dictionary<NodeId, ReferenceDescription> objectNodes, OpcUaLoadContext context)
    {
        var objectTypeMap = new Dictionary<NodeId, Type>();
        if (objectNodes.Count == 0)
        {
            return objectTypeMap;
        }

        var objectBrowseResults = await context.BrowseAsync(objectNodes.Keys).ConfigureAwait(false);
        foreach (var (nodeId, node) in objectNodes)
        {
            // Missing entry = browse returned a bad status (BrowseNodesAsync deliberately
            // omits failed NodeIds so they aren't cached). Leave unset; TryCreateDynamicProperty
            // logs "Could not infer type" and skips, and the next load gets to retry.
            if (objectBrowseResults.TryGetValue(nodeId, out var children))
            {
                objectTypeMap[nodeId] = _configuration.TypeResolver!.ResolveObjectNodeType(node, children);
            }
        }

        return objectTypeMap;
    }

    private async Task LoadChildPropertiesAsync(
        List<SubjectState> subjectStates,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
        OpcUaLoadContext context)
    {
        var attributeVariableNodes = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingVariableSubjects = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var pendingContainers = new List<(RegisteredSubjectProperty Property, NodeId NodeId)>();
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>();

        // Two sibling references with different NodeIds can map to the same property
        // (legal OPC UA, e.g. reached via different reference types). For subject,
        // collection, and dictionary properties the assignments are deferred, so the
        // duplicate would create, stage, claim, and monitor a second subject tree that
        // the final assignment then overwrites, leaving committed orphans. Dedupe by
        // destination property; the first reference wins (browse order), matching the
        // dynamic-property sibling dedup. Plain values and Variable-typed subject
        // references keep their own smaller-NodeId dedup rules downstream.
        var reservedStructuredTargets = new HashSet<RegisteredSubjectProperty>();

        bool TryReserveStructuredTarget(RegisteredSubjectProperty targetProperty, ReferenceDescription reference, NodeId nodeId)
        {
            if (reservedStructuredTargets.Add(targetProperty))
            {
                return true;
            }

            _logger.LogWarning(
                "Skipping OPC UA child '{BrowseName}' (NodeId: {NodeId}): another sibling reference already targets property '{Subject}.{Property}' in this load; the first reference wins.",
                reference.BrowseName.Name, nodeId, targetProperty.Subject.GetType().Name, targetProperty.Name);
            return false;
        }

        foreach (var (stateSubject, stateRegisteredSubject, stateChildEntries) in subjectStates)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            for (var i = 0; i < stateChildEntries.Count; i++)
            {
                var (nodeReference, resolvedNodeId, property) = stateChildEntries[i];

                property ??= TryCreateDynamicProperty(
                    stateRegisteredSubject, nodeReference, resolvedNodeId,
                    objectTypeMap, variableTypeMap, context);

                if (property is null)
                {
                    continue;
                }

                if (!_configuration.Mapper.TryGetMapping(property, _subject, out var nodeConfiguration))
                {
                    continue;
                }

                if (property.IsSubjectReference)
                {
                    if (nodeConfiguration.NodeClass == OpcUaNodeClass.Variable)
                    {
                        pendingVariableSubjects.Add((property, resolvedNodeId));
                    }
                    else if (TryReserveStructuredTarget(property, nodeReference, resolvedNodeId))
                    {
                        var subjectToLoad = await PrepareSubjectReferenceAsync(
                            property, nodeReference, resolvedNodeId, stateSubject,
                            context).ConfigureAwait(false);

                        if (subjectToLoad is not null)
                        {
                            children.Add((nodeReference, subjectToLoad));
                        }
                    }
                }
                else if (property.IsSubjectCollection || property.IsSubjectDictionary)
                {
                    if (TryReserveStructuredTarget(property, nodeReference, resolvedNodeId))
                    {
                        pendingContainers.Add((property, resolvedNodeId));
                    }
                }
                else
                {
                    MonitorValueNode(resolvedNodeId, property, context);
                    attributeVariableNodes.Add((property, resolvedNodeId));
                }
            }
        }

        await LoadVariableSubjectReferencesAsync(pendingVariableSubjects, context).ConfigureAwait(false);
        await LoadCollectionsAndDictionariesAsync(pendingContainers, children, context).ConfigureAwait(false);

        // One recursion over every child of the level, whichever branch kind led to it, so the
        // level below is browsed in one call and the round-trip count grows with depth alone.
        await LoadSubjectsAsync(children, context).ConfigureAwait(false);
        await _attributeLoader.LoadAttributesAsync(attributeVariableNodes, context).ConfigureAwait(false);
    }

    private RegisteredSubjectProperty? TryCreateDynamicProperty(
        RegisteredSubject registeredSubject,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        Dictionary<NodeId, Type> objectTypeMap,
        IReadOnlyDictionary<NodeId, Type?> variableTypeMap,
        OpcUaLoadContext context)
    {
        Type? inferredType = null;
        if (nodeReference.NodeClass == NodeClass.Object)
        {
            objectTypeMap.TryGetValue(resolvedNodeId, out inferredType);
        }
        else if (nodeReference.NodeClass == NodeClass.Variable)
        {
            variableTypeMap.TryGetValue(resolvedNodeId, out inferredType);
        }

        if (inferredType is null)
        {
            _logger.LogWarning(
                "Could not infer type for dynamic property '{PropertyName}' (NodeId: {NodeId}). Skipping property.",
                nodeReference.BrowseName.Name, nodeReference.NodeId);
            return null;
        }

        object? value = null;
        // Added eagerly rather than staged in the load context: if the load fails later,
        // the property survives on non-staged subjects (root and reused children), but the
        // next load re-matches it via the mapper's NodeIdentifier priority (the attached
        // OpcUaNodeAttribute carries the exact NodeId) and re-monitors it, so the leftover
        // is transient rather than torn state.
        return registeredSubject.AddProperty(
            nodeReference.BrowseName.Name,
            inferredType,
            _ => value,
            (_, o) => value = o,
            _configuration.TypeResolver!.GetDynamicPropertyAttributes(nodeReference, context.Session));
    }

    /// <returns>
    /// The subject the level below loads for this reference, or null when another path in this
    /// load already produced the node's subject and loads it.
    /// </returns>
    private async Task<IInterceptorSubject?> PrepareSubjectReferenceAsync(
        RegisteredSubjectProperty property,
        ReferenceDescription nodeReference,
        NodeId resolvedNodeId,
        IInterceptorSubject parentSubject,
        OpcUaLoadContext context)
    {
        if (context.SubjectsByNodeId.TryGetValue(resolvedNodeId, out var reusedSubject))
        {
            context.QueueBinding(property, reusedSubject);
            return null;
        }

        var existingChildren = property.Children;
        var existingSubject = existingChildren.IsEmpty ? null : existingChildren[0].Subject;
        var subjectToLoad = existingSubject
            ?? await _configuration.SubjectFactory.CreateSubjectAsync(property, nodeReference, context.Session, context.CancellationToken).ConfigureAwait(false);

        if (existingSubject is null)
        {
            context.RegisterStagedSubject(subjectToLoad, parentSubject.Context);
            context.QueueBinding(property, subjectToLoad);
        }

        context.SubjectsByNodeId.TryAdd(resolvedNodeId, subjectToLoad);
        return subjectToLoad;
    }

    // Variable subjects' value properties intentionally don't enter `_attributeLoader.LoadAttributesAsync`.
    // OPC UA Variable attributes (DataType, ValueRank, AccessLevel, etc.) should be modeled as peer
    // properties of the Variable subject, which the child-name match below handles. C# `.Attributes`
    // declared on the Value property are not discovered here, by design, to avoid the model-vs-protocol
    // ambiguity when a browse-child matches both a peer property and an attribute name.
    private async Task LoadVariableSubjectReferencesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> variableNodes,
        OpcUaLoadContext context)
    {
        if (variableNodes.Count == 0)
        {
            return;
        }

        // Dedup by childSubject: when two parent properties reference the same Variable
        // subject (graph-shaped address space, not tree), pick the smaller NodeId so the
        // outcome is reproducible across loads regardless of browse order, matching
        // OpcUaLoadContext.QueueClaim's tie-break.
        var nodeByChildSubject = new Dictionary<RegisteredSubject, NodeId>();

        foreach (var (property, nodeId) in variableNodes)
        {
            var children = property.Children;
            var childSubject = (children.IsEmpty ? null : (IInterceptorSubject?)children[0].Subject)?.TryGetRegisteredSubject();
            if (childSubject is null)
            {
                _logger.LogWarning(
                    "Skipping OPC UA Variable-typed subject reference '{Subject}.{Property}' (NodeId: {NodeId}): no child subject was pre-constructed. The parent type must instantiate this property in its constructor.",
                    property.Subject.GetType().Name, property.Name, nodeId);
                continue;
            }

            if (!nodeByChildSubject.TryGetValue(childSubject, out var existing) || nodeId.CompareTo(existing) < 0)
            {
                nodeByChildSubject[childSubject] = nodeId;
            }
        }

        if (nodeByChildSubject.Count == 0)
        {
            return;
        }

        var nodesToBrowse = new List<(NodeId NodeId, RegisteredSubject ChildSubject, RegisteredSubjectProperty? ValueProperty)>(nodeByChildSubject.Count);
        var nodeIds = new HashSet<NodeId>(nodeByChildSubject.Count);
        foreach (var (childSubject, nodeId) in nodeByChildSubject)
        {
            var valueProperty = childSubject.TryGetValueProperty(_configuration.Mapper, _subject)?.Property;
            if (valueProperty is not null)
            {
                MonitorValueNode(nodeId, valueProperty, context);
            }
            nodesToBrowse.Add((nodeId, childSubject, valueProperty));
            nodeIds.Add(nodeId);
        }

        var browseResults = await context.BrowseAsync(nodeIds).ConfigureAwait(false);

        foreach (var (nodeId, childSubject, valueProperty) in nodesToBrowse)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                continue;
            }

            var childNodes = context.DistinctByResolvedNodeId(rawChildren);

            // Pre-compute name lookup once per childSubject so child-to-property matching is O(C+P), not O(C*P).
            var propertiesByName = new Dictionary<string, RegisteredSubjectProperty>(childSubject.Properties.Length);
            foreach (var childProperty in childSubject.Properties)
            {
                if (childProperty == valueProperty)
                {
                    continue;
                }
                var name = childProperty.ResolvePropertyName(_configuration.Mapper, _subject);
                if (name is null)
                {
                    continue;
                }
                if (!propertiesByName.TryAdd(name, childProperty))
                {
                    _logger.LogWarning(
                        "Variable subject '{Subject}' has multiple properties resolving to the OPC UA browse name '{BrowseName}'. Keeping '{KeptProperty}', dropping '{DroppedProperty}'. Check the Mapper for browse-name collisions.",
                        childSubject.Subject.GetType().Name, name, propertiesByName[name].Name, childProperty.Name);
                }
            }

            foreach (var (childNode, childNodeId) in childNodes)
            {
                if (propertiesByName.TryGetValue(childNode.BrowseName.Name, out var match))
                {
                    MonitorValueNode(childNodeId, match, context);
                }
            }
        }
    }

    private async Task LoadCollectionsAndDictionariesAsync(
        List<(RegisteredSubjectProperty Property, NodeId NodeId)> pendingContainers,
        List<(ReferenceDescription Node, IInterceptorSubject Subject)> children,
        OpcUaLoadContext context)
    {
        if (pendingContainers.Count == 0)
        {
            return;
        }

        var nodeIds = new List<NodeId>(pendingContainers.Count);
        foreach (var (_, nodeId) in pendingContainers)
        {
            nodeIds.Add(nodeId);
        }

        var browseResults = await context.BrowseAsync(nodeIds).ConfigureAwait(false);

        foreach (var (property, nodeId) in pendingContainers)
        {
            if (!browseResults.TryGetValue(nodeId, out var rawChildren))
            {
                // Missing entry = the browse failed with a permanent bad status (transient
                // failures abort the whole load). Keep the property's current items rather
                // than overwriting them with an empty container; the next load retries.
                _logger.LogWarning(
                    "Skipping OPC UA collection or dictionary '{Subject}.{Property}' (NodeId: {NodeId}): browse failed this load; existing items are preserved.",
                    property.Subject.GetType().Name, property.Name, nodeId);
                continue;
            }

            var isDictionary = property.IsSubjectDictionary;
            var containerChildren = await ResolveChildSubjectsAsync(
                property, context.DistinctByResolvedNodeId(rawChildren), isDictionary, context).ConfigureAwait(false);

            object container;
            if (isDictionary)
            {
                var entries = new Dictionary<object, IInterceptorSubject>(containerChildren.Count);
                foreach (var (node, subject) in containerChildren)
                {
                    entries[ExtractDictionaryKey(node.BrowseName.Name)] = subject;
                }
                container = DefaultSubjectFactory.Instance.CreateSubjectDictionary(property.Type, entries);
            }
            else
            {
                container = DefaultSubjectFactory.Instance.CreateSubjectCollection(property.Type, containerChildren.Select(child => child.Subject));
            }

            context.QueueBinding(property, container);
            children.AddRange(containerChildren);
        }
    }

    // Reuses existing children when possible: dictionaries match by browse name,
    // collections match by index position (so server-side reordering between loads
    // rebinds existing subjects to new items).
    private async Task<List<(ReferenceDescription Node, IInterceptorSubject Subject)>> ResolveChildSubjectsAsync(
        RegisteredSubjectProperty property,
        List<(ReferenceDescription Reference, NodeId NodeId)> childNodes,
        bool isDictionary,
        OpcUaLoadContext context)
    {
        var existingChildren = property.Children;
        var existingByKey = new Dictionary<string, IInterceptorSubject?>(isDictionary ? existingChildren.Length : 0);
        if (isDictionary)
        {
            foreach (var existing in existingChildren)
            {
                // Key by the invariant string form of the dictionary key so it matches the bracket
                // key parsed from the browse name (ExtractDictionaryKey), regardless of the key type
                // (e.g. an int key 1 and the browse name "Items[1]" both reduce to "1").
                if (existing.Index is { } index &&
                    Convert.ToString(index, CultureInfo.InvariantCulture) is { } existingKey)
                {
                    existingByKey[existingKey] = existing.Subject;
                }
            }
        }

        // Two children can extract to the same dictionary key (e.g. 'Items[a]' and a
        // bracket-less sibling 'a'). The loser is skipped before its subject is created so it is
        // never staged, claimed, or monitored (it would be committed yet unreachable from the
        // graph); the first reference wins, matching the sibling dedup in
        // ClassifyChildReferencesAsync.
        HashSet<string>? seenKeys = isDictionary ? new(childNodes.Count) : null;
        var children = new List<(ReferenceDescription Node, IInterceptorSubject Subject)>(childNodes.Count);

        for (var i = 0; i < childNodes.Count; i++)
        {
            var (childNode, nodeId) = childNodes[i];

            IInterceptorSubject? childSubject;
            object factoryIndex;

            if (isDictionary)
            {
                var key = ExtractDictionaryKey(childNode.BrowseName.Name);
                if (!seenKeys!.Add(key))
                {
                    _logger.LogWarning(
                        "Skipping OPC UA dictionary child '{BrowseName}' (NodeId: {NodeId}): a sibling already produced the same dictionary key.",
                        childNode.BrowseName.Name, nodeId);
                    continue;
                }

                existingByKey.TryGetValue(key, out childSubject);
                factoryIndex = key;
            }
            else
            {
                childSubject = i < existingChildren.Length ? existingChildren[i].Subject : null;
                factoryIndex = i;
            }

            if (childSubject is null)
            {
                context.SubjectsByNodeId.TryGetValue(nodeId, out childSubject);
            }

            var isFreshlyCreated = childSubject is null;
            childSubject ??= await _configuration.SubjectFactory.CreateCollectionSubjectAsync(
                property, childNode, factoryIndex, context.Session, context.CancellationToken).ConfigureAwait(false);

            if (isFreshlyCreated)
            {
                context.RegisterStagedSubject(childSubject, property.Subject.Context);
            }

            context.SubjectsByNodeId.TryAdd(nodeId, childSubject);
            children.Add((childNode, childSubject));
        }

        return children;
    }

    private static string ExtractDictionaryKey(string browseName)
    {
        return OpcUaBrowseName.TryGetBracketContent(browseName, out var content)
            ? content.ToString()
            : browseName;
    }

}
