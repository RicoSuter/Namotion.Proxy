using Namotion.Interceptor.Generator.Tests.Models;

namespace Namotion.Interceptor.Generator.Tests;

public class PropertyChangedInheritanceTests
{
    [Fact]
    public void WhenSettingBaseClassProperty_ThenPropertyChangedIsFired()
    {
        // Arrange
        var employee = new Employee();
        var firedEvents = new List<string>();
        employee.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        employee.FirstName = "Rico";

        // Assert
        Assert.Contains("FirstName", firedEvents);
    }

    [Fact]
    public void WhenSettingDerivedClassProperty_ThenPropertyChangedIsFired()
    {
        // Arrange
        var employee = new Employee();
        var firedEvents = new List<string>();
        employee.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        employee.Department = "Engineering";

        // Assert
        Assert.Contains("Department", firedEvents);
    }

    [Fact]
    public void WhenBaseListIsOnADifferentPartialDeclaration_ThenSubjectExposesBaseAndOwnProperties()
    {
        // Arrange
        var contractor = new Contractor();
        var firedEvents = new List<string>();
        contractor.PropertyChanged += (_, e) => firedEvents.Add(e.PropertyName!);

        // Act
        contractor.FirstName = "Rico";
        contractor.Agency = "Acme";
        var properties = ((IInterceptorSubject)contractor).Properties;

        // Assert
        Assert.True(properties.ContainsKey("FirstName"));
        Assert.True(properties.ContainsKey("Agency"));
        Assert.Equal(["FirstName", "Agency"], firedEvents);
    }

    [Fact]
    public void WhenSettingBothProperties_ThenBothEventsAreFired()
    {
        // Arrange
        var employee = new Employee();
        var firedEvents = new List<string>();
        employee.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        employee.FirstName = "Rico";
        employee.Department = "Engineering";

        // Assert
        Assert.Equal(2, firedEvents.Count);
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("Department", firedEvents);
    }
}
