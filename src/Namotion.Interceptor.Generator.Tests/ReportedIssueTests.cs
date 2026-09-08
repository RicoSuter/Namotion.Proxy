using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

// Shape reported in https://github.com/RicoSuter/Namotion.Interceptor/issues/428, renamed:
// an enum, a base interface whose property is named after its own type, a sub-interface that
// supplies the value through an explicit implementation, and an empty subject class with no
// namespace.

public enum Rank { Junior, Senior }

public interface IEmployee
{
    Rank Rank { get; }
}

public interface ISenior : IEmployee
{
    Rank IEmployee.Rank => Rank.Senior;
}

[InterceptorSubject]
public partial class Alice : ISenior
{
}

namespace Namotion.Interceptor.Generator.Tests
{
    public class ReportedIssueTests
    {
        [Fact]
        public void WhenSubjectInheritsExplicitImplementationFromSubInterface_ThenPropertyIsExposed()
        {
            // Arrange
            var alice = new Alice();

            // Act
            var properties = ((IInterceptorSubject)alice).Properties;

            // Assert
            Assert.True(properties.ContainsKey("Rank"));
            Assert.Equal(Rank.Senior, properties["Rank"].GetValue?.Invoke(alice));
        }

        [Fact]
        public void WhenSubjectInheritsExplicitImplementationFromSubInterface_ThenPropertyIsNotIntercepted()
        {
            // Arrange
            var alice = new Alice();

            // Act
            var metadata = ((IInterceptorSubject)alice).Properties["Rank"];

            // Assert: an explicitly implemented member cannot be routed through the executor
            Assert.False(metadata.IsIntercepted);
            Assert.Equal("Rank", metadata.PropertyInfo?.Name);
        }
    }
}
