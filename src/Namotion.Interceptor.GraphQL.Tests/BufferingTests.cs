using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class BufferingTests
{
    [Fact]
    public void DefaultBufferTime_Is50Milliseconds()
    {
        var configuration = new GraphQLSubjectConfiguration();
        Assert.Equal(TimeSpan.FromMilliseconds(50), configuration.BufferTime);
    }

    [Fact]
    public async Task Subscription_WithBuffering_BatchesRapidChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context);

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL(
                sp => sp.GetRequiredService<Sensor>(),
                _ => new GraphQLSubjectConfiguration
                {
                    BufferTime = TimeSpan.FromMilliseconds(500)
                })
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        var result = await executor.ExecuteAsync("subscription { root { temperature } }");
        var stream = ((IResponseStream)result).ReadResultsAsync();
        var enumerator = stream.GetAsyncEnumerator();

        // Start reading from the stream to establish the subscription.
        // MoveNextAsync triggers HotChocolate to invoke the Subscribe lambda,
        // which sets up the observable pipeline.
        var firstMoveTask = enumerator.MoveNextAsync().AsTask();

        // Give the subscription pipeline time to be established
        await Task.Delay(100);

        // Act - rapid changes within buffer window
        sensor.Temperature = 10.0m;
        sensor.Temperature = 20.0m;
        sensor.Temperature = 30.0m;

        // Wait for buffer to flush (buffer is 500ms)
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hasFirst = await firstMoveTask.WaitAsync(cancellationTokenSource.Token);
        Assert.True(hasFirst, "Should receive at least one update");

        // Collect any additional updates with a short timeout
        var updateCount = 1; // We already got the first one
        try
        {
            using var additionalTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            while (await enumerator.MoveNextAsync().AsTask().WaitAsync(additionalTimeout.Token))
            {
                updateCount++;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected - no more updates
        }

        // With a 500ms buffer and synchronous property changes,
        // all 3 changes should be batched into exactly 1 update.
        Assert.Equal(1, updateCount);
    }
}
