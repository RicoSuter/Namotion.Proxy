using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// One Object node to infer a CLR type for, with the children the batched browse already returned for it.
/// </summary>
/// <param name="Session">The session the node was browsed from.</param>
/// <param name="Node">The browse reference of the node.</param>
/// <param name="NodeId">The node's id, resolved against the session's namespace table.</param>
/// <param name="Children">The node's children in browse order. Empty when the node has none.</param>
public readonly record struct OpcUaObjectNodeContext(
    ISession Session,
    ReferenceDescription Node,
    NodeId NodeId,
    IReadOnlyList<ReferenceDescription> Children);
