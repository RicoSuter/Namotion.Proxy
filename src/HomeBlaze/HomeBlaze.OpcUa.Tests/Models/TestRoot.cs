using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.OpcUa.Tests;

/// <summary>
/// What the root configuration file deserializes into, so the real <see cref="HomeBlaze.Services.RootManager"/>
/// can reach its loaded state.
/// </summary>
[InterceptorSubject]
public partial class TestRoot : IConfigurable
{
    [Configuration]
    public partial string Name { get; set; }

    public TestRoot()
    {
        Name = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
