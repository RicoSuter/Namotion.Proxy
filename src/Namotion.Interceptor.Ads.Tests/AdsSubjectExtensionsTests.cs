using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Ads;
using Namotion.Interceptor.Ads.Client;
using Namotion.Interceptor.Ads.Mapping;
using Namotion.Interceptor.Ads.Tests.Models;
using TwinCAT.Ads;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests;

public class AdsSubjectExtensionsTests
{

    [Fact]
    public void AddAdsSubjectClientSource_GenericOverload_ShouldRegisterBothHostedServices()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddAdsSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            amsPort: 851);

        // Assert
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Single(hostedServices);
        Assert.Contains(hostedServices, service => service is AdsSubjectClientSource);
    }

    [Fact]
    public void AddAdsSubjectClientSource_WithLocalAmsNetId_ShouldRegisterInSystemRouterMode()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act - the loopback overload: no host, so no embedded router is started
        services.AddAdsSubjectClientSource<TestPlcModel>(AmsNetId.Local);

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<AdsSubjectClientSource>()
            .Single();

        Assert.Equal(AmsNetId.Local, source.Configuration.AmsNetId);
        Assert.Null(source.Configuration.Host);
        Assert.Equal(851, source.Configuration.AmsPort);

        // Validate() rejects a configuration with neither Host nor AmsNetId, so this also proves
        // the overload produces a usable one.
        source.Configuration.Validate();
    }

    [Fact]
    public void AddAdsSubjectClientSource_WithCustomConfiguration_ShouldUseProvidedConfiguration()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddAdsSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "10.0.0.1",
                AmsNetId = AmsNetId.Parse("10.0.0.1.1.1"),
                AmsPort = 852,
                DefaultReadMode = AdsReadMode.Polled,
                Mapper = AdsCompositeMapper.CreateDefault("custom")
            });

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<AdsSubjectClientSource>()
            .Single();

        Assert.Equal("10.0.0.1", source.Configuration.Host);
        Assert.Equal("10.0.0.1.1.1", source.Configuration.GetTargetAmsNetId().ToString());
        Assert.Equal(852, source.Configuration.AmsPort);
        Assert.Equal(AdsReadMode.Polled, source.Configuration.DefaultReadMode);
    }

    [Fact]
    public void AddAdsSubjectClientSource_DefaultAmsNetId_ShouldAppendOneOne()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddAdsSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<AdsSubjectClientSource>()
            .Single();

        Assert.Equal("192.168.1.100.1.1", source.Configuration.GetTargetAmsNetId().ToString());
    }

    [Fact]
    public void AddAdsSubjectClientSource_ExplicitAmsNetId_ShouldUseProvidedValue()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddAdsSubjectClientSource<TestPlcModel>(
            host: "192.168.1.100",
            amsNetId: AmsNetId.Parse("5.23.100.200.1.1"));

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<AdsSubjectClientSource>()
            .Single();

        Assert.Equal("5.23.100.200.1.1", source.Configuration.GetTargetAmsNetId().ToString());
    }

    [Fact]
    public void AddAdsSubjectClientSource_SystemRouterOverload_UsesAmsNetIdWithoutEmbedding()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act
        services.AddAdsSubjectClientSource<TestPlcModel>(
            amsNetId: AmsNetId.Parse("5.23.45.67.1.1"));

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<AdsSubjectClientSource>()
            .Single();

        Assert.Equal("5.23.45.67.1.1", source.Configuration.GetTargetAmsNetId().ToString());
        Assert.Null(source.Configuration.Host);
        Assert.False(source.Configuration.UseEmbeddedRouter);
    }

    [Fact]
    public void AddAdsSubjectClientSource_MultipleRegistrations_ShouldRegisterAllServices()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();

        // Act - register two independent sources
        services.AddAdsSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "192.168.1.100",
                AmsNetId = AmsNetId.Parse("192.168.1.100.1.1"),
                AmsPort = 851,
                Mapper = AdsCompositeMapper.CreateDefault("ads")
            });

        services.AddAdsSubjectClientSource(
            subjectSelector: _ => new TestPlcModel(context),
            configurationProvider: _ => new AdsClientConfiguration
            {
                Host = "192.168.1.200",
                AmsNetId = AmsNetId.Parse("192.168.1.200.1.1"),
                AmsPort = 852,
                Mapper = AdsCompositeMapper.CreateDefault("ads")
            });

        // Assert - should have 2 hosted services (one per registration)
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hostedServices.Count);
        Assert.Equal(2, hostedServices.OfType<AdsSubjectClientSource>().Count());
    }

    [Fact]
    public void AddAdsSubjectClientSource_DefaultAmsPort_ShouldBe851()
    {
        // Arrange
        var context = TestHelpers.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestPlcModel>(_ => new TestPlcModel(context));

        // Act - do not pass amsPort, should default to 851
        services.AddAdsSubjectClientSource<TestPlcModel>(
            host: "10.0.0.1");

        // Assert
        var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IHostedService>()
            .OfType<AdsSubjectClientSource>()
            .Single();

        Assert.Equal(851, source.Configuration.AmsPort);
    }
}
