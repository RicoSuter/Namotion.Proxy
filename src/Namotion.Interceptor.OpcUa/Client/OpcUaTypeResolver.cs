using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

public class OpcUaTypeResolver
{
    private readonly ILogger _logger;

    public OpcUaTypeResolver(ILogger logger)
    {
        _logger = logger;
    }

    public virtual Attribute[] GetDynamicPropertyAttributes(ReferenceDescription reference, ISession session)
    {
        var namespaceUri = reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex);
        return
        [
            new OpcUaNodeAttribute(reference.BrowseName.Name, namespaceUri)
            {
                NodeIdentifier = reference.NodeId.Identifier.ToString(),
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
    /// <param name="node">The Object node being classified. Not inspected here; available to overrides.</param>
    /// <param name="children">The node's browsed children in browse order.</param>
    public virtual Type ResolveObjectNodeType(ReferenceDescription node, IReadOnlyList<ReferenceDescription> children)
    {
        if (children.Count > 0 && children[0].NodeClass == NodeClass.Object)
        {
            var name = children[0].BrowseName?.Name;
            if (name is not null && OpcUaBrowseName.TryGetBracketContent(name, out var content))
            {
                return int.TryParse(content, out _)
                    ? typeof(DynamicSubject[])
                    : typeof(IReadOnlyDictionary<string, DynamicSubject>);
            }
        }

        return typeof(DynamicSubject);
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
                node.Reference.BrowseName.Name, node.DataType.StatusCode);
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
