using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class MetadataEnumerationTopologyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenMetadataEnumerationChangesAttachment_ThenAdmissionUsesTheResultingOwnership(bool initiallyAttached)
    {
        // Arrange
        var propertyAttachCount = 0;
        var context = InterceptorSubjectContext.Create().WithRegistry()
            .WithService(() => new PropertyObserver(() => propertyAttachCount++));
        var root = (IInterceptorSubject)new Person();
        var child = new Person();
        var enumerationCount = 0;
        if (initiallyAttached) root.AttachToContext(context);

        IEnumerable<SubjectPropertyMetadata> Metadata()
        {
            enumerationCount++;
            if (initiallyAttached) root.DetachFromContext(context);
            else root.AttachToContext(context);
            yield return new SubjectPropertyMetadata("Added", typeof(Person), [], _ => child, null,
                isIntercepted: true, isDynamic: true);
        }

        // Act
        root.AddProperties(Metadata());

        // Assert
        Assert.Equal(1, enumerationCount);
        Assert.True(root.Properties.ContainsKey("Added"));
        Assert.Same(initiallyAttached ? null : context, root.TryGetContext());
        Assert.Same(initiallyAttached ? null : context, child.TryGetContext());
        Assert.Equal(initiallyAttached ? 0 : 1, child.GetReferenceCount());
        Assert.Equal(initiallyAttached ? 0 : 1, propertyAttachCount);
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Equal(!initiallyAttached, registry.TryGetRegisteredSubject(child) is not null);
        if (!initiallyAttached) root.DetachFromContext(context);
        Assert.Null(root.TryGetContext());
        Assert.Null(child.TryGetContext());
        Assert.Null(registry.TryGetRegisteredSubject(root));
        Assert.Null(registry.TryGetRegisteredSubject(child));
    }

    private sealed class PropertyObserver(Action attached) : IPropertyLifecycleHandler
    {
        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (change.Property.Name == "Added") attached();
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change) { }
    }
}
