using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class InitializerReattachTests
{
    /// <summary>
    /// Adds an attribute property the way a real initializer does, so the added property lands on
    /// the subject and outlives detach while Registry's projection of it does not.
    /// </summary>
    private sealed class UnitInitializer : ISubjectPropertyInitializer
    {
        public int Invocations;

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.Name != nameof(Person.LastName))
            {
                return;
            }

            Invocations++;
            property.AddAttribute("Unit", typeof(string), _ => "kg", null);
        }
    }

    [Fact]
    public void WhenASubjectCarryingAnInitializerAddedAttributeReattaches_ThenTheRerunIsAbsorbed()
    {
        // Arrange: an initializer that adds an attribute property, and a child that will move
        // out of and back into the graph. The added property survives detach on the subject
        // while the projection of it is rebuilt per attach, so the rerun re-registers a name
        // that is already there.
        var initializer = new UnitInitializer();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        context.AddService<ISubjectPropertyInitializer>(initializer);

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "C" };
        root.Father = child;
        var invocationsAfterFirstAttach = initializer.Invocations;

        // Act: an ordinary move, the old reference removed before the new one is added.
        root.Father = null;
        var exception = Record.Exception(() => root.Father = child);

        // Assert: the rerun happens and is absorbed, and the attribute still resolves through the
        // rebuilt projection rather than being lost or duplicated.
        Assert.Null(exception);
        Assert.True(initializer.Invocations > invocationsAfterFirstAttach,
            "the initializer is expected to rerun on reattach; this test covers that the rerun is harmless");

        var registeredChild = child.TryGetRegisteredSubject();
        Assert.NotNull(registeredChild);
        Assert.NotNull(registeredChild.TryGetProperty($"{nameof(Person.LastName)}@Unit"));
    }
}
