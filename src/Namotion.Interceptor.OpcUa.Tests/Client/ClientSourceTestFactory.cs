using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Builds an OPC UA client source that never contacts a server, for tests that only exercise what
/// the source reports about itself.
/// </summary>
internal static class ClientSourceTestFactory
{
    /// <param name="withPropertyTracking">
    /// <c>false</c> leaves the context without a <c>PropertyChangeInterceptor</c>, which makes the
    /// pump fail its configuration guard on the first attempt, the only way to reach
    /// <c>ConnectorMetrics.MarkStarted</c> without a server.
    /// </param>
    /// <param name="configuration">The client configuration, or <c>null</c> for the default one.</param>
    internal static OpcUaSubjectClientSource CreateClientSource(
        bool withPropertyTracking = true,
        OpcUaClientConfiguration? configuration = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        if (withPropertyTracking)
        {
            context.WithFullPropertyTracking();
        }

        var root = new TestRoot(context);
        return new OpcUaSubjectClientSource(root, configuration ?? CreateConfiguration(), NullLogger.Instance);
    }

    internal static OpcUaClientConfiguration CreateConfiguration(
        string serverUrl = "opc.tcp://localhost:4840",
        string certificateStoreBasePath = "pki") => new()
    {
        // Not dialled by default: the tests that reach the wire pass a port nothing is listening on.
        ServerUrl = serverUrl,
        CertificateStoreBasePath = certificateStoreBasePath,
        TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
        ValueConverter = new OpcUaValueConverter(),
        SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
    };
}
