using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// <c>WithLifecycle()</c> is the one configuration entry point for graph ownership: context
/// inheritance and parent tracking are intrinsic to the built-in lifecycle rather than opt-in
/// handlers, and the removed <c>WithContextInheritance()</c> and <c>WithParents()</c> have no
/// aliases. The default lifecycle is registered idempotently, and a custom
/// <see cref="ILifecycleInterceptor"/> conflicts with it through the singleton contract.
/// </summary>
public class LifecycleConfigurationTests
{
    #region Context inheritance is intrinsic

    [Fact]
    public void WhenPropertyIsAssigned_ThenContextIsInherited()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var person = new Person(context);
        person.Mother = new Person { FirstName = "Mother" };

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.Mother!.GetResolvedServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoInheritTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), mother.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), grandmother.GetResolvedServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenPropertyWithDeepStructureIsRemoved_ThenChildrenStopResolvingTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var grandmother = new Person
        {
            FirstName = "Grandmother"
        };

        var mother = new Person
        {
            FirstName = "Mother",
            Mother = grandmother
        };

        var person = new Person(context)
        {
            FirstName = "Child",
            Mother = mother
        };

        // Act
        person.Mother = null;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Empty(mother.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Empty(grandmother.GetResolvedServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenArrayIsAssigned_ThenAllChildrenInheritTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var person = new Person(context)
        {
            FirstName = "Mother",
            Children = [
                child1,
                child2
            ]
        };

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), person.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child1.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child2.GetResolvedServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenUsingCircularDependencies_ThenAllSubjectsInheritTheContext()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var child3 = new Person { FirstName = "Child3" };

        child1.Mother = child2;
        child2.Mother = child3;
        child3.Mother = child1;

        // Assert
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child1.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child2.GetResolvedServices<ILifecycleInterceptor>());
        Assert.Equal(context.GetServices<ILifecycleInterceptor>(), child3.GetResolvedServices<ILifecycleInterceptor>());
    }


    #endregion

    #region Parent tracking is intrinsic

    [Fact]
    public void WhenReferencedByTwoPropertiesOfTheSameParent_ThenTwoParentsAreReported()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        // Assert
        var parents = parent.GetParents();
        Assert.Equal(2, parents.Length);
    }

    [Fact]
    public void WhenReferencesAreSetToNull_ThenParentsAreEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var parent = new Person(context)
        {
            FirstName = "Parent"
        };

        var person = new Person(context);
        person.FirstName = "Child";
        person.Mother = parent;
        person.Father = parent;

        // Act
        person.Mother = null;
        person.Father = null;

        // Assert
        var parents = parent.GetParents();
        Assert.Empty(parents);
    }

    [Fact]
    public void WhenReferencedByTwoOtherSubjects_ThenItHasTwoParents()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var mother = new Person(context);
        mother.FirstName = "Mother";

        var child1 = new Person(context);
        child1.FirstName = "Child1";
        child1.Mother = mother;

        var child2 = new Person(context)
        {
            FirstName = "Child2",
            Mother = mother
        };

        // Assert
        var parents = mother.GetParents();
        Assert.Equal(2, parents.Length);
    }

    #endregion

    #region Registration is idempotent, the singleton contract guards conflicts

    [Fact]
    public void WhenLifecycleIsConfiguredRepeatedly_ThenOneLifecycleIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithLifecycle()
            .WithFullPropertyTracking();

        // Assert: one authority, appearing exactly once in the ordered handler fan-out.
        Assert.Single(context.GetServices<ILifecycleInterceptor>());
        Assert.Single(context.GetServices<ILifecycleHandler>(), handler => handler is LifecycleInterceptor);
    }

    [Fact]
    public void WhenACustomLifecycleIsRegistered_ThenWithLifecycleThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<ILifecycleInterceptor>(new CustomLifecycle());

        // Act & Assert: the default lifecycle is not silently skipped, because half the
        // configuration surface would then quietly run against an implementation it was not
        // written for. The singleton contract turns the ambiguity into an error.
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithLifecycle());
        Assert.Contains("singleton contract", exception.Message);
    }

    private sealed class CustomLifecycle : ILifecycleInterceptor
    {
        public void EnterStructuralWriteGate()
        {
        }

        public void ExitStructuralWriteGate()
        {
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
            => throw new NotSupportedException();

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
            => throw new NotSupportedException();

        public bool TryAddProperties(SubjectPropertyRegistration registration)
            => throw new NotSupportedException();

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
            => next(ref context);
    }

    #endregion
}

/// <summary>
/// The services a subject resolves through the one exact context it is attached to; an
/// unattached subject resolves nothing.
/// </summary>
file static class SubjectServiceResolutionExtensions
{
    public static ImmutableArray<TService> GetResolvedServices<TService>(this IInterceptorSubject subject)
    {
        return subject.TryGetContext()?.GetServices<TService>() ?? [];
    }
}
