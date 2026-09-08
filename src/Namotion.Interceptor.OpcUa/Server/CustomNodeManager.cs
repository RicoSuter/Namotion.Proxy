using System.Reflection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Server;

namespace Namotion.Interceptor.OpcUa.Server;

internal class CustomNodeManager : CustomNodeManager2
{
    private const string PathDelimiter = ".";

    private readonly IInterceptorSubject _subject;
    private readonly OpcUaSubjectServer _serverService;
    private readonly OpcUaServerConfiguration _configuration;
    private readonly IPropertyMapper<OpcUaPropertyMapping> _mapper;
    private readonly ILogger _logger;
    private readonly OpcUaNodeFactory _nodeFactory;

    private readonly SemaphoreSlim _structureLock = new(1, 1);
    private readonly Dictionary<RegisteredSubject, NodeState> _subjects = new();

    public CustomNodeManager(
        IInterceptorSubject subject,
        OpcUaSubjectServer serverService,
        IServerInternal server,
        ApplicationConfiguration applicationConfiguration,
        OpcUaServerConfiguration configuration,
        ILogger logger) :
        base(server, applicationConfiguration, configuration.GetNamespaceUris())
    {
        _subject = subject;
        _serverService = serverService;
        _configuration = configuration;
        _mapper = configuration.Mapper;
        _logger = logger;
        _nodeFactory = new OpcUaNodeFactory(logger);
    }

    // Expose protected members for OpcUaNodeFactory
    internal ISystemContext GetSystemContext() => SystemContext;
    internal NodeIdDictionary<NodeState> GetPredefinedNodes() => PredefinedNodes;
    internal NodeState FindNode(NodeId nodeId) => FindNodeInAddressSpace(nodeId);
    internal void AddNode(NodeState node) => AddPredefinedNode(SystemContext, node);

    protected override NodeStateCollection LoadPredefinedNodes(ISystemContext context)
    {
        var collection = base.LoadPredefinedNodes(context);
        _configuration.LoadPredefinedNodes(collection, context);
        return collection;
    }

    public void ClearPropertyData()
    {
        var rootSubject = _subject.TryGetRegisteredSubject();
        if (rootSubject != null)
        {
            foreach (var property in rootSubject.Properties)
            {
                property.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
                ClearAttributePropertyData(property);
            }
        }

        // Snapshot under the structure lock: _subjects is a plain dictionary mutated by
        // RemoveSubjectNodes (on subject-detach threads), so enumerating it directly races
        // with a concurrent detach (throws "collection modified during enumeration").
        RegisteredSubject[] subjects;
        _structureLock.Wait();
        try
        {
            subjects = _subjects.Keys.ToArray();
        }
        finally
        {
            _structureLock.Release();
        }

        foreach (var subject in subjects)
        {
            foreach (var property in subject.Properties)
            {
                property.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
                ClearAttributePropertyData(property);
            }
        }
    }

