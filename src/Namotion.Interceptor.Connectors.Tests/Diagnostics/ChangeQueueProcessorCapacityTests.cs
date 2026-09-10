using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Diagnostics;

public class ChangeQueueProcessorCapacityTests
{
    [Fact]
    public void WhenMaxQueueDepthIsZeroOnTheBufferedPath_ThenConstructionThrowsWithBothRemedies()
    {
        // Arrange: the throw lands inside a connector's retry loop, which catches it and tries again,
        // so the message is all the caller gets and has to name both remedies.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeQueueProcessor(
            source,
            subscription,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            TimeSpan.FromMilliseconds(8),
            maxQueueDepth: 0,
            logger: NullLogger.Instance));

        Assert.Equal("maxQueueDepth", exception.ParamName);
        Assert.Contains("null", exception.Message);
        Assert.Contains("buffer time of zero", exception.Message);
    }

    [Fact]
    public void WhenMaxQueueDepthIsZeroOnTheImmediatePath_ThenConstructionSucceeds()
    {
        // Arrange: the immediate path never fills the queue the bound applies to, so the remedy the
        // message above names is one a caller can actually take.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new Person(context);
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        using var processor = new ChangeQueueProcessor(
            source,
            subscription,
            _ => true,
            (_, _) => ValueTask.CompletedTask,
            ChangeDeliveryRule.SourceValuesMayBeStale,
            TimeSpan.Zero,
            maxQueueDepth: 0,
            logger: NullLogger.Instance);

        // Assert
        Assert.Equal(0, processor.QueueDepth);
        Assert.Equal(0L, processor.DropCount);
    }
}
