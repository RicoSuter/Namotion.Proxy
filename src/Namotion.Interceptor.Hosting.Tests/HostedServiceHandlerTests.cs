using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

public class HostedServiceHandlerTests
{
    [Fact]
    public async Task WhenSubjectImplementsIHostedService_ThenItIsStartedAndStopped()
    {
        // Arrange
        PersonWithBackgroundService person = null!;

        // Act
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new PersonWithBackgroundService(context);
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Assert
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
        Assert.Equal("Disposed", person.FirstName);
    }
    
    [Fact]
    public async Task WhenHostedServiceIsAttachedToSubject_ThenHostedServiceIsStarted()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
            
            // Act
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);

            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            // Assert
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
        Assert.Equal("Disposed", person.FirstName);
    }
    
    [Fact]
    public async Task WhenHostedServiceIsDetachedFromSubject_ThenHostedServiceIsStopped()
    {
        // Arrange
        Person person;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
         
            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.GetAttachedHostedServices();

            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");

            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
            Assert.Single(attachedHostedServices);

            // Act
            person.DetachHostedService(hostedService);
            attachedHostedServices = person.GetAttachedHostedServices();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
            Assert.Equal("Disposed", person.FirstName);
            Assert.Empty(attachedHostedServices);
        });
    }

    [Fact]
    public async Task WhenSubjectServiceIsDetached_ThenHostedServiceIsStopped()
    {
        // Arrange
        Person person;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);

            var hostedService = new PersonBackgroundService(person);
            person.AttachHostedService(hostedService);
            var attachedHostedServices = person.GetAttachedHostedServices();

            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "John");
            Assert.Single(attachedHostedServices);

            // Act
            ((IInterceptorSubject)person).Context.RemoveFallbackContext(context);
            attachedHostedServices = person.GetAttachedHostedServices();

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => person.FirstName == "Disposed");
            Assert.Equal("Disposed", person.FirstName);
            Assert.Empty(attachedHostedServices); // the service has been stopped and
                                                   // removed from list (not allowed to restart again anyway)
        });
    }

    [Fact]
    public async Task WhenHostedServiceIsAttachedAsync_ThenServiceIsStartedAndAwaited()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Act - AttachHostedServiceAsync should wait for StartAsync to complete
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);

            // Assert - Service should be running immediately after await returns
            Assert.Equal("John", person.FirstName);
            Assert.Equal("Doe", person.LastName);
            Assert.Single(person.GetAttachedHostedServices());
        });

        await AsyncTestHelpers.WaitUntilAsync(() => person!.FirstName == "Disposed");
        Assert.Equal("Disposed", person!.FirstName);
    }

    [Fact]
    public async Task WhenHostedServiceIsDetachedAsync_ThenServiceIsStoppedAndAwaited()
    {
        // Arrange
        Person person = null!;
        await RunWithAppLifecycleAsync(async context =>
        {
            person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Start the service
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);
            Assert.Equal("John", person.FirstName);

            // Act - DetachHostedServiceAsync should wait for StopAsync to complete
            await person.DetachHostedServiceAsync(hostedService, CancellationToken.None);

            // Assert - Service should be stopped immediately after await returns
            Assert.Equal("Disposed", person.FirstName);
            Assert.Empty(person.GetAttachedHostedServices());
        });
    }

    [Fact]
    public async Task WhenAttachHostedServiceAsyncCalledTwice_ThenOnlyStartsOnce()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Act - Attach same service twice
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);
            await person.AttachHostedServiceAsync(hostedService, CancellationToken.None);

            // Assert - Should only be in the collection once
            Assert.Single(person.GetAttachedHostedServices());
            Assert.Equal("John", person.FirstName);
        });
    }


    private static async Task RunWithAppLifecycleAsync(Func<IInterceptorSubjectContext, Task> action)
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();
        try
        {
            await action(context);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