    private void ClearAttributePropertyData(RegisteredSubjectProperty property)
    {
        foreach (var attribute in property.Attributes)
        {
            attribute.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
            ClearAttributePropertyData(attribute);
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        base.CreateAddressSpace(externalReferences);

        _structureLock.Wait();
        try
        {
            var registeredSubject = _subject.TryGetRegisteredSubject();
            if (registeredSubject is not null)
            {
                if (_configuration.RootName is not null)
                {
                    var node = _nodeFactory.CreateFolderNode(this, ObjectIds.ObjectsFolder,
                        new NodeId(_configuration.RootName, NamespaceIndex), new QualifiedName(_configuration.RootName, NamespaceIndex), null, null, null);

                    CreateSubjectNodes(node.NodeId, registeredSubject, _configuration.RootName + PathDelimiter);
                }
                else
                {
                    CreateSubjectNodes(ObjectIds.ObjectsFolder, registeredSubject, string.Empty);
                }
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    /// <summary>
    /// Removes nodes for a detached subject. Idempotent - safe to call multiple times.
    /// Uses DeleteNode to properly cleanup nodes and event handlers, preventing memory leaks.
    /// </summary>
    public void RemoveSubjectNodes(IInterceptorSubject subject)
    {
        _structureLock.Wait();
        try
        {
            // DeleteNode flushes whatever change masks are pending and takes no lock of its own, so
            // without this it can flush a value the write loop has set but not yet flushed itself,
            // from a thread the write loop's guard does not cover. DeleteNode already takes this lock
            // internally, so the order is unchanged.
            lock (Lock)
            {
                var registeredSubject = subject.TryGetRegisteredSubject();

                // Remove variable nodes for this subject's properties
                if (registeredSubject != null)
                {
                    foreach (var property in registeredSubject.Properties)
                    {
                        if (property.Reference.TryGetPropertyData(_serverService.OpcUaVariableKey, out var node)
                            && node is BaseDataVariableState variableNode)
                        {
                            DeleteNode(SystemContext, variableNode.NodeId);
                            property.Reference.RemovePropertyData(_serverService.OpcUaVariableKey);
                        }
                    }
                }

                // Remove object nodes
                var keysToRemove = _subjects.Where(kvp => kvp.Key.Subject == subject).Select(kvp => kvp.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    if (_subjects.TryGetValue(key, out var nodeState))
                    {
                        DeleteNode(SystemContext, nodeState.NodeId);
                        _subjects.Remove(key);
                    }
                }
            }
        }
        finally
        {
            _structureLock.Release();
        }
    }

    private void CreateSubjectNodes(NodeId parentNodeId, RegisteredSubject subject, string prefix)
    {
        foreach (var property in subject.Properties)
        {
            if (property.IsAttribute)
                continue;

            // Resolve each property's mapping once; the branch helpers reuse it.
            if (!_mapper.TryGetMapping(property, _subject, out var mapping))
                continue;

            var propertyName = mapping.BrowseName ?? property.BrowseName;
            if (property.IsSubjectCollection)
            {
                CreateArrayObjectNode(propertyName, property, mapping, property.Children, parentNodeId, prefix);
            }
            else if (property.IsSubjectDictionary)
            {
                CreateDictionaryObjectNode(propertyName, property, mapping, property.Children, parentNodeId, prefix);
            }
            else if (property.IsSubjectReference)
            {
                var referencedSubject = property.Children.SingleOrDefault();
                if (referencedSubject.Subject is not null)
                {
                    // Check if this should be a VariableNode instead of ObjectNode
                    if (mapping.NodeClass == OpcUaNodeClass.Variable)
                    {
                        CreateVariableNodeForSubject(propertyName, property, mapping, parentNodeId, prefix);
                    }
                    else
                    {
                        CreateReferenceObjectNode(propertyName, property, mapping, referencedSubject, parentNodeId, prefix);
                    }
                }
            }
            else
            {
                CreateVariableNode(propertyName, property, mapping, parentNodeId, prefix);
            }
        }
    }

    private void CreateReferenceObjectNode(string propertyName, RegisteredSubjectProperty property, OpcUaPropertyMapping? mapping, SubjectPropertyChild child, NodeId parentNodeId, string parentPath)
    {
        var path = parentPath + propertyName;
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, mapping, child.Index);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, mapping);

        CreateChildObject(property, mapping, browseName, child.Subject, path, parentNodeId, referenceTypeId);
    }

    private void CreateArrayObjectNode(string propertyName, RegisteredSubjectProperty property, OpcUaPropertyMapping? mapping, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = _nodeFactory.GetNodeId(this, mapping, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, mapping, null);

        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, mapping);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, mapping);

        var propertyNode = _nodeFactory.CreateFolderNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, mapping);

