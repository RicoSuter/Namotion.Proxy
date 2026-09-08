using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyRecorderTests
{
    private static DerivedPropertyRecorder CreateRecorder() => new();

    private static PropertyReference CreateSelf(IInterceptorSubject subject) =>
        new(subject, "__SelfUnderTest");

    [Fact]
    public void StartRecording_SetsIsRecordingTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        // Act
        recorder.StartRecording(CreateSelf(person));

        // Assert
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public void FinishRecording_SetsIsRecordingFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        recorder.StartRecording(CreateSelf(person));

        // Act
        _ = recorder.FinishRecording();

        // Assert
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void TouchProperty_RecordsProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(CreateSelf(person));

        // Act
        recorder.TouchProperty(ref property);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Single(recorded.ToArray());
        Assert.Equal(property, recorded.ToArray()[0]);
    }

    [Fact]
    public void TouchProperty_DeduplicatesSameProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(CreateSelf(person));

        // Act - touch same property multiple times
        recorder.TouchProperty(ref property);
        recorder.TouchProperty(ref property);
        recorder.TouchProperty(ref property);
        var recorded = recorder.FinishRecording();

        // Assert - should only appear once
        Assert.Single(recorded.ToArray());
    }

    [Fact]
    public void TouchProperty_RecordsMultipleDistinctProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));

        recorder.StartRecording(CreateSelf(person));

        // Act
        recorder.TouchProperty(ref firstNameProperty);
        recorder.TouchProperty(ref lastNameProperty);
        var recorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, recorded.Length);
        Assert.Contains(firstNameProperty, recorded.ToArray());
        Assert.Contains(lastNameProperty, recorded.ToArray());
    }

    [Fact]
    public void TouchProperty_SkipsSelfReference()
    {
        // The derived property's outer interceptor-chain read fires ReadProperty on itself,
        // which must not be recorded as a dependency.

        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var selfProperty = new PropertyReference(person, nameof(Person.FullName));
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(selfProperty);

        // Act - touch both the self-ref and a real dependency
        recorder.TouchProperty(ref selfProperty);
        recorder.TouchProperty(ref firstNameProperty);
        var recorded = recorder.FinishRecording();

        // Assert - only the real dependency is recorded
        Assert.Single(recorded.ToArray());
        Assert.Equal(firstNameProperty, recorded.ToArray()[0]);
    }

    [Fact]
    public void NestedRecording_EachFrameSkipsOnlyItsOwnSelf()
    {
        // Nested recording: outer frame records inner's property; inner frame records its own deps.
        // Each frame must exclude only its own self-ref, not the other's.

        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var outerSelf = new PropertyReference(person, "OuterDerived");
        var innerSelf = new PropertyReference(person, "InnerDerived");
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));

        // Act - outer frame touches inner's self-ref (legitimate dependency on the inner derived);
        //       inner frame touches its own self-ref (must be filtered).
        recorder.StartRecording(outerSelf);
        recorder.TouchProperty(ref innerSelf);

        recorder.StartRecording(innerSelf);
        recorder.TouchProperty(ref innerSelf);       // filtered (inner's self)
        recorder.TouchProperty(ref firstNameProperty);
        var innerRecorded = recorder.FinishRecording();

        var outerRecorded = recorder.FinishRecording();

        // Assert
        Assert.Single(innerRecorded.ToArray());
        Assert.Equal(firstNameProperty, innerRecorded.ToArray()[0]);

        Assert.Single(outerRecorded.ToArray());
        Assert.Equal(innerSelf, outerRecorded.ToArray()[0]);
    }

    [Fact]
    public void NestedRecording_MaintainsSeparateFrames()
    {
        // When a derived property's getter triggers recalculation of another derived property,
        // StartRecording() nests. Each frame must record only its own dependencies so that
        // StoreRecordedTouchedProperties builds the correct RequiredProperties per property.
        // Without frame isolation, outer properties would incorrectly include inner dependencies.

        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var firstNameProperty = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(Person.LastName));
        var fatherProperty = new PropertyReference(person, nameof(Person.Father));

        // Act - nested recording
        recorder.StartRecording(CreateSelf(person));
        recorder.TouchProperty(ref firstNameProperty);

        recorder.StartRecording(CreateSelf(person)); // Nested
        recorder.TouchProperty(ref lastNameProperty);
        recorder.TouchProperty(ref fatherProperty);
        var innerRecorded = recorder.FinishRecording();

        var outerRecorded = recorder.FinishRecording();

        // Assert
        Assert.Equal(2, innerRecorded.Length);
        Assert.Contains(lastNameProperty, innerRecorded.ToArray());
        Assert.Contains(fatherProperty, innerRecorded.ToArray());

        Assert.Single(outerRecorded.ToArray());
        Assert.Contains(firstNameProperty, outerRecorded.ToArray());
    }

    [Fact]
    public void Recording_GrowsBufferWhenNeeded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        recorder.StartRecording(CreateSelf(person));

        // Act - add more properties than initial buffer size (8)
        var properties = new List<PropertyReference>();
        for (var i = 0; i < 20; i++)
        {
            var property = new PropertyReference(person, $"Prop{i}");
            properties.Add(property);
            recorder.TouchProperty(ref property);
        }

        var recorded = recorder.FinishRecording();

        // Assert - all properties recorded and match
        Assert.Equal(20, recorded.Length);
        for (var i = 0; i < properties.Count; i++)
        {
            Assert.Equal(properties[i], recorded[i]);
        }
    }

    [Fact]
    public void MultipleRecordingSessions_ReuseBuffers()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();

        // Act - the first session is the one that rents the pooled buffer
        recorder.StartRecording(CreateSelf(person));
        TouchProperties(recorder, person, count: 5);
        var firstRecording = recorder.FinishRecording();

        // Identity of the backing storage is what proves reuse. Asserting on the recorded
        // values instead would pass just as happily if every session rented a fresh buffer,
        // which is the regression this test exists to catch.
        ref var firstSlot = ref MemoryMarshal.GetReference(firstRecording);

        Assert.Equal(5, firstRecording.Length);

        // Act - every later session stays within the rented capacity, so none may rent again
        for (var session = 1; session < 10; session++)
        {
            recorder.StartRecording(CreateSelf(person));
            TouchProperties(recorder, person, count: 5);
            var recording = recorder.FinishRecording();

            // Assert
            Assert.Equal(5, recording.Length);
            Assert.True(
                Unsafe.AreSame(ref firstSlot, ref MemoryMarshal.GetReference(recording)),
                $"Session {session} rented a new buffer instead of reusing the first session's.");
        }

        // Assert
        Assert.False(recorder.IsRecording);
    }

    private static void TouchProperties(DerivedPropertyRecorder recorder, IInterceptorSubject subject, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var property = new PropertyReference(subject, $"Prop{i}");
            recorder.TouchProperty(ref property);
        }
    }

    [Fact]
    public void ClearLastRecording_SetsCountToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Person(context);
        var recorder = CreateRecorder();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        recorder.StartRecording(CreateSelf(person));
        recorder.TouchProperty(ref property);
        _ = recorder.FinishRecording();

        // Act
        recorder.ClearLastRecording();

        // Assert - next recording session should start fresh
        recorder.StartRecording(CreateSelf(person));
        var recorded = recorder.FinishRecording();
        Assert.Equal(0, recorded.Length);
    }
}
