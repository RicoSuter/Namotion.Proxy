using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaTypeResolver
{
    // The base classification completes synchronously and yields one of exactly three types, so the
    // tasks are cached rather than allocated once per Object node of every load.
    private static readonly Task<Type> CollectionType = Task.FromResult(typeof(DynamicSubject[]));
    private static readonly Task<Type> DictionaryType = Task.FromResult(typeof(IReadOnlyDictionary<string, DynamicSubject>));
    private static readonly Task<Type> SubjectType = Task.FromResult(typeof(DynamicSubject));

    private readonly ILogger _logger;

    public OpcUaTypeResolver(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the attributes to stamp on a property the loader is adding for a discovered node.
    /// The base returns one <see cref="OpcUaNodeAttribute"/> carrying the node's browse name and
    /// identifier, which is what makes the property addressable by the mapper and what re-matches
    /// it to the same node on the next load. An override that drops it unmaps the property.
    /// </summary>
    public virtual Attribute[] GetAttributesForDynamicProperty(OpcUaDynamicPropertyContext property)
    {
        return CreateNodeAttributes(property.Session, property.Node);
    }

    /// <summary>
    /// Returns the attributes to stamp on an attribute the loader is adding for a discovered node,
    /// with the same contract as <see cref="GetAttributesForDynamicProperty"/>.
    /// </summary>
    public virtual Attribute[] GetAttributesForDynamicAttribute(OpcUaDynamicAttributeContext attribute)
    {
        return CreateNodeAttributes(attribute.Session, attribute.Node);
    }

    private static Attribute[] CreateNodeAttributes(ISession session, ReferenceDescription node)
    {
        var namespaceUri = node.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(node.NodeId.NamespaceIndex);
        return
        [
            new OpcUaNodeAttribute(node.BrowseName.Name, namespaceUri)
            {
                NodeIdentifier = node.NodeId.Identifier.ToString(),
                NodeNamespaceUri = namespaceUri
            }
        ];
    }

    /// <summary>
    /// Classifies an OPC UA Object node as a collection, a dictionary or a single subject reference
    /// from the browse name of its first child, and only when that child is an Object: numeric bracket
    /// content (<c>Items[0]</c>) yields <c>DynamicSubject[]</c>, other non-empty bracket content
    /// (<c>Items[Key]</c>) yields <c>IReadOnlyDictionary&lt;string, DynamicSubject&gt;</c>, and anything
    /// else, including an empty child list, yields <see cref="DynamicSubject"/>.
    /// </summary>
    /// <remarks>
    /// The children are already browsed, so the base implementation completes without any call to the
    /// server. It is asynchronous for overrides that have to read the server to classify a node; every
    /// such read costs one round-trip per Object node, which is what the batched loader otherwise avoids.
    /// </remarks>
    public virtual Task<Type> ResolveObjectNodeTypeAsync(OpcUaObjectNodeContext node, CancellationToken cancellationToken)
    {
        if (node.Children.Count > 0 && node.Children[0].NodeClass == NodeClass.Object)
        {
            var name = node.Children[0].BrowseName?.Name;
            if (name is not null && OpcUaBrowseName.TryGetBracketContent(name, out var content))
            {
                return int.TryParse(content, out _) ? CollectionType : DictionaryType;
            }
        }

        return SubjectType;
    }

    /// <summary>
    /// Infers the CLR type of every Variable node in <paramref name="variables"/> from one batched read
    /// of its DataType and ValueRank attributes. The result is keyed by resolved <see cref="NodeId"/>:
    /// a key is absent when the reference's <see cref="ExpandedNodeId"/> cannot be resolved against the
    /// session's namespace table, and a key with a null value means the type could not be inferred, so
    /// the loader skips the node. A ValueRank of zero or more yields an array of the mapped element type.
    /// </summary>
    /// <remarks>
    /// Override this only to replace the batched read itself, for example when the types come from a
    /// model file and no server read is needed. To decide the type of a single node, override
    /// <see cref="ResolveVariableNodeTypeAsync"/> instead and keep the batching, the positional alignment
    /// of the two attributes per node and the transient status handling.
    /// </remarks>
    /// <exception cref="OpcUaTransientServiceException">A DataType or ValueRank read returned a transient bad status.</exception>
    public virtual async Task<IReadOnlyDictionary<NodeId, Type?>> ResolveVariableNodeTypesAsync(
        ISession session,
        IReadOnlyCollection<ReferenceDescription> variables,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, Type?>(variables.Count);
        if (variables.Count == 0)
        {
            return result;
        }

        var resolvedVariables = new List<(NodeId NodeId, ReferenceDescription Reference)>(variables.Count);
        foreach (var reference in variables)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is not null)
            {
                resolvedVariables.Add((nodeId, reference));
            }
        }

        if (resolvedVariables.Count == 0)
        {
            return result;
        }

        var nodesToRead = new ReadValueIdCollection(resolvedVariables.Count * 2);
        foreach (var (nodeId, _) in resolvedVariables)
        {
            nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.DataType });
            nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.ValueRank });
        }

        // ReadNodesAsync pads short responses and clamps long ones, so
        // `allResults.Count == resolvedVariables.Count * 2` and `allResults[i]` is
        // positionally aligned with `nodesToRead[i]`.
        var allResults = await session.ReadNodesAsync(nodesToRead, TimestampsToReturn.Neither, _logger, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < resolvedVariables.Count; i++)
        {
            var (nodeId, reference) = resolvedVariables[i];
            var dataTypeIndex = i * 2;
            var valueRankIndex = dataTypeIndex + 1;

            // Abort on a transient attribute read: an unresolved type silently drops the
            // property from the model (does not self-heal). Permanent statuses fall through
            // to the graceful skip in ResolveVariableNodeTypeAsync.
            OpcUaStatusCodeClassifier.ThrowIfLoadMustRetry(allResults[dataTypeIndex].StatusCode, "Read", nodeId);
            OpcUaStatusCodeClassifier.ThrowIfLoadMustRetry(allResults[valueRankIndex].StatusCode, "Read", nodeId);

            Type? type = null;
            try
            {
                type = await ResolveVariableNodeTypeAsync(
                        new OpcUaVariableNodeContext(session, reference, nodeId, allResults[dataTypeIndex], allResults[valueRankIndex]),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OpcUaTransientServiceException)
            {
                // An override that reads from the server itself has to be able to abort the load.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to infer CLR type for node {BrowseName}.", reference.BrowseName.Name);
            }

            result[nodeId] = type;
        }

        return result;
    }

    /// <summary>
    /// Infers the CLR type of one Variable node from its already-read DataType and ValueRank attributes.
    /// Returns null when the type cannot be inferred, which makes the loader skip the node. A ValueRank
    /// of zero or more yields an array of the mapped element type.
    /// </summary>
    /// <remarks>
    /// This is the extension point for typing a node by something other than its built-in type, such as
    /// its browse name. Both attributes have already been classified, so a bad status here is permanent
    /// and returning null is the right answer for it. Throwing
    /// <see cref="OpcUaTransientServiceException"/> aborts the load so the source retries it; any other
    /// exception is logged and treated as an uninferable type.
    /// </remarks>
    protected virtual async Task<Type?> ResolveVariableNodeTypeAsync(
        OpcUaVariableNodeContext node,
        CancellationToken cancellationToken)
    {
        if (!StatusCode.IsGood(node.DataType.StatusCode))
        {
            _logger.LogWarning("Failed to read DataType for node {BrowseName} ({StatusCode}).",
                node.Node.BrowseName.Name, node.DataType.StatusCode);
            return null;
        }

        if (node.DataType.Value is not NodeId dataTypeId)
        {
            return null;
        }

        var builtIn = await TypeInfo.GetBuiltInTypeAsync(dataTypeId, node.Session.TypeTree, cancellationToken).ConfigureAwait(false);
        var elementType = TryMapBuiltInType(builtIn);
        if (elementType is null)
        {
            return null;
        }

        var rank = node.ValueRank.Value is int parsedRank ? parsedRank : -1;
        return rank >= 0 ? elementType.MakeArrayType() : elementType;
    }

    /// <summary>
    /// Maps an OPC UA built-in type to the CLR type of the dynamic property. Returns null when there is
    /// no mapping, which includes <see cref="BuiltInType.Variant"/> and <see cref="BuiltInType.Null"/> by
    /// design, so the caller skips the node.
    /// </summary>
    protected virtual Type? TryMapBuiltInType(BuiltInType builtInType) => builtInType switch
    {
        BuiltInType.Boolean => typeof(bool),
        BuiltInType.SByte => typeof(sbyte),
        BuiltInType.Byte => typeof(byte),
        BuiltInType.Int16 => typeof(short),
        BuiltInType.UInt16 => typeof(ushort),
        BuiltInType.Int32 => typeof(int),
        BuiltInType.UInt32 => typeof(uint),
        BuiltInType.Int64 => typeof(long),
        BuiltInType.UInt64 => typeof(ulong),
        BuiltInType.Float => typeof(float),
        BuiltInType.Double => typeof(double),
        BuiltInType.String => typeof(string),
        BuiltInType.DateTime => typeof(DateTime),
        BuiltInType.Guid => typeof(Guid),
        BuiltInType.ByteString => typeof(byte[]),
        BuiltInType.XmlElement => typeof(string),
        BuiltInType.NodeId => typeof(NodeId),
        BuiltInType.ExpandedNodeId => typeof(ExpandedNodeId),
        BuiltInType.StatusCode => typeof(StatusCode),
        BuiltInType.QualifiedName => typeof(QualifiedName),
        BuiltInType.LocalizedText => typeof(LocalizedText),
        BuiltInType.DiagnosticInfo => typeof(DiagnosticInfo),
        BuiltInType.ExtensionObject => typeof(ExtensionObject),
        BuiltInType.DataValue => typeof(DataValue),
        BuiltInType.Enumeration => typeof(int),
        BuiltInType.Number => typeof(double),
        BuiltInType.Integer => typeof(long),
        BuiltInType.UInteger => typeof(ulong),
        BuiltInType.Variant => null,
        BuiltInType.Null => null,
        _ => null
    };
}
