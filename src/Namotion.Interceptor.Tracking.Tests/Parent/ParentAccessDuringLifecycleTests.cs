using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// Tests that parent state is published before a subject's own ILifecycleHandler runs, so a subject
/// can access its parent hierarchy during initialization. Parent tracking is intrinsic to
/// WithLifecycle(); no separate opt-in exists.
/// </summary>
public class ParentAccessDuringLifecycleTests
{
    [Fact]
    public void WhenComponentAttachedToSimulation_ThenParentsAndRootAreAvailableDuringAttachSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act: Attach component to simulation
        simulation.Component = component;

        // Assert: Component should have found parents and root during its AttachSubjectToContext
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenComponentAddedToArray_ThenParentsAreSetBeforeAttachSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act: Add component to array
        simulation.Components = [component];

        // Assert: Component should have found parents during its AttachSubjectToContext
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenComponentWithoutContextAttachedViaContextInheritance_ThenParentsAreSetBeforeAttachSubject()
    {
        // This test specifically tests the scenario where:
        // 1. Root has a context with the lifecycle registered
        // 2. Child is created WITHOUT context
        // 3. When child is attached, the context is inherited AND parents should be set

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var simulation = new Simulation(context) { Name = "Root" };

        // Component created without context - inherits it when the lifecycle attaches it
        var component = new Component { Name = "Child" };

        // Act: Attach component - context will be inherited, parents should be set
        simulation.Component = component;

        // Assert: Parents should be available when component's AttachSubjectToContext runs
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.NotNull(component.RootFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenOnlyLifecycleIsConfigured_ThenParentsAreAvailableDuringAttach()
    {
        // Arrange: parent state is owned by the lifecycle rather than by an opt-in handler. This
        // is a deliberate behavior change: on master this configuration returned empty parents.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var simulation = new Simulation(context) { Name = "Root" };
        var component = new Component { Name = "Child" };

        // Act
        simulation.Component = component;

        // Assert
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.NotEmpty(component.ParentsFoundDuringAttach);
        Assert.Same(simulation, component.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenNestedHierarchy_ThenAllComponentsCanFindRoot()
    {
        // Test: root.Component.ChildComponent - nested hierarchy
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var simulation = new Simulation(context) { Name = "Root" };
        var outerComponent = new Component { Name = "Outer" };
        var innerComponent = new Component { Name = "Inner" };

        // Build hierarchy: simulation -> outerComponent -> innerComponent
        simulation.Component = outerComponent;
        outerComponent.ChildComponent = innerComponent;

        // Assert: Outer component finds root
        Assert.Null(outerComponent.AttachException);
        Assert.NotNull(outerComponent.ParentsFoundDuringAttach);
        Assert.NotEmpty(outerComponent.ParentsFoundDuringAttach);
        Assert.NotNull(outerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, outerComponent.RootFoundDuringAttach);

        // Assert: Inner component finds root (through outer)
        Assert.Null(innerComponent.AttachException);
        Assert.NotNull(innerComponent.ParentsFoundDuringAttach);
        Assert.NotEmpty(innerComponent.ParentsFoundDuringAttach);
        Assert.NotNull(innerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, innerComponent.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenNestedHierarchyBuiltBeforeAttach_ThenAllComponentsCanFindRoot()
    {
        // Test: Build hierarchy first, then attach to context
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var simulation = new Simulation { Name = "Root" };  // No context yet
        var outerComponent = new Component { Name = "Outer" };
        var innerComponent = new Component { Name = "Inner" };

        // Build hierarchy first (no context)
        simulation.Component = outerComponent;
        outerComponent.ChildComponent = innerComponent;

        // Now attach context - should trigger attach for all
        ((IInterceptorSubject)simulation).AttachToContext(context);

        // Assert: All components should have found their parents during attach
        Assert.Null(outerComponent.AttachException);
        Assert.NotNull(outerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, outerComponent.RootFoundDuringAttach);

        Assert.Null(innerComponent.AttachException);
        Assert.NotNull(innerComponent.RootFoundDuringAttach);
        Assert.Same(simulation, innerComponent.RootFoundDuringAttach);
    }

    [Fact]
    public void WhenComponentIsRoot_ThenNoParentIsFound()
    {
        // When a component is created as a root (not attached to a parent),
        // it should have no parents during AttachSubjectToContext
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        // Component is created directly with context - it IS the root
        var component = new Component(context) { Name = "RootComponent" };

        // Assert: No parents because it's the root
        Assert.Null(component.AttachException);
        Assert.NotNull(component.ParentsFoundDuringAttach);
        Assert.Empty(component.ParentsFoundDuringAttach);  // No parents!
        Assert.Null(component.RootFoundDuringAttach);      // Can't find Simulation because there isn't one
    }

    [Fact]
    public void VerifyHandlerOrder_ParentStateIsPublishedBeforeSubjectHandler()
    {
        // This test verifies the exact order of handler invocations
        var handlerCallOrder = new List<string>();

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        // Add a custom handler to track the order
        context.AddService(new OrderTrackingHandler(handlerCallOrder));

        var root = new RootWithTrackedChild(context) { Name = "Root" };
        var child = new TrackedChild(handlerCallOrder) { Name = "Child" };

        // Act
        root.Child = child;

        // Assert: Verify that parents were available when subject's handler ran
        Assert.Contains("TrackedChild.AttachSubjectToContext - HasParents: True", handlerCallOrder);
    }

    [Fact]
    public void WhenFourLevelHierarchy_ThenTryGetFirstParentFindsInterfaceOnSecondLevel()
    {
        // Arrange: Create a 4-level tree: a => b => c => d
        // Where 'b' implements ISpecialMarker interface
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var levelA = new LevelA(context) { Name = "A" };
        var levelB = new LevelB { Name = "B" };  // Implements ISpecialMarker
        var levelC = new LevelC { Name = "C" };
        var levelD = new LevelD { Name = "D" };

        // Build hierarchy: A -> B -> C -> D
        levelA.Child = levelB;
        levelB.Child = levelC;
        levelC.Child = levelD;

        // Act: From D, find first parent implementing ISpecialMarker (should be B)
        var foundParent = levelD.TryGetFirstParent<ISpecialMarker>();

        // Assert
        Assert.NotNull(foundParent);
        Assert.Same(levelB, foundParent);
    }

    [Fact]
    public void WhenMultipleParents_AndFirstParentDoesNotHaveInterface_ThenFindsSecondParent()
    {
        // Arrange: Create a multi-parent scenario where:
        // - Child has two parents: ParentWithoutMarker and ParentWithMarker
        // - First parent does NOT implement ISpecialMarker
        // - Second parent DOES implement ISpecialMarker
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parentWithoutMarker = new ParentWithoutMarker(context) { Name = "NoMarker" };
        var parentWithMarker = new ParentWithMarker(context) { Name = "WithMarker" };
        var child = new MultiParentChild { Name = "Child" };

        // Attach child to first parent (no marker)
        parentWithoutMarker.Child = child;

        // Also attach child to second parent (has marker) - creates multi-parent
        parentWithMarker.Child = child;

        // Act: Find first parent implementing ISpecialMarker
        var foundParent = child.TryGetFirstParent<ISpecialMarker>();

        // Assert: Should find parentWithMarker even though parentWithoutMarker was added first
        Assert.NotNull(foundParent);
        Assert.Same(parentWithMarker, foundParent);
    }

    private class OrderTrackingHandler : ILifecycleHandler
    {
        private readonly List<string> _callOrder;

        public OrderTrackingHandler(List<string> callOrder)
        {
            _callOrder = callOrder;
        }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                _callOrder.Add($"OrderTrackingHandler.OnAttached for {change.Subject.GetType().Name}");
            }

            if (change.IsContextDetach)
            {
                _callOrder.Add($"OrderTrackingHandler.OnDetached for {change.Subject.GetType().Name}");
            }
        }
    }
}

/// <summary>
/// A root subject that holds a tracked child.
/// </summary>
[InterceptorSubject]
public partial class RootWithTrackedChild
{
    public partial string Name { get; set; }

    public partial TrackedChild? Child { get; set; }

    public RootWithTrackedChild()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// A component that tracks the order of handler calls.
/// </summary>
[InterceptorSubject]
public partial class TrackedChild : ILifecycleHandler
{
    private readonly List<string> _callOrder;

    public partial string Name { get; set; }

    public TrackedChild(List<string> callOrder)
    {
        _callOrder = callOrder;
        Name = string.Empty;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            var hasParents = this.GetParents().Length > 0;
            _callOrder.Add($"TrackedChild.AttachSubjectToContext - HasParents: {hasParents}");
        }

        if (change.IsContextDetach)
        {
            _callOrder.Add("TrackedChild.DetachSubjectFromContext");
        }
    }
}

/// <summary>
/// A root subject for testing parent tracking scenarios.
/// </summary>
[InterceptorSubject]
public partial class Simulation
{
    public partial string Name { get; set; }

    public partial Component? Component { get; set; }

    public partial Component[] Components { get; set; }

    public Simulation()
    {
        Name = string.Empty;
        Components = [];
    }
}

/// <summary>
/// A component that implements ILifecycleHandler and tries to access its parent during AttachSubjectToContext.
/// Used to test that parent tracking is set up before the subject's own lifecycle handler runs.
/// </summary>
[InterceptorSubject]
public partial class Component : ILifecycleHandler
{
    public partial string Name { get; set; }

    /// <summary>
    /// Nested child component for testing hierarchies like root.component.component
    /// </summary>
    public partial Component? ChildComponent { get; set; }

    public Component()
    {
        Name = string.Empty;
    }

    /// <summary>
    /// Stores the root found during AttachSubjectToContext (if any).
    /// </summary>
    public Simulation? RootFoundDuringAttach { get; private set; }

    /// <summary>
    /// Stores any exception that occurred during AttachSubjectToContext.
    /// </summary>
    public Exception? AttachException { get; private set; }

    /// <summary>
    /// Stores the parents found during AttachSubjectToContext.
    /// </summary>
    public HashSet<SubjectParent>? ParentsFoundDuringAttach { get; private set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextAttach)
        {
            try
            {
                // Store the parents at the moment OnAttached is called
                ParentsFoundDuringAttach = new HashSet<SubjectParent>(this.GetParents());

                // Try to find the root simulation via parent traversal
                RootFoundDuringAttach = this.TryGetFirstParent<Simulation>();
            }
            catch (Exception ex)
            {
                AttachException = ex;
            }
        }
    }
}

/// <summary>
/// Marker interface for testing TryGetFirstParent with interface type parameter.
/// </summary>
public interface ISpecialMarker
{
}

/// <summary>
/// Root level (level A) of the 4-level hierarchy test.
/// </summary>
[InterceptorSubject]
public partial class LevelA
{
    public partial string Name { get; set; }
    public partial LevelB? Child { get; set; }

    public LevelA()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// Level B of the 4-level hierarchy - implements ISpecialMarker.
/// </summary>
[InterceptorSubject]
public partial class LevelB : ISpecialMarker
{
    public partial string Name { get; set; }
    public partial LevelC? Child { get; set; }

    public LevelB()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// Level C of the 4-level hierarchy.
/// </summary>
[InterceptorSubject]
public partial class LevelC
{
    public partial string Name { get; set; }
    public partial LevelD? Child { get; set; }

    public LevelC()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// Leaf level (level D) of the 4-level hierarchy.
/// </summary>
[InterceptorSubject]
public partial class LevelD
{
    public partial string Name { get; set; }

    public LevelD()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// Parent that does NOT implement ISpecialMarker for multi-parent test.
/// </summary>
[InterceptorSubject]
public partial class ParentWithoutMarker
{
    public partial string Name { get; set; }
    public partial MultiParentChild? Child { get; set; }

    public ParentWithoutMarker()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// Parent that implements ISpecialMarker for multi-parent test.
/// </summary>
[InterceptorSubject]
public partial class ParentWithMarker : ISpecialMarker
{
    public partial string Name { get; set; }
    public partial MultiParentChild? Child { get; set; }

    public ParentWithMarker()
    {
        Name = string.Empty;
    }
}

/// <summary>
/// Child that can have multiple parents for multi-parent test.
/// </summary>
[InterceptorSubject]
public partial class MultiParentChild
{
    public partial string Name { get; set; }

    public MultiParentChild()
    {
        Name = string.Empty;
    }
}
