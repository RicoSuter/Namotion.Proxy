using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Dynamic.Tests;

public interface IMotor
{
    int Speed { get; set; }
}

public interface ISensor
{
    int Temperature { get; set; }
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
        subject.Context.AddFallbackContext(context);

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
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestInterceptor("a", logs), _ => false)
            .WithService(() => new TestInterceptor("b", logs), _ => false)
            .WithService(() => new TestLifecycleInterceptor("lifecycle", logs), _ => false);

        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        subject.Context.AddFallbackContext(context);

        var motor = (IMotor)subject;
        var sensor = (ISensor)subject;

        // Act
        motor.Speed = 100;
        sensor.Temperature = 25;
        var speed = motor.Speed;
        var temperature = sensor.Temperature;
        
        subject.Context.RemoveFallbackContext(context);

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

    public class TestLifecycleInterceptor : ILifecycleInterceptor
    {
        private readonly List<string> _logs;
        private readonly string _name;

        public TestLifecycleInterceptor(string name, List<string> logs)
        {
            _logs = logs;
            _name = name;
        }

        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            _logs.Add($"{_name}: Attached");
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            _logs.Add($"{_name}: Detached");
        }
    }
}
