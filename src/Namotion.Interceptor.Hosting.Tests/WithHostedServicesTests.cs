using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Hosting.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Hosting.Tests;

public class WithHostedServicesTests
{
    [Fact]
    public async Task WhenTwoContextsShareOneServiceCollection_ThenBothHandlersRun()
    {
        // Arrange
        var builder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(builder);

        var secondContext = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Act
            var firstPerson = new Person(firstContext);
            var secondPerson = new Person(secondContext);
            firstPerson.AttachHostedService(() => new PersonBackgroundService(firstPerson));
            secondPerson.AttachHostedService(() => new PersonBackgroundService(secondPerson));

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => firstPerson.FirstName == "John");
            await AsyncTestHelpers.WaitUntilAsync(() => secondPerson.FirstName == "John",
                message: "The second context's handler was dropped by TryAddEnumerable dedupe.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenOneSubjectIsReachableFromTwoHostingContexts_ThenItIsStartedOnce()
    {
        // Arrange - both contexts resolve their own handler and both see the subject's context attach,
        // so without a single owner per target the subject is started twice.
        var builder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(builder);

        var secondContext = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var subject = new CountingHostedSubject();

            // Act
            ((IInterceptorSubject)subject).Context.AddFallbackContext(firstContext);
            ((IInterceptorSubject)subject).Context.AddFallbackContext(secondContext);

            // Assert - the empty transition drains the target's chain, so the count is read once every
            // queued start has run.
            await ((IInterceptorSubject)subject)
                .TryGetSubjectTarget()!
                .AppendAsync(() => Task.CompletedTask);

            Assert.Equal(1, subject.StartCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAttachCannotResolveOneHandler_ThenTheSubjectStoresNoAttachment()
    {
        // Arrange - two reachable hosting contexts make the handler lookup throw, and the attach has to
        // be all or nothing: a factory stored by a call the caller saw fail is started by the next
        // context attach, and nothing holds a handle to stop it.
        var builder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(builder);

        var secondContext = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var person = new Person(firstContext);
            var subject = (IInterceptorSubject)person;
            subject.Context.AddFallbackContext(secondContext);

            var created = 0;

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => person.AttachHostedService(() =>
            {
                Interlocked.Increment(ref created);
                return new TrackedBackgroundService();
            }));

            // Assert
            Assert.Contains("exactly one service", exception.Message);
            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));

            // A context detach and re-attach is what turns a stored factory into a running instance the
            // caller has no handle to. Deterministic either way: every chain the re-attach can append
            // to belongs to an attachment this list holds.
            await ReAttachToSingleContextAsync(subject, firstContext, secondContext);

            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAwaitedAttachCannotResolveOneHandler_ThenTheSubjectStoresNoAttachment()
    {
        // Arrange - the awaiting overload adds its attachment on its own path, so it needs its own test.
        var builder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(builder);

        var secondContext = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var person = new Person(firstContext);
            var subject = (IInterceptorSubject)person;
            subject.Context.AddFallbackContext(secondContext);

            var created = 0;

            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => person.AttachHostedServiceAsync(
                () =>
                {
                    Interlocked.Increment(ref created);
                    return new TrackedBackgroundService();
                }, CancellationToken.None));

            // Assert
            Assert.Contains("exactly one service", exception.Message);
            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));

            // For the reason on the synchronous overload.
            await ReAttachToSingleContextAsync(subject, firstContext, secondContext);

            Assert.Empty(subject.GetHostedServiceAttachments());
            Assert.Equal(0, Volatile.Read(ref created));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenADetachCannotResolveOneHandler_ThenTheAttachmentAndItsInstanceSurvive()
    {
        // Arrange - the attachment is taken while one context is reachable, so the detach below is the
        // first call whose lookup throws. A detach that removed it first would leave the instance
        // running with no stop appended and nothing left to reach it through.
        var builder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(builder);

        var secondContext = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var person = new Person(firstContext);
            var subject = (IInterceptorSubject)person;

            var attachment = await person.AttachHostedServiceAsync(
                () => new TrackedBackgroundService(), CancellationToken.None);

            var instance = attachment.Current;
            Assert.NotNull(instance);
            Assert.True(instance.IsStarted);

            subject.Context.AddFallbackContext(secondContext);

            // Act
            var exception = Assert.Throws<InvalidOperationException>(() => person.DetachHostedService(attachment));

            // Assert - nothing removed, nothing stopped and nothing disposed
            Assert.Contains("exactly one service", exception.Message);
            Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Same(instance, attachment.Current);
            Assert.False(instance.IsStopped);
            Assert.False(instance.IsDisposed);

            // Still the attachment a detach acts on, rather than one the handler alone still holds.
            subject.Context.RemoveFallbackContext(secondContext);
            Assert.True(person.DetachHostedService(attachment));
            await attachment.DrainAsync();
            Assert.True(instance.IsStopped);
            Assert.True(instance.IsDisposed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task WhenAnAwaitedDetachCannotResolveOneHandler_ThenTheAttachmentAndItsInstanceSurvive()
    {
        // Arrange - the awaiting overload removes its attachment on its own path, so it needs its own
        // test. See the synchronous overload for what the removal would cost.
        var builder = HostingTestHost.CreateBuilder();

        var firstContext = HostingTestHost.CreateContext(builder);

        var secondContext = HostingTestHost.CreateContext(builder);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            var person = new Person(firstContext);
            var subject = (IInterceptorSubject)person;

            var attachment = await person.AttachHostedServiceAsync(
                () => new TrackedBackgroundService(), CancellationToken.None);

            var instance = attachment.Current;
            Assert.NotNull(instance);
            Assert.True(instance.IsStarted);

            subject.Context.AddFallbackContext(secondContext);

            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => person.DetachHostedServiceAsync(attachment, CancellationToken.None));

            // Assert - nothing removed, nothing stopped and nothing disposed
            Assert.Contains("exactly one service", exception.Message);
            Assert.Same(attachment, Assert.Single(subject.GetHostedServiceAttachments()));
            Assert.Same(instance, attachment.Current);
            Assert.False(instance.IsStopped);
            Assert.False(instance.IsDisposed);

            // Still the attachment a detach acts on, rather than one the handler alone still holds.
            subject.Context.RemoveFallbackContext(secondContext);
            Assert.True(await person.DetachHostedServiceAsync(attachment, CancellationToken.None));
            Assert.True(instance.IsStopped);
            Assert.True(instance.IsDisposed);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Takes the subject down to one hosting context and cycles it out of that one and back in, which
    /// is the graph event that starts whatever attachments the subject holds. Drains every chain the
    /// re-attach could have appended to, so a read after it is ordered rather than timed.
    /// </summary>
    private static async Task ReAttachToSingleContextAsync(
        IInterceptorSubject subject,
        IInterceptorSubjectContext firstContext,
        IInterceptorSubjectContext secondContext)
    {
        subject.Context.RemoveFallbackContext(secondContext);
        subject.Context.RemoveFallbackContext(firstContext);
        subject.Context.AddFallbackContext(firstContext);

        foreach (var attachment in subject.GetHostedServiceAttachments())
        {
            await attachment.DrainAsync();
        }
    }
}
