using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests;

public class PlainOriginBase
{
    public string Origin => "base";
}

[InterceptorSubject]
public partial class NarrowedOriginSubject : PlainOriginBase
{
    public new partial int Origin { get; set; }
}

public class PropertyPrecedenceTests
{
    [Fact]
    public void WhenNewPropertyTypeDiffersFromTheHiddenOne_ThenPropertiesDoesNotThrow()
    {
        // Arrange
        var subject = new NarrowedOriginSubject();

        // Act
        var properties = ((IInterceptorSubject)subject).Properties;

        // Assert
        Assert.Equal(typeof(int), properties["Origin"].Type);
        Assert.Equal(typeof(NarrowedOriginSubject), properties["Origin"].PropertyInfo?.DeclaringType);
    }
}
