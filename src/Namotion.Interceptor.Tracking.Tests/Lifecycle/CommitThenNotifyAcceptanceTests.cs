using System.Collections;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Lifecycle.SupportContractAssertions;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CommitThenNotifyAcceptanceTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void WhenAnAttachCallbackFailsAfterAdoptingProvisionalRoots_ThenOnlyCommittedEdgesSupportThem(
        int provisionalCount, bool hasOutsideSupport)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var provisionalRoots = Enumerable.Range(0, provisionalCount)
            .Select(_ => new Person(context) { Father = new Person() }).ToArray();
        var grandchildren = provisionalRoots.Select(subject => subject.Father!).ToArray();
        var refusedChild = new Person();
        var root = new Person { Children = [.. provisionalRoots, refusedChild] };
        var outside = new Person();
        if (hasOutsideSupport)
        {
            outside.AttachToContext(context);
            outside.Father = provisionalRoots[0];
        }
        var failure = FailAttach(context, refusedChild);

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        AssertAttached(context, root, 0);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        AssertAttached(context, refusedChild, 1);
        for (var index = 0; index < provisionalRoots.Length; index++)
        {
            var provisional = provisionalRoots[index];
            Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
            AssertAttached(context, provisional, hasOutsideSupport && index == 0 ? 2 : 1);
            AssertAttached(context, grandchildren[index], 1);
            AssertEdge(context, provisional, new PropertyReference(root, nameof(Person.Children)), index);
            AssertEdge(context, grandchildren[index], new PropertyReference(provisional, nameof(Person.Father)));
        }
        AssertEdge(context, refusedChild, new PropertyReference(root, nameof(Person.Children)), provisionalCount);
        if (hasOutsideSupport)
            AssertEdge(context, provisionalRoots[0], new PropertyReference(outside, nameof(Person.Father)));
        Assert.Equal(2 + 2 * provisionalCount + (hasOutsideSupport ? 1 : 0), Registry(context).KnownSubjects.Count);

        // Act
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, root, refusedChild);
        for (var index = hasOutsideSupport ? 1 : 0; index < provisionalRoots.Length; index++)
            AssertReleased(context, provisionalRoots[index], grandchildren[index]);
        if (hasOutsideSupport)
        {
            AssertAttached(context, outside, 0);
            AssertAttached(context, provisionalRoots[0], 1);
            AssertAttached(context, grandchildren[0], 1);
            AssertEdge(context, provisionalRoots[0], new PropertyReference(outside, nameof(Person.Father)));
            Assert.Equal(3, Registry(context).KnownSubjects.Count);

            // Act
            outside.Father = null;
            outside.DetachFromContext(context);

            // Assert
            AssertReleased(context, outside, provisionalRoots[0], grandchildren[0]);
        }
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenANestedAttachCallbackFails_ThenTheOuterDrainReportsItAndBothRootsRemainIndependentlyOwned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var innerProvisional = new Person(context) { Father = new Person() };
        var outerProvisional = new Person(context);
        var refusedChild = new Person();
        var innerRoot = new Person { Father = innerProvisional, Mother = refusedChild };
        var outerRoot = new EnumerableChildrenHolder();
        var innerReturned = false;
        Exception? innerFailure = null;
        var entered = false;
        outerRoot.Children = new HookEnumerable([outerProvisional], () =>
        {
            if (entered || outerRoot.TryGetContext() is null) return;
            entered = true;
            innerFailure = Record.Exception(() => innerRoot.AttachToContext(context));
            innerReturned = true;
        });
        var failure = FailAttach(context, refusedChild);

        // Act
        var outerFailure = Record.Exception(() => outerRoot.AttachToContext(context));

        // Assert
        Assert.True(entered);
        Assert.True(innerReturned);
        Assert.Null(innerFailure);
        Assert.Same(failure, outerFailure);
        AssertAttached(context, innerRoot, 0);
        AssertAttached(context, outerRoot, 0);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)innerRoot).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)outerRoot).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)innerProvisional).Executor.AttachmentAnchor);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)outerProvisional).Executor.AttachmentAnchor);
        AssertAttached(context, innerProvisional, 1);
        AssertAttached(context, innerProvisional.Father!, 1);
        AssertAttached(context, outerProvisional, 1);
        AssertAttached(context, refusedChild, 1);
        AssertEdge(context, innerProvisional, new PropertyReference(innerRoot, nameof(Person.Father)));
        AssertEdge(context, refusedChild, new PropertyReference(innerRoot, nameof(Person.Mother)));
        AssertEdge(context, innerProvisional.Father!, new PropertyReference(innerProvisional, nameof(Person.Father)));
        AssertEdge(context, outerProvisional, new PropertyReference(outerRoot, nameof(EnumerableChildrenHolder.Children)), 0);
        Assert.Equal(6, Registry(context).KnownSubjects.Count);

        // Act
        innerRoot.DetachFromContext(context);

        // Assert
        AssertReleased(context, innerRoot, innerProvisional, innerProvisional.Father!, refusedChild);
        AssertAttached(context, outerRoot, 0);
        AssertAttached(context, outerProvisional, 1);
        Assert.Equal(2, Registry(context).KnownSubjects.Count);

        // Act
        outerRoot.DetachFromContext(context);

        // Assert
        AssertReleased(context, outerRoot, outerProvisional);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenAnAdmittedPropertyCallbackFails_ThenItsPublishedEdgeAndRegistryProjectionRemainConsistent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var failure = new InvalidOperationException("admission callback failed");
        var callbackCount = 0;
        context.WithService(() => new DelegatePropertyAttachHandler(change =>
        {
            if (change.Property.Name != "Poison") return;
            callbackCount++;
            throw failure;
        }));
        var root = new Person();
        root.AttachToContext(context);
        var child = new Person();
        var property = new PropertyReference(root, "Poison");

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AddProperties(
            new SubjectPropertyMetadata("Poison", typeof(Person), [], _ => child, null,
                isIntercepted: true, isDynamic: true)));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(1, callbackCount);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Poison"));
        Assert.Same(child, context.TryGetLifecycleInterceptor()!.Graph.GetBaseline(property));
        AssertAttached(context, root, 0);
        AssertAttached(context, child, 1);
        AssertEdge(context, child, property);
        Assert.Equal(2, Registry(context).KnownSubjects.Count);

        // Act
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, root, child);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    private static InvalidOperationException FailAttach(IInterceptorSubjectContext context, IInterceptorSubject target)
    {
        var failure = new InvalidOperationException("attach callback failed");
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, target)) throw failure;
        };
        return failure;
    }

    private static ISubjectRegistry Registry(IInterceptorSubjectContext context) => context.GetService<ISubjectRegistry>();

    private static void AssertEdge(IInterceptorSubjectContext context, IInterceptorSubject child, PropertyReference property, object? index = null)
    {
        Assert.Contains(child.GetParents(), parent => parent.Property.Equals(property) && Equals(parent.Index, index));
        var registered = Registry(context).TryGetRegisteredSubject(child)!;
        Assert.Contains(registered.Parents, parent => parent.Property.Reference.Equals(property) && Equals(parent.Index, index));
        var registeredProperty = Registry(context).TryGetRegisteredSubject(property.Subject)!.TryGetProperty(property.Name)!;
        Assert.Contains(registeredProperty.Children, item => ReferenceEquals(item.Subject, child) && Equals(item.Index, index));
    }

    private sealed class HookEnumerable(IEnumerable<Person> values, Action onEnumeration) : IEnumerable<Person>
    {
        public IEnumerator<Person> GetEnumerator()
        {
            onEnumeration();
            return values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
