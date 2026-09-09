using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectStartupTests
{
    [Fact]
    public async Task WhenRootIsLoaded_ThenHostedStartupSeesPublicationAndServiceRegistration()
    {
        // Arrange
        using var probe = new ConfigurableStartupProbe();
        probe.Release.Set();
        var services = new ServiceCollection().AddSingleton(probe)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        var context = InterceptorSubjectContext.Create().WithHostedServices(services);
        services.AddSingleton(context);
        await using var provider = services.BuildServiceProvider();
        var handler = Assert.Single(provider.GetServices<IHostedService>());
        var types = new TypeProvider();
        types.AddTypes([typeof(ConfigurableStartupSubject)]);
        var path = Path.Combine(Path.GetTempPath(), $"root-startup-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {"$type":"HomeBlaze.Services.Tests.Serialization.ConfigurableStartupSubject","configuration":"configured"}
            """);
        var configuration = new Mock<IConfiguration>();
        configuration.Setup(value => value["HomeBlaze:RootConfigFile"]).Returns(path);
        RootManager? manager = null;
        using var rootManager = manager = new RootManager(new SubjectTypeRegistry(types),
            new ConfigurableSubjectSerializer(types, provider), context,
            new SubjectPathResolver(() => manager?.Root), configuration.Object);
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Starting = subject => published.TrySetResult(ReferenceEquals(rootManager.Root, subject) &&
            ReferenceEquals(context.TryGetService<ConfigurableStartupSubject>(), subject) &&
            rootManager.RootLoaded.IsCompletedSuccessfully);
        await handler.StartAsync(CancellationToken.None);

        try
        {
            // Act
            await rootManager.StartAsync(CancellationToken.None);
            await rootManager.RootLoaded.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.True(await published.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("configured", await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WhenConfigurationIsPopulated_ThenHostedStartupReadsTheConfiguredValue()
    {
        // Arrange
        using var probe = new ConfigurableStartupProbe();
        var services = new ServiceCollection().AddSingleton(probe)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        var context = InterceptorSubjectContext.Create().WithHostedServices(services);
        services.AddSingleton(context);
        using var provider = services.BuildServiceProvider();
        var handler = Assert.Single(provider.GetServices<IHostedService>());
        probe.BeforeAssignment = () => handler.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var types = new TypeProvider();
        var type = typeof(ConfigurableStartupSubject);
        types.AddTypes([type]);
        var serializer = new ConfigurableSubjectSerializer(types, provider);
        var deserialize = Task.Run(() => serializer.Deserialize($$"""
            {"$type":"{{type.FullName}}","configuration":"configured"}
            """));
        try
        {
            // Act
            await probe.Populating.Task.WaitAsync(TimeSpan.FromSeconds(10));
            probe.Release.Set();
            var subject = (IInterceptorSubject)(await deserialize.WaitAsync(TimeSpan.FromSeconds(10)))!;
            var observed = await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("configured", observed);
            Assert.Same(handler, subject.Context.GetService<IHostedService>());
        }
        finally
        {
            probe.Release.Set();
            await deserialize.WaitAsync(TimeSpan.FromSeconds(10));
            await handler.StopAsync(CancellationToken.None);
        }
    }
    [Fact]
    public async Task WhenConfigurationPopulationThrows_ThenTheCapturedStartIsStillReleased()
    {
        // Arrange
        using var probe = new ConfigurableStartupProbe { FailPopulation = true };
        probe.Release.Set();
        var services = new ServiceCollection().AddSingleton(probe)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        var context = InterceptorSubjectContext.Create().WithHostedServices(services);
        services.AddSingleton(context);
        using var provider = services.BuildServiceProvider();
        var handler = Assert.Single(provider.GetServices<IHostedService>());
        await handler.StartAsync(CancellationToken.None);
        var types = new TypeProvider();
        types.AddTypes([typeof(ConfigurableStartupSubject)]);
        var serializer = new ConfigurableSubjectSerializer(types, provider);

        try
        {
            // Act
            Assert.Throws<System.Reflection.TargetInvocationException>(() => serializer.Deserialize("""
                {"$type":"HomeBlaze.Services.Tests.Serialization.ConfigurableStartupSubject","configuration":"configured"}
                """));

            // Assert - the scope defers the start, it does not decide whether the subject is fit to
            // run, so unwinding through its disposal releases the start like any other exit.
            Assert.Equal(string.Empty, await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await handler.StopAsync(CancellationToken.None);
        }
    }
}

public sealed class ConfigurableStartupProbe : IDisposable
{
    public TaskCompletionSource Populating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim Release { get; } = new();
    public bool FailPopulation { get; init; }
    public Action? BeforeAssignment { get; set; }
    public Action<ConfigurableStartupSubject>? Starting { get; set; }
    public void Dispose() => Release.Dispose();
}

[InterceptorSubject]
public partial class ConfigurableStartupSubject : IHostedService, IConfigurable
{
    private readonly ConfigurableStartupProbe _probe;
    private string _configuration = string.Empty;

    public ConfigurableStartupSubject(ConfigurableStartupProbe probe, IInterceptorSubjectContext context)
    {
        _probe = probe;
        ((IInterceptorSubject)this).Context.AddFallbackContext(context);
    }

    [Configuration]
    public string Configuration
    {
        get => _configuration;
        set
        {
            _probe.Populating.TrySetResult();
            if (!_probe.Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            _probe.BeforeAssignment?.Invoke();
            if (_probe.FailPopulation) throw new InvalidOperationException("Invalid configuration");
            _configuration = value;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _probe.Starting?.Invoke(this);
        _probe.Started.TrySetResult(Configuration);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
