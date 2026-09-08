using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Tests;

public class VirtualPropertyIntegrationTests
{
    [Fact]
    public void VirtualProperty_WithTracking_WorksCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();

        var person = new VirtualPerson(context) { Name = "John" };

        // Act - Change virtual property
        person.Name = "Jane";
        
        // Assert - Property was changed
        Assert.Equal("Jane", person.Name);
    }
    
    [Fact]
    public void OverrideProperty_WithInterception_WorksCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();

        var employee = new VirtualEmployee(context) 
        { 
            Name = "John",
            Department = "Engineering"
        };

        // Act - Change override property
        employee.Name = "Jane";
        employee.Department = "Sales";
        
        // Assert - Both properties work
        Assert.Equal("Jane", employee.Name);
        Assert.Equal("Sales", employee.Department);
    }
    
    [Fact]
    public void VirtualPropertyChain_WithMultipleLevels_WorksCorrectly()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();

        var manager = new VirtualManager(context) 
        { 
            Name = "Alice",
            Department = "IT",
            TeamSize = 5
        };

        // Act
        manager.Name = "Bob";
        
        // Assert - Entire chain works
        Assert.Equal("Bob", manager.Name);
        Assert.Equal(5, manager.TeamSize);
    }

    [Fact]
    public void WhenWritingThroughAThreeLevelHierarchy_ThenBaseAndDerivedWritesAreIntercepted()
    {
        // Arrange: the value alone proves nothing here, because a base-declared property kept its
        // value even while base-declared writes bypassed the interceptor chain.
        var writeInterceptor = new RecordingWriteInterceptor();
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(() => writeInterceptor);

        var manager = new VirtualManager(context);

        // Act
        manager.Name = "Rico";
        manager.Department = "Engineering";
        manager.TeamSize = 4;

        // Assert
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Name");
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "Department");
        Assert.Contains(writeInterceptor.Writes, write => write.PropertyName == "TeamSize");
    }

    private sealed class RecordingWriteInterceptor : IWriteInterceptor
    {
        public List<(string PropertyName, object? Value)> Writes { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Writes.Add((context.Property.Name, context.NewValue));
            next(ref context);
        }
    }
}

[InterceptorSubject]
public partial class VirtualPerson
{
    public virtual partial string Name { get; set; }
}

[InterceptorSubject]
public partial class VirtualEmployee : VirtualPerson
{
    public override partial string Name { get; set; }
    public partial string Department { get; set; }
}

[InterceptorSubject]
public partial class VirtualManager : VirtualEmployee
{
    public partial int TeamSize { get; set; }
}
