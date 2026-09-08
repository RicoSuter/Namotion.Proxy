using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit.Abstractions;

namespace HomeBlaze.E2E.Tests;

[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class BrowserStartupTests(PlaywrightFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task WhenBrowserOpensBeforeRootLoadingCompletes_ThenRootPropertiesAppearAfterLoading()
    {
        // Arrange
        using var factory = new StartupHostFactory();
        var address = factory.ServerAddress;
        var barrier = factory.ServerServices.GetRequiredService<AttachBarrier>();
        var manager = factory.ServerServices.GetRequiredService<RootManager>();
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(manager.Root);
        Assert.False(manager.IsLoaded);
        await using var browserContext = await fixture.Browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        page.PageError += (_, error) => output.WriteLine(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") output.WriteLine(message.Text);
        };

        try
        {
            // Establish an interactive circuit before entering the browser so prerender quiescence
            // cannot postpone the entire HTTP response until the root is ready.
            await page.GotoAsync(address + "Error");
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle Developer Mode" }).ClickAsync();

            // Act
            await page.GetByRole(AriaRole.Link, new() { Name = "Browser", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#scrollContainer")).ToBeVisibleAsync();
            factory.ReleaseAttachments();

            // Assert
            // The constructor pause can leave storage disconnected while its hosted service races
            // configuration population. Browsing must still expose its state and operations.
            await Assertions.Expect(page.Locator("#scrollContainer strong:text-is('Status:')")).ToBeVisibleAsync(
                new() { Timeout = 15000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true })).ToBeVisibleAsync(
                new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        }
        catch
        {
            output.WriteLine($"Root loaded: {manager.IsLoaded}; execution: {manager.ExecuteTask?.Status}; failure: {manager.ExecuteTask?.Exception}");
            output.WriteLine(await page.ContentAsync());
            throw;
        }
        finally
        {
            factory.ReleaseAttachments();
        }
    }

    private sealed class StartupHostFactory : WebTestingHostFactory<App>
    {
        private readonly List<AttachBarrier> _barriers = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options => options.DetailedErrors = true);
                services.AddSingleton(_ =>
                {
                    var barrier = new AttachBarrier();
                    lock (_barriers) _barriers.Add(barrier);
                    return barrier;
                });
                services.RemoveAll<RootManager>();
                services.AddSingleton(serviceProvider =>
                {
                    serviceProvider.GetRequiredService<IInterceptorSubjectContext>()
                        .GetService<LifecycleInterceptor>().SubjectAttached +=
                        serviceProvider.GetRequiredService<AttachBarrier>().HandleLifecycleChange;
                    return ActivatorUtilities.CreateInstance<RootManager>(serviceProvider);
                });
            });
        }

        public void ReleaseAttachments()
        {
            lock (_barriers)
            {
                foreach (var barrier in _barriers) barrier.Release.Set();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseAttachments();
            base.Dispose(disposing);
        }
    }

    private sealed class AttachBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsContextAttach) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Root attachment was not released");
        }
    }
}
