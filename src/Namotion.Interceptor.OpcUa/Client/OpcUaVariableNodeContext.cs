using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// One Variable node to infer a CLR type for, with the attributes the batched read already returned for it.
/// </summary>
/// <param name="Session">The session the node was read from. Its type tree resolves a custom DataType to a built-in one.</param>
/// <param name="Node">The browse reference of the node.</param>
/// <param name="NodeId">The node's id, resolved against the session's namespace table.</param>
/// <param name="DataType">The DataType attribute. Its status is either good or permanently bad, because a transient status aborts the load before the type is inferred.</param>
/// <param name="ValueRank">The ValueRank attribute. Zero or more means the node is an array.</param>
public readonly record struct OpcUaVariableNodeContext(
    ISession Session,
    ReferenceDescription Node,
    NodeId NodeId,
    DataValue DataType,
    DataValue ValueRank);
