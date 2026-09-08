using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackDelegateSnapshotTests
{
    [Fact]
    public void WhenEventHandlerMutatesSubscribersAndThrows_ThenCurrentSnapshotFinishesAndNextEventUsesNewSubscribers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var parent = new Person(context);
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var events = new List<string>();
        var failure = new InvalidOperationException("first subscriber");
        Action<SubjectLifecycleChange> second = _ => events.Add("second");
        Action<SubjectLifecycleChange> third = _ => events.Add("third");
        lifecycle.SubjectAttached += _ =>
        {
            events.Add("first");
            lifecycle.SubjectAttached -= second;
            lifecycle.SubjectAttached += third;
            throw failure;
        };
        lifecycle.SubjectAttached += second;

        // Act
        var firstException = Record.Exception(() => parent.Father = new Person());
        var secondException = Record.Exception(() => parent.Mother = new Person());

        // Assert
        Assert.Same(failure, firstException);
        Assert.Same(failure, secondException);
        Assert.Equal(["first", "second", "first", "third"], events);
        Assert.Same(context, parent.Father!.GetContext());
        Assert.Same(context, parent.Mother!.GetContext());
    }
}
