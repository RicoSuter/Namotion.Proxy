using Namotion.Interceptor.Connectors.Diagnostics;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base interface for components that connect subjects to external systems.
/// Implemented by sources (inbound sync) and by servers (outbound exposure).
/// </summary>
public interface ISubjectConnector
{
    /// <summary>
    /// Gets the root subject being connected to an external system.
    /// </summary>
    IInterceptorSubject RootSubject { get; }

    /// <summary>
    /// Gets what this connector reports about the transport it drives.
    /// </summary>
    /// <remarks>
    /// Whether the model can be trusted is a separate question, answered by
    /// <see cref="ISubjectSource.State"/>.
    /// </remarks>
    ConnectorDiagnostics Diagnostics { get; }
}
