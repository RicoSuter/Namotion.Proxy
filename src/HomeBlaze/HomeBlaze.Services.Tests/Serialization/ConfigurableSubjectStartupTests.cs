using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenConfigurationIsPopulated_ThenHostedStartupReadsTheConfiguredValue(bool explicitContextConstructor)
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
        var type = explicitContextConstructor ? typeof(ExplicitContextStartupSubject) : typeof(ConfigurableStartupSubject);
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
            Assert.Same(context, subject.TryGetContext());
            if (subject is ExplicitContextStartupSubject explicitSubject)
                Assert.Same(context, explicitSubject.SuppliedContext);
        }
        finally
        {
            probe.Release.Set();
            await deserialize.WaitAsync(TimeSpan.FromSeconds(10));
            await handler.StopAsync(CancellationToken.None);
        }
    }
    [Fact]
    public async Task WhenConfigurationPopulationThrows_ThenThePartiallyConfiguredServiceNeverStarts()
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
            using var next = new ConfigurableStartupProbe();
            _ = new ConfigurableStartupSubject(next, context);
            await next.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.False(probe.Started.Task.IsCompleted);
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
    public void Dispose() => Release.Dispose();
}

[InterceptorSubject]
public partial class ConfigurableStartupSubject : IHostedService, IConfigurable
{
    private readonly ConfigurableStartupProbe _probe;
    private string _configuration = string.Empty;

    public ConfigurableStartupSubject(ConfigurableStartupProbe probe) => _probe = probe;

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
        _probe.Started.TrySetResult(Configuration);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ExplicitContextStartupSubject : ConfigurableStartupSubject
{
    public IInterceptorSubjectContext SuppliedContext { get; }

    public ExplicitContextStartupSubject(ConfigurableStartupProbe probe, IInterceptorSubjectContext context) : base(probe)
    {
        SuppliedContext = context;
        this.AttachToContext(context);
    }
}
