using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Pins the write routing of dynamic properties added through
/// <see cref="RegisteredSubject.AddProperty"/>: their metadata setter loses the compile-time
/// property type (values travel boxed), so the routing must come from the declared type given at
/// registration. A scalar-declared dynamic property (source telemetry, say) must write on the
/// scalar route without paying the lifecycle gate, while a subject-capable one must take the full
/// structural protocol.
/// </summary>
public class DynamicPropertyWriteRoutingTests
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
            var executor = subject.Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            executor.TryUpdateAttachment(revision, context, anchor, out _);
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _);
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref WritePropertyCount);
            next(ref context);
        }
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeIsScalar_ThenWriteTakesNoLifecycleGate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new GateCountingLifecycle();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        var storedValue = 0.0;
        registeredSubject.AddProperty(
            "Temperature",
            typeof(double),
            _ => storedValue,
            (_, value) => storedValue = (double)value!);

        // Registration publishes the initial value as a null-to-value write, which a double chain
        // cannot carry and which therefore routes as object. That one-off is not what this test
        // pins: the invariant is the per-update cost of a scalar-declared dynamic property.
        var gateEntriesAfterRegistration = probe.GateEnterCount;
        var writesAfterRegistration = probe.WritePropertyCount;

        // Act
        person.Properties["Temperature"].SetValue!(person, 42.0);

        // Assert: the scalar declared type keeps the dynamic write off the structural protocol
        // even though the value arrives boxed; the chain itself still runs.
        Assert.Equal(gateEntriesAfterRegistration, probe.GateEnterCount);
        Assert.Equal(writesAfterRegistration + 1, probe.WritePropertyCount);
        Assert.Equal(42.0, storedValue);
    }

    [Fact]
    public void WhenDynamicPropertyDeclaredTypeCanContainSubjects_ThenWriteTakesTheLifecycleGate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new GateCountingLifecycle();
        context.AddService(probe);

        IInterceptorSubject person = new Person();
        person.AttachToContext(context);

        var registeredSubject = new RegisteredSubject(person);
        object? storedValue = null;
        registeredSubject.AddProperty(
            "Buddy",
            typeof(Person),
            _ => storedValue,
            (_, value) => storedValue = value);

        IInterceptorSubject buddy = new Person();

        // Act
        person.Properties["Buddy"].SetValue!(person, buddy);

        // Assert: the subject-capable declared type takes the structural protocol, the gate
        // before the chain, so a racing attach or detach orders against this write. Registration
        // issues no initial write for a subject-capable property, so this is the only one.
        Assert.Equal(1, probe.GateEnterCount);
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Same(buddy, storedValue);
    }
}
