using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceDiagnosticsTests
{
    [Fact]
    public void WhenNeverStarted_ThenTheSourceExposesAnEmptyDiagnostics()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);

        // Act
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);

        // Assert
        Assert.NotNull(source.Diagnostics);
        Assert.Null(source.Diagnostics.StartTime);
        Assert.Null(source.Diagnostics.IsOperational);
        Assert.Null(source.Diagnostics.ClaimedPropertyCount);
    }

    [Fact]
    public void WhenReadThroughEitherInterface_ThenItIsTheSourcesOwnDiagnostics()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);

        // Act
        var throughSource = ((ISubjectSource)source).Diagnostics;
        var throughConnector = ((ISubjectConnector)source).Diagnostics;

        // Assert
        // ISubjectSource narrows the member, so the two interfaces are separate slots that both have
        // to land on the source's single view: a second view would report empty buffers.
        Assert.Same(source.Diagnostics, throughSource);
        Assert.Same(source.Diagnostics, throughConnector);
    }

    [Fact]
    public async Task WhenStarted_ThenTheCounterEpochIsStamped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);

        // Act
        await ((IHostedService)source).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.StartTime is not null,
            message: "The pump did not run through the connector's diagnostics lifecycle.");

        // Assert
        Assert.NotNull(source.Diagnostics.StartTime);
        await ((IHostedService)source).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenStartedWithoutLivenessReporting_ThenTheSourceIsSynchronizedWithUnavailableLiveness()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);

        // Act
        await ((IHostedService)source).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.State == SourceState.Synchronized,
            message: "The source did not complete its initial synchronization.");

        // Assert
        Assert.Null(source.Diagnostics.IsOperational);
        await ((IHostedService)source).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenAConnectAttemptFails_ThenTheErrorIsRecordedWithoutTheSourceStopping()
    {
        // Arrange: the long retry time keeps the source parked in the backoff after the single
        // failure, so the state asserted below is the one that failure left behind.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance, retryTime: TimeSpan.FromMinutes(1))
        {
            StartListeningFailure = new InvalidOperationException("cannot connect")
        };

        // Act
        await ((IHostedService)source).StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.LastError is not null,
            message: "The swallowed connect failure was not reported to the diagnostics.");

        // Assert
        Assert.IsType<InvalidOperationException>(source.Diagnostics.LastError);
        Assert.Equal(SourceState.Synchronizing, source.State);
        await ((IHostedService)source).StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenTheStopTearsDownAConnectAttempt_ThenTheTeardownFailureIsNotRecorded()
    {
        // Arrange: a connect that fails with something other than the cancellation once the stop
        // reaches it, like a torn-down transport. Recorded, it would replace the genuine fault of a
        // source that had already failed, and a stopped source never clears that sticky error.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithLifecycle();
        var subject = new Person(context);
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            StartListeningOverride = async (_, cancellationToken) =>
            {
                connecting.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("the connection was torn down by the stop");
                }

                return null;
            }
        };

        await ((IHostedService)source).StartAsync(CancellationToken.None);
        await connecting.Task;

        // Act
        await ((IHostedService)source).StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(source.Diagnostics.LastError);
    }
}
