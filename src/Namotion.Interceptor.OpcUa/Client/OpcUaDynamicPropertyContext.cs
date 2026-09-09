using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// A node the loader found no property for, about to be added to <paramref name="Subject"/> as a dynamic one.
/// </summary>
/// <param name="Session">The session the node was discovered on.</param>
/// <param name="Node">The browse reference of the node.</param>
/// <param name="NodeId">The node's id, resolved against the session's namespace table.</param>
/// <param name="PropertyType">The CLR type the property is being created with.</param>
/// <param name="Subject">The subject gaining the property.</param>
public readonly record struct OpcUaDynamicPropertyContext(
    ISession Session,
    ReferenceDescription Node,
    NodeId NodeId,
    Type PropertyType,
    RegisteredSubject Subject);
