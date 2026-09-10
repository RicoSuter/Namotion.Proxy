using System.Reflection;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins the emitted IInterceptorSubject.Executor as an explicit implementation.
/// DynamicSubjectFactory reflects over GetProperties(Instance | Public | NonPublic) and turns
/// every unknown property into an intercepted subject property; a public or protected Executor
/// would give every Castle-proxied generated subject a phantom property.
/// </summary>
public class GeneratedExecutorTests
{
    [Fact]
    public void WhenSubjectIsGenerated_ThenExecutorIsAnExplicitImplementation()
    {
        // Arrange
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Sample
                {
                    public partial string Name { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunForExecution(source);
        var sampleType = result.LoadAssembly().GetType("Repro.Sample");
        Assert.NotNull(sampleType);
        var instance = (IInterceptorSubject)result.CreateInstance("Repro.Sample");

        // Assert: no simple-named Executor property exists at any accessibility, only the
        // explicitly implemented slot.
        Assert.Null(sampleType.GetProperty("Executor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(sampleType.GetProperty("Namotion.Interceptor.IInterceptorSubject.Executor", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(instance.Executor);
    }
}
