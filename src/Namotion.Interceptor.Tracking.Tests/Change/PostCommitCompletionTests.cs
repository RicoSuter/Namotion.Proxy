using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PostCommitCompletionTests
{
    public PostCommitCompletionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAStructuralWriteThrowsAfterCommit_ThenQueuedChangesAndOwnershipStillSettle(bool observerThrows)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CompletionSubject(context);
        var child = new CompletionSubject { Name = "child" };
        var writeFailure = new InvalidOperationException("downstream");
        var observerFailure = new InvalidOperationException("observer");
        using var queue = context.CreatePropertyChangeQueueSubscription();
        var observed = 0;
        using var subscription = new PropertyReference(subject, nameof(CompletionSubject.Child))
            .SubscribeInline((in SubjectPropertyChange _) =>
            {
                observed++;
                if (observerThrows) throw observerFailure;
            });
        context.WithService(() => new ThrowingInterceptor(writeFailure, () => { }, propertyName: nameof(CompletionSubject.Child)));

        // Act
        var exception = Record.Exception(() => subject.Child = child);

        // Assert
        if (observerThrows)
        {
            var aggregate = Assert.IsType<AggregateException>(exception);
            Assert.Equal(new[] { writeFailure, observerFailure }, aggregate.InnerExceptions);
        }
        else Assert.Same(writeFailure, exception);
        Assert.Same(child, subject.Child);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal("child", Data(subject, nameof(CompletionSubject.ChildName)));
        Assert.Equal(1, observed);
        var changes = Drain(queue);
        Assert.Single(changes, item => item.Property.Name == nameof(CompletionSubject.Child));
        Assert.Single(changes, item => item.Property.Name == nameof(CompletionSubject.ChildName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenADownstreamInterceptorThrowsAfterCommit_ThenChangesPublishAndDerivedValuesSettle(bool subscribeDuringWrite)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CompletionSubject(context) { Name = "old" };
        var failure = new InvalidOperationException("downstream");
        PropertyChangeQueueSubscription? queue = subscribeDuringWrite ? null : context.CreatePropertyChangeQueueSubscription();
        context.WithService(() => new ThrowingInterceptor(failure,
            () => queue ??= context.CreatePropertyChangeQueueSubscription()));

        // Act
        var exception = Record.Exception(() => subject.Name = "new");

        // Assert
        using (queue)
        {
            Assert.Same(failure, exception);
            Assert.Contains(nameof(ThrowingInterceptor.WriteProperty), exception!.StackTrace);
            AssertSettled(subject);
            var changes = Drain(queue!);
            Assert.Equal(3, changes.Count);
            var change = Assert.Single(changes, item => item.Property.Name == nameof(CompletionSubject.Name));
            Assert.Equal("old", change.GetOldValue<string>());
            Assert.Equal("new", change.GetNewValue<string>());
            Assert.True(change.Revision > 0);
        }
    }

    [Fact]
    public void WhenADownstreamInterceptorThrowsBeforeCommit_ThenNoCompletionIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CompletionSubject(context) { Name = "old" };
        using var queue = context.CreatePropertyChangeQueueSubscription();
        var failure = new InvalidOperationException("before commit");
        context.WithService(() => new ThrowingInterceptor(failure, () => { }, beforeCommit: true));

        // Act
        var exception = Record.Exception(() => subject.Name = "new");

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal("old", subject.Name);
        Assert.Equal("old:first", Data(subject, nameof(CompletionSubject.First)));
        Assert.Equal("old:second", Data(subject, nameof(CompletionSubject.Second)));
        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void WhenPublicationAlsoThrows_ThenBothFailuresArePreservedAndDerivedValuesSettle()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CompletionSubject(context) { Name = "old" };
        using var queue = context.CreatePropertyChangeQueueSubscription();
        var primaryFailure = new InvalidOperationException("downstream");
        var publicationFailure = new InvalidOperationException("publication");
        var publicationCount = 0;
        using var subscription = new PropertyReference(subject, nameof(CompletionSubject.Name))
            .SubscribeInline((in SubjectPropertyChange _) =>
            {
                publicationCount++;
                throw publicationFailure;
            });
        context.WithService(() => new ThrowingInterceptor(primaryFailure, () => { }));

        // Act
        var exception = Record.Exception(() => subject.Name = "new");

        // Assert
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Equal(new[] { primaryFailure, publicationFailure }, aggregate.InnerExceptions);
        Assert.Equal(1, publicationCount);
        AssertSettled(subject);
        Assert.Equal(3, Drain(queue).Count);
    }

    [Fact]
    public void WhenANestedObserverWriteRetriggersAFailingRecalculation_ThenBothFailuresArePreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CompletionSubject(context) { Name = "old" };
        var firstFailure = new InvalidOperationException("first notification");
        var retriggerFailure = new InvalidOperationException("retrigger notification");
        var notifications = 0;
        using var subscription = new PropertyReference(subject, nameof(CompletionSubject.First))
            .SubscribeInline((in SubjectPropertyChange _) =>
            {
                if (++notifications == 1)
                {
                    subject.Name = "latest";
                    throw firstFailure;
                }

                throw retriggerFailure;
            });

        // Act
        var exception = Record.Exception(() => subject.Name = "new");

        // Assert
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Equal(new[] { firstFailure, retriggerFailure }, aggregate.InnerExceptions);
        Assert.Equal(2, notifications);
        Assert.Equal("latest", subject.Name);
        Assert.Equal("latest:first", Data(subject, nameof(CompletionSubject.First)));
        Assert.Equal("latest:second", Data(subject, nameof(CompletionSubject.Second)));
    }

    [Fact]
    public void WhenDependentNotificationsThrow_ThenRemainingDependentsStillSettle()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var subject = new CompletionSubject(context) { Name = "old" };
        using var queue = context.CreatePropertyChangeQueueSubscription();
        var firstFailure = new InvalidOperationException("first");
        var secondFailure = new InvalidOperationException("second");
        using var first = new PropertyReference(subject, nameof(CompletionSubject.First))
            .SubscribeInline((in SubjectPropertyChange _) => throw firstFailure);
        using var second = new PropertyReference(subject, nameof(CompletionSubject.Second))
            .SubscribeInline((in SubjectPropertyChange _) => throw secondFailure);

        // Act
        var exception = Record.Exception(() => subject.Name = "new");

        // Assert
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(firstFailure, aggregate.InnerExceptions);
        Assert.Contains(secondFailure, aggregate.InnerExceptions);
        AssertSettled(subject);
        Assert.Equal(3, Drain(queue).Count);
    }

    private static object? Data(CompletionSubject subject, string name) =>
        new PropertyReference(subject, name).GetDerivedPropertyData().LastKnownValue;

    private static void AssertSettled(CompletionSubject subject)
    {
        Assert.Equal("new", subject.Name);
        Assert.Equal("new:first", Data(subject, nameof(CompletionSubject.First)));
        Assert.Equal("new:second", Data(subject, nameof(CompletionSubject.Second)));
    }

    private static List<SubjectPropertyChange> Drain(PropertyChangeQueueSubscription queue)
    {
        var changes = new List<SubjectPropertyChange>();
        while (queue.TryDequeueImmediate(out var change)) changes.Add(change);
        return changes;
    }

    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class ThrowingInterceptor(Exception failure, Action afterCommit, bool beforeCommit = false,
        string propertyName = nameof(CompletionSubject.Name)) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.Property.Name != propertyName)
            {
                next(ref context);
                return;
            }

            if (beforeCommit) throw failure;
            next(ref context);
            afterCommit();
            throw failure;
        }
    }
}

[InterceptorSubject]
public partial class CompletionSubject
{
    public partial string? Name { get; set; }
    public partial CompletionSubject? Child { get; set; }
    [Derived] public string First => Name + ":first";
    [Derived] public string Second => Name + ":second";
    [Derived] public string? ChildName => Child?.Name;
}
