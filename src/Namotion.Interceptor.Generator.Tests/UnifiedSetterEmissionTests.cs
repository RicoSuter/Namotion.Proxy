using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins that every generated setter calls the one SetPropertyValue accessor helper, whatever the
/// declared property type: the write routes at runtime on TProperty inside the executor, so the
/// generator emits no routing and no structural accessor helper.
/// </summary>
public class UnifiedSetterEmissionTests
{
    [Fact]
    public void WhenPropertiesSpanScalarAndSubjectBearingTypes_ThenEverySetterCallsTheUnifiedHelper()
    {
        // Arrange: scalar allowlist samples next to every shape the old generation-time routing
        // sent through the structural helper (same-compilation subject, object, interface,
        // subject collection).
        const string source = """
            using System;
            using System.Collections.Generic;
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                public interface IDevice
                {
                }

                [InterceptorSubject]
                public partial class Child
                {
                    public partial string Name { get; set; }
                }

                [InterceptorSubject]
                public partial class Parent
                {
                    public partial int Count { get; set; }
                    public partial string Title { get; set; }
                    public partial DateTimeOffset? UpdatedAt { get; set; }
                    public partial Child? Node { get; set; }
                    public partial object? Payload { get; set; }
                    public partial IDevice? Device { get; set; }
                    public partial List<Child> Children { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.RunExpectingNoWarnings(source);
        var parent = Assert.Single(result.Sources, s => s.HintName.Contains("Repro.Parent.g.cs")).SourceText.ToString();

        // Assert
        foreach (var propertyName in new[]
                 {
                     "Count", "Title", "UpdatedAt", "Node", "Payload", "Device", "Children"
                 })
        {
            Assert.Contains($"&& SetPropertyValue(nameof({propertyName})", parent);
        }

        Assert.DoesNotContain("SetStructuralPropertyValue", parent);
    }

    [Fact]
    public void WhenPropertyTypeIsUnresolved_ThenTheSetterStillCallsTheUnifiedHelper()
    {
        // Arrange: the type does not exist, so the compilation has errors by construction, but the
        // generator still emits the same setter shape as for any other type.
        const string source = """
            using Namotion.Interceptor;
            using Namotion.Interceptor.Attributes;

            namespace Repro
            {
                [InterceptorSubject]
                public partial class Holder
                {
                    public partial UndefinedWidget Widget { get; set; }
                }
            }
            """;

        // Act
        var result = GeneratorTestHost.Run(source);
        var generated = result.SingleSource();

        // Assert
        Assert.Contains("&& SetPropertyValue(nameof(Widget)", generated);
        Assert.DoesNotContain("SetStructuralPropertyValue", generated);
    }
}
