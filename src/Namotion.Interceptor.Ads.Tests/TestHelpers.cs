using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using TwinCAT.Ads;

namespace Namotion.Interceptor.Ads.Tests;

/// <summary>
/// Shared factory methods for common test setup.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a context with property tracking and registry (no lifecycle).
    /// </summary>
    public static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    /// <summary>
    /// Creates a context with property tracking, registry, and lifecycle.
    /// </summary>
    public static IInterceptorSubjectContext CreateContextWithLifecycle()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();
    }

    /// <summary>
    /// Creates a default ADS client configuration for unit tests.
    /// </summary>
    public static AdsClientConfiguration CreateConfiguration(char pathSeparator = '.')
    {
        return new AdsClientConfiguration
        {
            // System-router mode (no Host): the unit tests exercise behavior with an unreachable target,
            // without starting the embedded router (which would bind the host's AMS TCP port).
            AmsNetId = AmsNetId.Parse("127.0.0.1.1.1"),
            AmsPort = 851,
            Mapper = AdsCompositeMapper.CreateDefault(
                AdsConstants.DefaultConnectorName,
                new AttributeBasedPathProvider(AdsConstants.DefaultConnectorName, pathSeparator))
        };
    }

    /// <summary>
    /// Creates an ADS subject loader for unit tests.
    /// </summary>
    public static AdsSubjectLoader CreateLoader()
    {
        return new AdsSubjectLoader(AdsCompositeMapper.CreateDefault());
    }

    /// <summary>
    /// Resolves a registered property by name from a subject (the subject must be attached to a registry context).
    /// </summary>
    public static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string propertyName)
    {
        var registered = subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("Subject is not registered.");
        return registered.Properties.Single(property => property.Name == propertyName);
    }
}
