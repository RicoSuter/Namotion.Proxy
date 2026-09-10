using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

/// <summary>
/// Pins the write protocol for subjects written by hand instead of generated: their setters call
/// the one <see cref="IInterceptorExecutor.SetPropertyValue{TProperty}"/> entry, and the runtime
/// routing must give a subject-typed property the structural protocol (the lifecycle gate before
/// the chain) without the author choosing an accessor.
/// </summary>
public class HandWrittenSubjectWriteTests
{
    /// <summary>
    /// A minimal faithful lifecycle that counts gate entries and chain executions, so a test can
    /// observe on which side of the structural routing a write ran.
    /// </summary>
    private sealed class GateCountingLifecycle : ILifecycleInterceptor
    {
        private readonly object _structuralWriteGate = new();

        public int GateEnterCount;
        public int WritePropertyCount;

        public void EnterStructuralWriteGate()
        {
            Monitor.Enter(_structuralWriteGate);
            Interlocked.Increment(ref GateEnterCount);
        }

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            InterceptorSubjectExtensions.ApplyRootAnchor(subject, context, anchor);
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);
            executor.TryUpdateAttachment(revision, attachedContext, SubjectAttachmentAnchorKind.None, out _);
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref WritePropertyCount);
            next(ref context);
        }
    }

    /// <summary>
    /// A subject implemented by hand, the way a consumer without the source generator writes one:
    /// every setter calls SetPropertyValue, whatever the property type.
    /// </summary>
    private sealed class HandWrittenSubject : IInterceptorSubject
    {
        private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(HandWrittenSubject),
                    [],
                    static subject => ((HandWrittenSubject)subject)._child,
                    static (subject, value) => ((HandWrittenSubject)subject).Child = (HandWrittenSubject?)value,
                    isIntercepted: true,
                    isDynamic: false),
                [nameof(Count)] = new(
                    nameof(Count),
                    typeof(int),
                    [],
                    static subject => ((HandWrittenSubject)subject)._count,
                    static (subject, value) => ((HandWrittenSubject)subject).Count = (int)value!,
                    isIntercepted: true,
                    isDynamic: false)
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private HandWrittenSubject? _child;
        private int _count;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");

        public HandWrittenSubject? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((HandWrittenSubject)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) => ((HandWrittenSubject)subject)._child = newValue);
        }

        public int Count
        {
            get => Executor.GetPropertyValue(nameof(Count), static subject => ((HandWrittenSubject)subject)._count);
            set => Executor.SetPropertyValue(nameof(Count), value, _count,
                static (subject, newValue) => ((HandWrittenSubject)subject)._count = newValue);
        }
    }

    [Fact]
    public void WhenHandWrittenSetterAssignsASubjectTypedProperty_ThenTheWriteTakesTheLifecycleGate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new GateCountingLifecycle();
        context.AddService(probe);
        var subject = new HandWrittenSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        var child = new HandWrittenSubject();

        // Act
        subject.Child = child;

        // Assert: the write entered the structural protocol (gate before the chain), not just the
        // chain. A write that skips the gate still runs the chain, so both counters are needed.
        Assert.Equal(1, probe.GateEnterCount);
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Same(child, subject.Child);
    }

    [Fact]
    public void WhenHandWrittenSetterAssignsAScalarProperty_ThenTheWriteTakesNoLifecycleGate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new GateCountingLifecycle();
        context.AddService(probe);
        var subject = new HandWrittenSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Act
        subject.Count = 42;

        // Assert: the scalar route never pays for the structural protocol.
        Assert.Equal(0, probe.GateEnterCount);
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Equal(42, subject.Count);
    }
}