        // Child objects below the array folder use path: parentPath + propertyName + "[index]"
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, mapping);
        foreach (var child in children)
        {
            var childBrowseName = new QualifiedName($"{propertyName}[{child.Index}]", NamespaceIndex);
            var childPath = $"{parentPath}{propertyName}[{child.Index}]";

            CreateChildObject(property, mapping, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private void CreateDictionaryObjectNode(string propertyName, RegisteredSubjectProperty property, OpcUaPropertyMapping? mapping, ICollection<SubjectPropertyChild> children, NodeId parentNodeId, string parentPath)
    {
        var nodeId = _nodeFactory.GetNodeId(this, mapping, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, mapping, null);

        var typeDefinitionId = _nodeFactory.GetTypeDefinitionId(this, mapping);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, mapping);

        var propertyNode = _nodeFactory.CreateFolderNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, mapping);
        var childReferenceTypeId = _nodeFactory.GetChildReferenceTypeId(this, mapping);
        foreach (var child in children)
        {
            var indexString = child.Index?.ToString();
            if (string.IsNullOrEmpty(indexString))
            {
                _logger.LogWarning(
                    "Dictionary property '{PropertyName}' contains a child with null or empty key. Skipping OPC UA node creation.",
                    propertyName);

                continue;
            }

            var childBrowseName = new QualifiedName(indexString, NamespaceIndex);
            var childPath = parentPath + propertyName + PathDelimiter + child.Index;

            CreateChildObject(property, mapping, childBrowseName, child.Subject, childPath, propertyNode.NodeId, childReferenceTypeId);
        }
    }

    private BaseDataVariableState CreateVariableNode(
        string propertyName,
        RegisteredSubjectProperty property,
        OpcUaPropertyMapping? mapping,
        NodeId parentNodeId,
        string parentPath,
        RegisteredSubjectProperty? configurationProperty = null,
        OpcUaPropertyMapping? valuePropertyMapping = null)
    {
        // `mapping` is the mapping for the configuration property: the containing property when a
        // separate configurationProperty is supplied, otherwise `property` itself. When a separate
        // configurationProperty is supplied, the caller passes the already-resolved value-property
        // mapping via valuePropertyMapping so it is not resolved a second time; the two parameters
        // must be paired (both null or both non-null).
        var nodeId = _nodeFactory.GetNodeId(this, mapping, parentPath + propertyName);
        var browseName = _nodeFactory.GetBrowseName(this, propertyName, mapping, null);
        var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, mapping);

        var dataTypeMapping = configurationProperty is null ? mapping : valuePropertyMapping;
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(this, dataTypeMapping);

        var variableNode = ConfigureVariableNode(property, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, mapping);

        property.Reference.SetPropertyData(_serverService.OpcUaVariableKey, variableNode);

        CreateAttributeNodes(variableNode, property, parentPath + propertyName);
        return variableNode;
    }

    private void CreateAttributeNodes(NodeState parentNode, RegisteredSubjectProperty property, string parentPath)
    {
        foreach (var attribute in property.Attributes)
        {
            if (!_mapper.TryGetMapping(attribute, _subject, out var mapping))
                continue;

            var attributeName = mapping.BrowseName ?? attribute.BrowseName;
            var attributePath = parentPath + PathDelimiter + attributeName;
            var referenceTypeId = _nodeFactory.GetReferenceTypeId(this, mapping) ?? ReferenceTypeIds.HasProperty;

            // Reuse the mapping resolved above instead of resolving it again in the helper.
            var attributeNode = CreateVariableNodeForAttribute(
                attributeName,
                attribute,
                mapping,
                parentNode.NodeId,
                attributePath,
                referenceTypeId);

            // Recursive: attributes can have attributes
            CreateAttributeNodes(attributeNode, attribute, attributePath);
        }
    }

    private BaseDataVariableState CreateVariableNodeForAttribute(
        string attributeName,
        RegisteredSubjectProperty attribute,
        OpcUaPropertyMapping mapping,
        NodeId parentNodeId,
        string path,
        NodeId referenceTypeId)
    {
        var nodeId = _nodeFactory.GetNodeId(this, mapping, path);
        var browseName = _nodeFactory.GetBrowseName(this, attributeName, mapping, null);
        var dataTypeOverride = _nodeFactory.GetDataTypeOverride(this, mapping);

        var variableNode = ConfigureVariableNode(attribute, parentNodeId, nodeId, browseName, referenceTypeId, dataTypeOverride, mapping);

        attribute.Reference.SetPropertyData(_serverService.OpcUaVariableKey, variableNode);

        return variableNode;
    }

    /// <summary>
    /// Shared helper that configures a variable node with value, access levels, array dimensions, and state change handler.
    /// </summary>
    private BaseDataVariableState ConfigureVariableNode(
        RegisteredSubjectProperty property,
        NodeId parentNodeId,
        NodeId nodeId,
        QualifiedName browseName,
        NodeId? referenceTypeId,
        NodeId? dataTypeOverride,
        OpcUaPropertyMapping? mapping)
    {
        var value = _configuration.ValueConverter.ConvertToNodeValue(property.GetValue(), property);
        var typeInfo = _configuration.ValueConverter.GetNodeTypeInfo(property.Type);

        var variableNode = _nodeFactory.CreateVariableNode(this, parentNodeId, nodeId, browseName, typeInfo, referenceTypeId, dataTypeOverride, mapping);
        _nodeFactory.AddAdditionalReferences(this, variableNode, mapping);
        variableNode.Handle = property.Reference;

        // Adjust access according to property setter
        if (!property.HasSetter)
        {
            variableNode.AccessLevel = AccessLevels.CurrentRead;
            variableNode.UserAccessLevel = AccessLevels.CurrentRead;
        }
        else
        {
            variableNode.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variableNode.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
        }

        // Set array dimensions (works for 1D and multi-D)
        if (value is Array arrayValue)
        {
            variableNode.ArrayDimensions = new ReadOnlyList<uint>(
                Enumerable.Range(0, arrayValue.Rank)
                    .Select(i => (uint)arrayValue.GetLength(i))
                    .ToArray());
        }

        variableNode.Value = value;

        var writeTimestamp = property.Reference.TryGetWriteTimestamp();
        if (writeTimestamp.HasValue)
        {
            variableNode.Timestamp = writeTimestamp.Value.UtcDateTime;
        }

        // Assigning the value above leaves a pending change mask that nothing else clears, so the next
        // flush of this node reports the creation value as if a client had written it. Cleared here,
        // during CreateAddressSpace and before the handler is attached, so nothing observes it.
        variableNode.ClearChangeMasks(SystemContext, false);

        variableNode.StateChanged += (_, _, changes) =>
        {
            if (changes.HasFlag(NodeStateChangeMasks.Value))
            {
                // Callers that reach here hold NodeManager.Lock; every value set must be flushed by
                // the same actor under that same hold, or a later flush misattributes it as a client write.
                _serverService.UpdateProperty(property.Reference, variableNode.Timestamp, variableNode.Value);
            }
        };

        return variableNode;
    }

    private void CreateVariableNodeForSubject(string propertyName, RegisteredSubjectProperty property, OpcUaPropertyMapping? mapping, NodeId parentNodeId, string parentPath)
    {
        // Get the child subject - skip if null (structural sync will handle later)
        var childSubject = property.Children.SingleOrDefault().Subject?.TryGetRegisteredSubject();
        if (childSubject is null)
        {
            return;
        }

        // Find the [OpcUaValue] property
        var valuePropertyResult = childSubject.TryGetValueProperty(_mapper, _subject);
        if (valuePropertyResult is null)
        {
            return;
        }

        var (valueProperty, valuePropertyMapping) = valuePropertyResult.Value;

        // Create the variable node: value from valueProperty, config (and its mapping) from the containing property
        var variableNode = CreateVariableNode(
            propertyName, valueProperty, mapping, parentNodeId, parentPath,
            configurationProperty: property, valuePropertyMapping: valuePropertyMapping);

        // Create child properties of the VariableNode (excluding the value property)
        var path = parentPath + propertyName;
        foreach (var childProperty in childSubject.Properties)
        {
            if (!_mapper.TryGetMapping(childProperty, _subject, out var childConfig))
                continue;

            if (childConfig.IsValue == true)
                continue;

            var childName = childConfig.BrowseName ?? childProperty.BrowseName;
            CreateVariableNode(childName, childProperty, childConfig, variableNode.NodeId, path + PathDelimiter);
        }
    }

    private void CreateChildObject(RegisteredSubjectProperty property, OpcUaPropertyMapping? mapping, QualifiedName browseName,
        IInterceptorSubject subject,
        string path,
        NodeId parentNodeId,
        NodeId? referenceTypeId)
    {
        var registeredSubject = subject.TryGetRegisteredSubject() ?? throw new InvalidOperationException("Registered subject not found.");

        if (_subjects.TryGetValue(registeredSubject, out var existingNode))
        {
            // Subject already created, add reference to existing node.
            // In OPC UA the BrowseName lives on the target node, not the reference, so
            // when the reusing property's intended browse name differs from the one the
            // node was first published with, the second name is lost on the wire.
            if (!existingNode.BrowseName.Equals(browseName))
            {
                _logger.LogWarning(
                    "Subject '{SubjectType}' reused at '{ParentType}.{PropertyName}' under browse name '{NewBrowseName}', but the node was first published as '{ExistingBrowseName}'. The second name is not preserved on the wire; clients cannot bind that property on round-trip. Use a distinct subject instance per property if both names must round-trip.",
                    registeredSubject.Subject.GetType().Name,
                    property.Subject.GetType().Name,
                    property.Name,
                    browseName,
                    existingNode.BrowseName);
            }

            var parentNode = FindNodeInAddressSpace(parentNodeId);
            parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, existingNode.NodeId);
        }
        else
        {
            // Create new node and add to dictionary (protected by _structureLock). `mapping` is the
            // containing property's mapping, threaded in to avoid re-resolving it per collection child.
            var nodeId = _nodeFactory.GetNodeId(this, mapping, path);
            var typeDefinitionId = GetTypeDefinitionIdForSubject(subject);

            var node = _nodeFactory.CreateObjectNode(this, parentNodeId, nodeId, browseName, typeDefinitionId, referenceTypeId, mapping);
            _nodeFactory.AddAdditionalReferences(this, node, mapping);
            _subjects[registeredSubject] = node;
            CreateSubjectNodes(node.NodeId, registeredSubject, path + PathDelimiter);
        }
    }

    private NodeId? GetTypeDefinitionIdForSubject(IInterceptorSubject subject)
    {
        // For subjects, check if type has OpcUaNode attribute at class level
        var typeAttribute = subject.GetType().GetCustomAttribute<OpcUaNodeAttribute>();
        return _nodeFactory.GetTypeDefinitionId(this, typeAttribute);
    }
}
