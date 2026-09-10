using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// End-to-end pin for hand-written subjects against the real lifecycle: a setter that calls the
/// one SetPropertyValue entry on a subject-typed property must behave exactly like a generated
/// structural setter, attaching the assigned child and updating the parent edges.
/// </summary>
public class HandWrittenSubjectLifecycleTests
{
    /// <summary>
    /// A subject implemented by hand, the way a consumer without the source generator writes one:
    /// every setter calls SetPropertyValue, whatever the property type.
    /// </summary>
    private sealed class HandWrittenDevice : IInterceptorSubject
    {
        private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(HandWrittenDevice),
                    [],
                    static subject => ((HandWrittenDevice)subject)._child,
                    static (subject, value) => ((HandWrittenDevice)subject).Child = (HandWrittenDevice?)value,
                    isIntercepted: true,
                    isDynamic: false),
                [nameof(Name)] = new(
                    nameof(Name),
                    typeof(string),
                    [],
                    static subject => ((HandWrittenDevice)subject)._name,
                    static (subject, value) => ((HandWrittenDevice)subject).Name = (string?)value,
                    isIntercepted: true,
                    isDynamic: false)
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private HandWrittenDevice? _child;
        private string? _name;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");

        public HandWrittenDevice? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((HandWrittenDevice)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) => ((HandWrittenDevice)subject)._child = newValue);
        }

        public string? Name
        {
            get => Executor.GetPropertyValue(nameof(Name), static subject => ((HandWrittenDevice)subject)._name);
            set => Executor.SetPropertyValue(nameof(Name), value, _name,
                static (subject, newValue) => ((HandWrittenDevice)subject)._name = newValue);
        }
    }

    [Fact]
    public void WhenHandWrittenSetterAssignsAChildSubject_ThenTheChildAttachesAndTheParentEdgeAppears()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        var parent = new HandWrittenDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var child = new HandWrittenDevice();

        // Act
        parent.Child = child;

        // Assert: the assigned child is owned by the parent's context and the ownership edge
        // points back at the parent through the written property.
        Assert.Same(context, ((IInterceptorSubject)child).TryGetContext());
        var parents = ((IInterceptorSubject)child).GetParents();
        var edge = Assert.Single(parents);
        Assert.Same(parent, edge.Property.Subject);
        Assert.Equal(nameof(HandWrittenDevice.Child), edge.Property.Name);

        // Act: clearing the edge releases the child.
        parent.Child = null;

        // Assert
        Assert.Null(((IInterceptorSubject)child).TryGetContext());
        Assert.Empty(((IInterceptorSubject)child).GetParents());
    }
}
