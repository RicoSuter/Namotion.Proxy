using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// A node the loader found no attribute for, about to be added to <paramref name="Property"/> as a dynamic one.
/// </summary>
/// <param name="Session">The session the node was discovered on.</param>
/// <param name="Node">The browse reference of the node.</param>
/// <param name="NodeId">The node's id, resolved against the session's namespace table.</param>
/// <param name="AttributeType">The CLR type the attribute is being created with.</param>
/// <param name="Property">The property gaining the attribute.</param>
/// <param name="AttributeName">The name the attribute is being registered under.</param>
public readonly record struct OpcUaDynamicAttributeContext(
    ISession Session,
    ReferenceDescription Node,
    NodeId NodeId,
    Type AttributeType,
    RegisteredSubjectProperty Property,
    string AttributeName);
