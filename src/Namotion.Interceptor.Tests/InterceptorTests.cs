using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class InterceptorTests
{
    [Fact]
    public Task WhenReadingProperties_ThenInterceptorsAreCalledInTheRightOrder()
    {
        // Arrange
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestReadInterceptor("a", logs), _ => false)
            .WithService(() => new TestReadInterceptor("b", logs), _ => false);
        
        var car = new Car(context);

        // Act
        var speed = car.Speed;

        // Assert
        return Verify(logs);
    }

    public class TestReadInterceptor : IReadInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestReadInterceptor(string name, List<string> logs)
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
    }
    
    [Fact]
    public Task WhenWritingProperties_ThenInterceptorsAreCalledInTheRightOrder()
    {
        // Arrange
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestWriteInterceptor("a", logs), _ => false)
            .WithService(() => new TestWriteInterceptor("b", logs), _ => false);
        
        var car = new Car(context);

        // Act
        car.Speed = 5;

        // Assert
        Assert.Equal(7, car.Speed); // both interceptors added 1
        return Verify(logs);
    }

    public class TestWriteInterceptor : IWriteInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestWriteInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before write {context.Property.Name}");
            context.NewValue = (TProperty)(object)((int)((object)context.NewValue!) + 1);
            next(ref context);
            _logs.Add($"{_name}: After write {context.Property.Name}");
        }
    }
    
    [Fact]
    public Task WhenAddingAndRemovingContext_ThenTheLifecycleInterceptorIsCalled()
    {
        // Arrange: one lifecycle interceptor per context. A second one is a singleton conflict,
        // because two of them would be competing authorities over the same subjects.
        var logs = new List<string>();

        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestLifecycleInterceptor("a", logs), _ => false);

        // Act
        var car = new Car(context);
        ((IInterceptorSubject)car).DetachFromContext(context);

        // Assert
        return Verify(logs);
    }

    [Fact]
    public void WhenASecondLifecycleInterceptorIsRegistered_ThenTheSingletonContractRejectsIt()
    {
        // Arrange
        var logs = new List<string>();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestLifecycleInterceptor("a", logs), _ => false);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => context.WithService(() => new TestLifecycleInterceptor("b", logs), _ => false));
    }

    public class TestLifecycleInterceptor : ILifecycleInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        private readonly object _structuralWriteGate = new();

        public void EnterStructuralWriteGate() => Monitor.Enter(_structuralWriteGate);

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        public TestLifecycleInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            _logs.Add($"{_name}: Attached");
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            _logs.Add($"{_name}: Detached");
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }
    
    [Fact]
    public Task WhenReadingMetadata_ThenItShouldBeCorrect()
    {
        // Arrange
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestReadInterceptor("a", logs), _ => false)
            .WithService(() => new TestReadInterceptor("b", logs), _ => false);
        
        var car = new Car(context) as IInterceptorSubject;

        // Act
        var properties = car.Properties;

        // Assert
        return Verify(properties);
    }
}