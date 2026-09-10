using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Dynamic.Tests;

public interface IMotor
{
    int Speed { get; set; }
}

public interface ISensor
{
    int Temperature { get; set; }
}

public interface IMotorHolder
{
    Motor? Motor { get; set; }
}

[InterceptorSubject]
public partial class Motor : IMotor
{
    public Motor()
    {
        Speed = 100;
    }
    
    public partial int Speed { get; set; }
}

public class DynamicSubjectTests
{
    [Fact]
    public void WhenCreatingDynamicSubject_ThenItImplementsInterfaces()
    {
        // Act
        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        var motor = (IMotor)subject;
        var sensor = (ISensor)subject;

        // Assert
        motor.Speed = 100;
        sensor.Temperature = 25;

        Assert.Equal(100, motor.Speed);
        Assert.Equal(25, sensor.Temperature);
    }
    
    [Fact]
    public void WhenCreatingDynamicSubject_ThenRegistryKnowsProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        subject.AttachToContext(context);

        // Act
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var properties = registeredSubject.Properties;

        // Assert
        Assert.Contains(properties, p => p.Name == "Speed");
        Assert.Contains(properties, p => p.Name == "Temperature");
    }
    
    [Fact]
    public Task WhenInterceptingDynamicSubject_ThenTheyAreCalled()
    {
        // Act
        var logs = new List<string>();
        
        // Two read/write interceptors pin the nesting order; the lifecycle is registered once,
        // because a second one would be a competing ownership authority on the same context.
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestLifecycleInterceptor("lifecycle", logs), _ => false)
            .WithService(() => new TestInterceptor("a", logs), _ => false)
            .WithService(() => new TestInterceptor("b", logs), _ => false);

        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        subject.AttachToContext(context);

        var motor = (IMotor)subject;
        var sensor = (ISensor)subject;

        // Act
        motor.Speed = 100;
        sensor.Temperature = 25;
        var speed = motor.Speed;
        var temperature = sensor.Temperature;
        
        subject.DetachFromContext(context);

        // Assert & Act (read)
        Assert.Equal(102, motor.Speed);
        Assert.Equal(27, sensor.Temperature);
        
        // Assert
        return Verify(logs);
    }
    
    [Fact]
    public void WhenCreatingDynamicSubjectForClass_ThenItImplementsInterfaces()
    {
        // Arrange
        var motor = DynamicSubjectFactory.CreateSubject<Motor>(typeof(IMotor), typeof(ISensor));
        var motorFromInterface = (IMotor)motor;
        var sensor = (ISensor)motor;

        // Act
        sensor.Temperature = 5;
        
        // Assert
        // 100 from class (direct call on class)
        Assert.Equal(100, motor.Speed);

        // 100 from class (should redirect interface call to class)
        Assert.Equal(100, motorFromInterface.Speed); 
        
        // 5 from dynamic store (interface not implemented)
        Assert.Equal(5, sensor.Temperature);
    }

    [Fact]
    public void WhenADynamicStructuralPropertyIsWrittenOnAnAttachedProxy_ThenTheSubjectJoinsTheGraph()
    {
        // Arrange: the Castle set path classifies the declared type at runtime and routes a
        // subject-bearing write through the synchronized structural accessor.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotorHolder));
        subject.AttachToContext(context);
        var holder = (IMotorHolder)subject;
        var motor = new Motor();

        // Act
        holder.Motor = motor;

        // Assert
        Assert.Same(context, motor.TryGetContext());
        Assert.Equal(1, motor.GetReferenceCount());

        // Act
        holder.Motor = null;

        // Assert
        Assert.Null(motor.TryGetContext());
        Assert.Equal(0, motor.GetReferenceCount());
    }

    [Fact]
    public void WhenADynamicStructuralPropertyIsWrittenBeforeAttach_ThenTheAttachDiscoversIt()
    {
        // Arrange: an unattached proxy writes through the structural accessor's unattached arm,
        // and the later attach discovers the stored subject through the property's getter.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotorHolder));
        var holder = (IMotorHolder)subject;
        var motor = new Motor();
        holder.Motor = motor;

        // Act
        subject.AttachToContext(context);

        // Assert
        Assert.Same(context, motor.TryGetContext());
        Assert.Equal(1, motor.GetReferenceCount());
    }

    [Fact]
    public void WhenProxyingAGeneratedSubject_ThenNoGeneratedInterceptionMemberBecomesAProperty()
    {
        // Arrange & Act: Motor is [InterceptorSubject], so the proxy's base is generated code.
        var motor = DynamicSubjectFactory.CreateSubject<Motor>(typeof(IMotor), typeof(ISensor));

        // Assert: DynamicSubjectFactory turns every reflected instance property that is not already
        // known into an intercepted subject property, and GetProperties(Instance | Public |
        // NonPublic) returns inherited protected properties. A generated subject has no protected
        // instance property today, which is exactly why nothing catches a new one. The set is
        // asserted exactly rather than by name, so a leak under any name fails here.
        var propertyNames = ((IInterceptorSubject)motor).Properties.Keys.OrderBy(name => name);
        Assert.Equal(["Speed", "Temperature"], propertyNames);
    }

    [Fact]
    public void WhenProxyingAGeneratedSubject_ThenExecutorDoesNotBecomeAProperty()
    {
        // Arrange & Act: the focused half of the exact-set assertion above. Executor is emitted as
        // an explicit implementation precisely so the factory's GetProperties(Instance | Public |
        // NonPublic) sweep cannot see it; a public or protected Executor would surface here.
        var motor = DynamicSubjectFactory.CreateSubject<Motor>(typeof(IMotor), typeof(ISensor));

        // Assert
        Assert.DoesNotContain(
            ((IInterceptorSubject)motor).Properties.Keys,
            name => name.EndsWith("Executor", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenDynamicSubjectIsCreated_ThenExecutorIsAnExplicitImplementation()
    {
        // Arrange
        var subject = (IInterceptorSubject)new DynamicSubject();

        // Act & Assert: no simple-named Executor property exists at any accessibility, so the
        // dynamic factory cannot turn it into a phantom intercepted property.
        Assert.Null(typeof(DynamicSubject).GetProperty(
            "Executor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(subject.Executor);
    }

    public class TestLifecycleInterceptor(string name, List<string> logs) : ILifecycleInterceptor
    {
        private readonly object _structuralWriteGate = new();

        public void EnterStructuralWriteGate() => Monitor.Enter(_structuralWriteGate);

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        // Attaches through the public raw seam, which is the whole third-party contract: no
        // Core internals are needed to implement a lifecycle.
        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            executor.TryUpdateAttachment(revision, context, anchor, out _);
            logs.Add($"{name}: Attached");
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _);
            logs.Add($"{name}: Detached");
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }

    public class TestInterceptor : IReadInterceptor, IWriteInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before read {context.Property.Name}");
            var result = next(ref context);
            _logs.Add($"{_name}: After read {context.Property.Name}");
            return result;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before write {context.Property.Name}");
            context.NewValue = (TProperty)(object)((int)((object)context.NewValue!) + 1);
            next(ref context);
            _logs.Add($"{_name}: After write {context.Property.Name}");
        }

    }
}