using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// The host and context bootstrap the hosting tests share. Tests that wire something extra in
/// before the host is built, a deferrer or a second context, call <see cref="CreateContext"/> and
/// build the host themselves.
/// </summary>
internal static class HostingTestHost
{
    /// <summary>
    /// Waits for everything already queued on the target's chain to have run.
    /// </summary>
    /// <remarks>
    /// An empty transition on the same chain. Appending never runs a body, so this completes only once
    /// everything ahead of it has, which is what makes a read after it deterministic rather than timed.
    /// </remarks>
    public static Task DrainAsync(this HostedServiceTarget target)
        => target.AppendAsync(() => Task.CompletedTask);

    /// <summary>
    /// Waits for everything already queued on the attachment's chain to have run. See
    /// <see cref="DrainAsync(HostedServiceTarget)"/>.
    /// </summary>
    public static Task DrainAsync(this IHostedServiceAttachment attachment)
        => ((IHostedServiceAttachmentTarget)attachment).Target.DrainAsync();

    /// <summary>
    /// Creates the host builder every test in this suite builds on.
    /// </summary>
    /// <remarks>
    /// Defaults are off because they watch appsettings.json for reloads, and this suite builds a host
    /// per test and stops it without disposing it, so those watchers accumulate for the life of the
    /// run until the operating system refuses another inotify instance and an unrelated test fails
    /// inside host construction. Logging is the one default that has to come back, because the handler
    /// registration resolves ILogger&lt;HostedServiceHandler&gt; as required.
    /// </remarks>
    public static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        return builder;
    }

    /// <summary>
    /// Creates the context under test, with hosting wired into <paramref name="builder"/>'s services.
    /// </summary>
    /// <remarks>
    /// WithContextInheritance, not just WithLifecycle: without it a child subject's Context never
    /// resolves the handler and every child scenario is silently unreachable.
    /// </remarks>
    public static IInterceptorSubjectContext CreateContext(HostApplicationBuilder builder)
        => InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

    /// <summary>
    /// Starts a host over a fresh context. The caller stops the host, which tests that stop it
    /// mid-scenario need.
    /// </summary>
    public static async Task<(IHost Host, IInterceptorSubjectContext Context)> StartAsync()
    {
        var builder = CreateBuilder();
        var context = CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();
        return (host, context);
    }

    /// <summary>
    /// Starts one host over two hosting enabled contexts, which is what every scenario needs where one
    /// subject is reachable from two handlers.
    /// </summary>
    public static async Task<(IHost Host, IInterceptorSubjectContext First, IInterceptorSubjectContext Second)>
        StartWithTwoContextsAsync()
    {
        var builder = CreateBuilder();
        var first = CreateContext(builder);
        var second = CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();
        return (host, first, second);
    }

    /// <summary>
    /// Runs <paramref name="action"/> against a started host and stops the host afterwards.
    /// </summary>
    public static async Task RunAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var (host, context) = await StartAsync();
        try
        {
            await action(context);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> against the two contexts of a started host and stops it afterwards.
    /// </summary>
    public static async Task RunWithTwoContextsAsync(
        Func<IInterceptorSubjectContext, IInterceptorSubjectContext, Task> action)
    {
        var (host, first, second) = await StartWithTwoContextsAsync();
        try
        {
            await action(first, second);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
