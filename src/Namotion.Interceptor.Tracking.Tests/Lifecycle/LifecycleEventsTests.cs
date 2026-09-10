using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class LifecycleEventsTests
{
    [Fact]
    public void SubjectAttached_FiresWithCorrectReferenceCount()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        SubjectLifecycleChange? capturedEvent = null;
        lifecycleInterceptor.SubjectAttached += change => capturedEvent = change;

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act
        parent.Father = child;

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(child, capturedEvent.Value.Subject);
        Assert.Equal(1, capturedEvent.Value.ReferenceCount);
        Assert.NotNull(capturedEvent.Value.Property);
        Assert.Equal("Father", capturedEvent.Value.Property.Value.Name);
    }

    [Fact]
    public void SubjectDetaching_FiresWithCorrectReferenceCount()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        SubjectLifecycleChange? capturedEvent = null;
        lifecycleInterceptor.SubjectDetaching += change => capturedEvent = change;

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        parent.Father = child;

        // Act
        parent.Father = null;

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(child, capturedEvent.Value.Subject);
        Assert.Equal(0, capturedEvent.Value.ReferenceCount);
        Assert.NotNull(capturedEvent.Value.Property);
        Assert.Equal("Father", capturedEvent.Value.Property.Value.Name);
    }

    [Fact]
    public void MultipleReferences_EventsFireOnceForAttachAndDetach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();

        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetaching += change => detachedEvents.Add(change);

        var person = new Person(context) { FirstName = "Person" };
        var parent = new Person { FirstName = "Parent" };

        // Act - Attach same subject via multiple properties
        person.Father = parent;
        person.Mother = parent;

        // Assert - SubjectAttached fires once (on first attach)
        var parentAttachEvents = attachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Single(parentAttachEvents);
        Assert.Equal(1, parentAttachEvents[0].ReferenceCount);
        Assert.Equal("Father", parentAttachEvents[0].Property?.Name);

        // Verify reference count is updated
        Assert.Equal(2, parent.GetReferenceCount());

        // Act - Detach one reference (subject still in graph)
        person.Father = null;

        // Assert - SubjectDetaching does NOT fire yet (still has one reference)
        Assert.Empty(detachedEvents);
        Assert.Equal(1, parent.GetReferenceCount());

        // Act - Detach second reference (subject leaves graph)
        person.Mother = null;

        // Assert - SubjectDetaching fires once (on last detach)
        Assert.Single(detachedEvents);
        Assert.Equal(parent, detachedEvents[0].Subject);
        Assert.Equal(0, detachedEvents[0].ReferenceCount);
        Assert.Equal("Mother", detachedEvents[0].Property?.Name);
    }

    [Fact]
    public void MultipleReferencesInArray_EventsFireWithCorrectCounts()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();

        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetaching += change => detachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        // Act - Attach children in array
        parent.Children = [child1, child2];

        // Assert - Both children attached with count 1
        // Note: attachedEvents[0] is for 'parent' itself, [1] and [2] are for children
        var childAttachEvents = attachedEvents.Where(e => e.Subject == child1 || e.Subject == child2).ToList();
        Assert.Equal(2, childAttachEvents.Count);
        Assert.Equal(1, childAttachEvents[0].ReferenceCount);
        Assert.Equal(1, childAttachEvents[1].ReferenceCount);
        Assert.Equal(0, childAttachEvents[0].Index);
        Assert.Equal(1, childAttachEvents[1].Index);

        // Act - Replace array with one child
        parent.Children = [child2];

        // Assert - child1 detached, child2 remains attached (no new attach event for child2)
        Assert.Single(detachedEvents);
        Assert.Equal(child1, detachedEvents[0].Subject);
        Assert.Equal(0, detachedEvents[0].ReferenceCount);
        Assert.Equal(2, childAttachEvents.Count); // No new attach event for child2

        // Act - Clear array
        parent.Children = [];

        // Assert - child2 detached
        Assert.Equal(2, detachedEvents.Count);
        Assert.Equal(child2, detachedEvents[1].Subject);
        Assert.Equal(0, detachedEvents[1].ReferenceCount);
    }

    [Fact]
    public void TryGetLifecycleInterceptor_ReturnsInterceptor_WhenConfigured()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        // Assert
        Assert.NotNull(lifecycleInterceptor);
        Assert.IsType<LifecycleInterceptor>(lifecycleInterceptor);
    }

    [Fact]
    public void TryGetLifecycleInterceptor_ReturnsNull_WhenNotConfigured()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create();

        // Act
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        // Assert
        Assert.Null(lifecycleInterceptor);
    }

    [Fact]
    public void GetReferenceCount_ReturnsZero_ForUnattachedSubject()
    {
        // Arrange
        var child = new Person { FirstName = "Child" };

        // Act
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetReferenceCount_ReturnsOne_AfterSingleAttach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        // Act
        person.Father = child;
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public void GetReferenceCount_ReturnsZero_AfterDetach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        person.Father = child;

        // Act
        person.Father = null;
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetReferenceCount_ReturnsCorrectCount_WithMultipleReferences()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var person1 = new Person(context) { FirstName = "Person1" };
        var person2 = new Person(context) { FirstName = "Person2" };
        var parent = new Person { FirstName = "Parent" };

        // Act & Assert
        person1.Father = parent;
        Assert.Equal(1, parent.GetReferenceCount());

        person1.Mother = parent;
        Assert.Equal(2, parent.GetReferenceCount());

        person2.Father = parent;
        Assert.Equal(3, parent.GetReferenceCount());

        person1.Father = null;
        Assert.Equal(2, parent.GetReferenceCount());

        person1.Mother = null;
        Assert.Equal(1, parent.GetReferenceCount());

        person2.Father = null;
        Assert.Equal(0, parent.GetReferenceCount());
    }

    [Fact]
    public void GetReferenceCount_ReturnsZero_WhenLifecycleNotEnabled()
    {
        // Arrange - Context without lifecycle
        var context = InterceptorSubjectContext.Create();

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        // Act
        person.Father = child;
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(0, count); // Should return 0 when lifecycle is not enabled
    }

    [Fact]
    public void SubjectAttached_FiresForRootSubject_WhenCreatedWithContext()
    {
        // Arrange
        var attachedEvents = new List<SubjectLifecycleChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);

        // Act
        var person = new Person(context) { FirstName = "Person" };

        // Assert
        Assert.Single(attachedEvents);
        Assert.Equal(person, attachedEvents[0].Subject);
        Assert.Equal(0, attachedEvents[0].ReferenceCount); // Root subjects have no property references
        Assert.Null(attachedEvents[0].Property); // Root subject has no parent property
    }

    [Fact]
    public void SubjectDetaching_FiresForRootSubject_WhenContextRemoved()
    {
        // Arrange
        var detachedEvents = new List<SubjectLifecycleChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        lifecycleInterceptor.SubjectDetaching += change => detachedEvents.Add(change);

        var person = new Person(context) { FirstName = "Person" };

        // Act: promote the constructor's provisional anchor and give it up, releasing the root.
        ((IInterceptorSubject)person).AttachToContext(context);
        ((IInterceptorSubject)person).DetachFromContext(context);

        // Assert
        Assert.Single(detachedEvents);
        Assert.Equal(person, detachedEvents[0].Subject);
        Assert.Equal(0, detachedEvents[0].ReferenceCount);
        Assert.Null(detachedEvents[0].Property); // Root subject has no parent property
    }

    [Fact]
    public void SubjectEvents_DoNotFire_WhenPropertyIsSetToSameValue()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        person.Father = child;

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetaching += change => detachedEvents.Add(change);

        // Act - Set to same value
        person.Father = child;

        // Assert - No new events should fire
        Assert.Empty(attachedEvents);
        Assert.Empty(detachedEvents);
        Assert.Equal(1, child.GetReferenceCount()); // Count should remain 1
    }

    [Fact]
    public void SubjectEvents_FireInCorrectOrder_WhenReplacingReference()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        person.Father = child1;

        var events = new List<(string type, IInterceptorSubject subject, int count)>();
        lifecycleInterceptor.SubjectAttached += change =>
            events.Add(("Subject Attached", change.Subject, change.ReferenceCount));
        lifecycleInterceptor.SubjectDetaching += change =>
            events.Add(("Subject Detaching", change.Subject, change.ReferenceCount));

        // Act - Replace child1 with child2
        person.Father = child2;

        // Assert - Detach of old value happens before attach of new value
        Assert.Equal(2, events.Count);
        Assert.Equal("Subject Detaching", events[0].type);
        Assert.Equal(child1, events[0].subject);
        Assert.Equal(0, events[0].count);
        Assert.Equal("Subject Attached", events[1].type);
        Assert.Equal(child2, events[1].subject);
        Assert.Equal(1, events[1].count);
    }

    [Fact]
    public void SubjectAttached_FiresAfterHandler_And_SubjectDetaching_FiresBeforeHandler()
    {
        // Arrange
        var events = new List<(string source, string eventType, IInterceptorSubject subject)>();

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => new EventOrderTracker(events));

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        lifecycleInterceptor.SubjectAttached += change =>
            events.Add(("Event", "Attached", change.Subject));
        lifecycleInterceptor.SubjectDetaching += change =>
            events.Add(("Event", "Detaching", change.Subject));

        // Act - attach
        var person = new Person(context) { FirstName = "TestPerson" };

        // Assert - SubjectAttached fires AFTER HandleLifecycleChange(attach)
        Assert.Equal(2, events.Count);
        Assert.Equal("Handler", events[0].source);
        Assert.Equal("attached", events[0].eventType);
        Assert.Equal(person, events[0].subject);
        Assert.Equal("Event", events[1].source);
        Assert.Equal("Attached", events[1].eventType);
        Assert.Equal(person, events[1].subject);

        events.Clear();

        // Act - detach: promote the constructor's provisional anchor, then give it up.
        ((IInterceptorSubject)person).AttachToContext(context);
        ((IInterceptorSubject)person).DetachFromContext(context);

        // Assert - SubjectDetaching fires BEFORE HandleLifecycleChange(detach)
        Assert.Equal(2, events.Count);
        Assert.Equal("Event", events[0].source);
        Assert.Equal("Detaching", events[0].eventType);
        Assert.Equal(person, events[0].subject);
        Assert.Equal("Handler", events[1].source);
        Assert.Equal("detached", events[1].eventType);
        Assert.Equal(person, events[1].subject);
    }

    [Fact]
    public void WhenSubjectAppearsTwiceInSameCollection_ThenItAttachesAndDetachesOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        var attachCount = 0;
        var detachCount = 0;
        lifecycleInterceptor.SubjectAttached += _ => attachCount++;
        lifecycleInterceptor.SubjectDetaching += _ => detachCount++;

        // Act: both collection slots hold the same subject. Every occurrence is one edge, so the
        // subject attaches once (context attach on the first occurrence) but counts two references.
        parent.Children = [child, child];

        // Assert
        Assert.Equal(1, attachCount);
        Assert.Equal(2, child.GetReferenceCount());

        // Act: clearing the collection must detach the duplicated subject exactly once.
        parent.Children = [];

        // Assert
        Assert.Equal(1, detachCount);
        Assert.Equal(0, child.GetReferenceCount());
    }

    private class EventOrderTracker : ILifecycleHandler
    {
        private readonly List<(string source, string eventType, IInterceptorSubject subject)> _events;

        public EventOrderTracker(List<(string source, string eventType, IInterceptorSubject subject)> events) => _events = events;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            var type = change.IsContextAttach ? "attached" : change.IsContextDetach ? "detached" : null;
            if (type is not null)
            {
                _events.Add(("Handler", type, change.Subject));
            }
        }
    }
}
