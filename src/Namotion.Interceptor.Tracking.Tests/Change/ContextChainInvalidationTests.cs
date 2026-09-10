using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class ContextChainInvalidationTests
{
    [Fact]
    public void WhenServicesAreRegisteredAfterChainWasCompiled_ThenTheNextWriteSeesThem()
    {
        // Arrange: the first write compiles and caches the context's interceptor chain. The
        // lifecycle is registered before the subject attaches, because registering one behind an
        // attach is rejected; the services this test adds afterwards are the ones that do not
        // constrain their order.
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var person = new Person(context);
        person.LastName = "compiles the write chain";

        // Act: registering services publishes a fresh state, which has to retire the already
        // compiled chain.
        context.WithFullPropertyTracking();

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        person.LastName = "Doe";

        // Assert
        Assert.Single(changes);
        Assert.Equal(nameof(Person.LastName), changes[0].Property.Name);
    }
}
