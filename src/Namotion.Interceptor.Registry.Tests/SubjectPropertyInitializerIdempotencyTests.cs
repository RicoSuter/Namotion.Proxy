using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Re-attaching a subject builds a fresh registration over properties that are still on the subject,
/// so every initializer runs again. See <see cref="ISubjectPropertyInitializer"/>.
/// </summary>
public class SubjectPropertyInitializerIdempotencyTests
{
    private const string UnitAttribute = "Unit";

    private static (IInterceptorSubjectContext Context, IdempotentInitializer Initializer) CreateContext()
    {
        IInterceptorSubjectContext context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents();

        var initializer = new IdempotentInitializer();
        context.AddService<ISubjectPropertyInitializer>(initializer);
        return (context, initializer);
    }

    [Fact]
    public void WhenSubjectMovesBetweenParents_ThenAnIdempotentInitializerSucceeds()
    {
        // Arrange
        var (context, initializer) = CreateContext();
        var first = new MeasurementNode(context) { Name = "first" };
        var second = new MeasurementNode(context) { Name = "second" };
        var child = new MeasurementNode { Name = "child" };

        // Act: the move drops the reference count to zero, so the child leaves the registry and is
        // registered again under the new parent.
        first.Child = child;
        first.Child = null;
        second.Child = child;

        // Assert
        Assert.True(initializer.InvocationCount > 1, "Expected the initializer to run again on re-attach.");
        Assert.NotNull(((IInterceptorSubject)child).TryGetRegisteredSubject()?
            .TryGetProperty(nameof(MeasurementNode.Value))?
            .TryGetAttribute(UnitAttribute));
    }

    [Fact]
    public void WhenSubjectIsDetachedAndReattached_ThenAnIdempotentInitializerSucceeds()
    {
        // Arrange
        var (context, _) = CreateContext();
        var root = new MeasurementNode(context) { Name = "root" };
        var child = new MeasurementNode { Name = "child" };

        // Act
        root.Child = child;
        root.Child = null;
        root.Child = child;

        // Assert
        Assert.NotNull(((IInterceptorSubject)child).TryGetRegisteredSubject()?
            .TryGetProperty(nameof(MeasurementNode.Value))?
            .TryGetAttribute(UnitAttribute));
    }

    [Fact]
    public void WhenSubjectKeepsAReferenceThroughoutAMove_ThenTheInitializerDoesNotRunAgain()
    {
        // Arrange
        var (context, initializer) = CreateContext();
        var first = new MeasurementNode(context) { Name = "first" };
        var second = new MeasurementNode(context) { Name = "second" };
        var child = new MeasurementNode { Name = "child" };

        first.Child = child;
        var afterFirstAttach = initializer.InvocationCount;

        // Act: adding the second parent before removing the first keeps the reference count above
        // zero, so the child never leaves the registry.
        second.Child = child;
        first.Child = null;

        // Assert
        Assert.Equal(afterFirstAttach, initializer.InvocationCount);
    }

    [Fact]
    public void WhenAnInitializerAddsUnconditionally_ThenReattachFailsWithAnActionableError()
    {
        // Arrange
        IInterceptorSubjectContext context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents();
        context.AddService<ISubjectPropertyInitializer>(new UnconditionalInitializer());

        var root = new MeasurementNode(context) { Name = "root" };
        var child = new MeasurementNode { Name = "child" };
        root.Child = child;
        root.Child = null;

        // Act & Assert: the message has to name the property and point at the initializer contract,
        // because the underlying collection would otherwise report only a duplicate key.
        var exception = Assert.Throws<InvalidOperationException>(() => root.Child = child);

        Assert.Contains($"{nameof(MeasurementNode.Value)}@{UnitAttribute}", exception.Message);
        Assert.Contains(nameof(MeasurementNode), exception.Message);
        Assert.Contains(nameof(ISubjectPropertyInitializer), exception.Message);
    }

    [Fact]
    public void WhenAddingAPropertyThatAlreadyExists_ThenTheSubjectMetadataIsLeftUnchanged()
    {
        // Arrange
        var (context, _) = CreateContext();
        var subject = new MeasurementNode(context) { Name = "subject" };
        var interceptorSubject = (IInterceptorSubject)subject;
        var registered = interceptorSubject.TryGetRegisteredSubject()!;
        var declaredBefore = interceptorSubject.Properties[nameof(MeasurementNode.Value)];

        // Act & Assert: the name collides with a declared property, so the add is rejected.
        Assert.Throws<InvalidOperationException>(() =>
            registered.AddProperty(nameof(MeasurementNode.Value), typeof(string), _ => "hijacked", null));

        // Assert: the rejection has to happen before the subject is written to. Adding properties is
        // last-wins, so a check placed after it would leave the declared property carrying the
        // caller's dynamic getter.
        var declaredAfter = interceptorSubject.Properties[nameof(MeasurementNode.Value)];
        Assert.False(declaredAfter.IsDynamic);
        Assert.Equal(declaredBefore.Type, declaredAfter.Type);
    }

    private sealed class IdempotentInitializer : ISubjectPropertyInitializer
    {
        public int InvocationCount { get; private set; }

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.IsAttribute || property.Name != nameof(MeasurementNode.Value))
            {
                return;
            }

            InvocationCount++;

            if (property.TryGetAttribute(UnitAttribute) is not null)
            {
                return;
            }

            property.AddAttribute(UnitAttribute, typeof(string), _ => "celsius", null);
        }
    }

    private sealed class UnconditionalInitializer : ISubjectPropertyInitializer
    {
        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.IsAttribute || property.Name != nameof(MeasurementNode.Value))
            {
                return;
            }

            property.AddAttribute(UnitAttribute, typeof(string), _ => "celsius", null);
        }
    }
}

[InterceptorSubject]
public partial class MeasurementNode
{
    public partial string Name { get; set; }

    public partial double Value { get; set; }

    public partial MeasurementNode? Child { get; set; }

    public MeasurementNode()
    {
        Name = string.Empty;
    }
}
