using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Lifecycle.SupportContractAssertions;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CommitThenNotifyBoundaryTests
{
    [Fact]
    public void WhenAnAttachCallbackPromotesAnAdoptedSubjectAndThrows_ThenTheExplicitAnchorSurvivesParentRemoval()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var grandchild = new Person();
        var provisional = new Person(context) { Father = grandchild };
        var refusedChild = new Person();
        var root = new Person { Father = provisional, Mother = refusedChild };
        var failure = new InvalidOperationException("attach callback failed after promotion");
        SubjectAttachmentAnchorKind? anchorBeforePromotion = null;
        var promotionReturned = false;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (!ReferenceEquals(change.Subject, refusedChild)) return;
            anchorBeforePromotion = ((IInterceptorSubject)provisional).Executor.AttachmentAnchor;
            provisional.AttachToContext(context);
            promotionReturned = true;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.True(promotionReturned);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchorBeforePromotion);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        AssertAttached(context, root, 0);
        AssertAttached(context, provisional, 1);
        AssertAttached(context, grandchild, 1);
        AssertAttached(context, refusedChild, 1);
        Assert.Equal(4, Registry(context).KnownSubjects.Count);

        // Act
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, root, refusedChild);
        AssertAttached(context, provisional, 0);
        AssertAttached(context, grandchild, 1);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        Assert.Equal(2, Registry(context).KnownSubjects.Count);
        Assert.Same(grandchild, Assert.Single(Registry(context).TryGetRegisteredSubject(provisional)!
            .TryGetProperty(nameof(Person.Father))!.Children).Subject);

        // Act
        provisional.DetachFromContext(context);

        // Assert
        AssertReleased(context, provisional, grandchild);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenAnExplicitDetachCallbackThrows_ThenTheEntireComponentIsReleasedBeforeTheFailureEscapes()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var grandchild = new Person();
        var child = new Person { Father = grandchild };
        var root = new Person { Father = child };
        root.AttachToContext(context);
        var failure = new InvalidOperationException("detach callback failed");
        var callbackCount = 0;
        IInterceptorSubjectContext? callbackContext = null;
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, child)) return;
            callbackCount++;
            callbackContext = child.GetContext();
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.DetachFromContext(context));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(1, callbackCount);
        Assert.Same(context, callbackContext);
        AssertReleased(context, root, child, grandchild);
        Assert.Empty(Registry(context).KnownSubjects);

        // Act
        var laterRoot = new Person { Father = new Person() };
        laterRoot.AttachToContext(context);
        laterRoot.DetachFromContext(context);

        // Assert
        Assert.Equal(1, callbackCount);
        AssertReleased(context, laterRoot, laterRoot.Father!);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    [Fact]
    public void WhenADetachCallbackAdmitsMetadataOnAReleasingHost_ThenNoEdgeOrRegistryResidueConsumesTheProvisionalRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new Person();
        root.AttachToContext(context);
        var host = new Person();
        var sibling = new Person();
        root.Children = [host, sibling];
        var grandchild = new Person();
        var provisional = new Person(context) { Father = grandchild };
        var revision = ((IInterceptorSubject)provisional).Executor.AttachmentRevision;
        var graph = context.TryGetLifecycleInterceptor()!.Graph;
        var admittedProperty = new PropertyReference(host, "Adopted");
        var admittedCallbacks = 0;
        context.WithService(() => new DelegatePropertyAttachHandler(change =>
        {
            if (change.Property.Equals(admittedProperty)) admittedCallbacks++;
        }));
        var admissionReturned = false;
        var hostWasReleasing = false;
        var hostReferences = -1;
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (!ReferenceEquals(change.Subject, sibling)) return;
            hostWasReleasing = graph.IsReleasing(host) && !graph.IsOwned(host) &&
                               ReferenceEquals(host.TryGetContext(), context);
            hostReferences = host.GetReferenceCount();
            ((IInterceptorSubject)host).AddProperties(new SubjectPropertyMetadata(
                "Adopted", typeof(Person), [], _ => provisional, null, isIntercepted: true, isDynamic: true));
            admissionReturned = true;
        };

        // Act
        root.Children = [];

        // Assert
        Assert.True(admissionReturned);
        Assert.True(hostWasReleasing);
        Assert.Equal(0, hostReferences);
        Assert.Equal(0, admittedCallbacks);
        Assert.True(((IInterceptorSubject)host).Properties.ContainsKey("Adopted"));
        Assert.False(graph.HasBaseline(admittedProperty));
        AssertReleased(context, host, sibling);
        AssertAttached(context, root, 0);
        AssertAttached(context, provisional, 0);
        AssertAttached(context, grandchild, 1);
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, ((IInterceptorSubject)provisional).Executor.AttachmentAnchor);
        Assert.Equal(revision, ((IInterceptorSubject)provisional).Executor.AttachmentRevision);
        Assert.Equal(3, Registry(context).KnownSubjects.Count);

        // Act
        provisional.AttachToContext(context);
        provisional.DetachFromContext(context);
        root.DetachFromContext(context);

        // Assert
        AssertReleased(context, provisional, grandchild, root);
        Assert.Empty(Registry(context).KnownSubjects);
    }

    private static ISubjectRegistry Registry(IInterceptorSubjectContext context) => context.GetService<ISubjectRegistry>();

}
