namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Covers the hint-name helper the generator's catch block uses when extraction or code
/// generation throws. AddSource requires a hint name that is unique within the run; two failing
/// subjects sharing a bare class name (or a failing "N.Foo" alongside a succeeding global "Foo")
/// collide on the old bare-class-name naming and make AddSource throw ArgumentException from
/// inside the catch block itself, which Roslyn turns into CS8785 and drops every generated file
/// for the run. This is exercised as a direct unit test of the naming function rather than through
/// the full generator pipeline: nothing in the extractor or code generator can be made to throw
/// through valid or invalid-but-parseable C# source, since every known failure mode already
/// returns a diagnostic instead of throwing, so an end-to-end reproduction of the catch block
/// itself would require a fault injected into production code purely for testability.
/// </summary>
public class GeneratorFailureHintNameTests
{
    [Fact]
    public void WhenTwoFailingSubjectsShareABareClassName_ThenHintNamesDiffer()
    {
        // Arrange: two distinct fully-qualified types whose bare class name collides.
        const string first = "global::N1.Foo";
        const string second = "global::N2.Foo";

        // Act
        var firstHintName = InterceptorSubjectGenerator.GetFailureHintName(first);
        var secondHintName = InterceptorSubjectGenerator.GetFailureHintName(second);

        // Assert
        Assert.NotEqual(firstHintName, secondHintName);
    }

    [Fact]
    public void WhenAFailingNamespacedTypeSharesANameWithASucceedingGlobalType_ThenHintNamesDiffer()
    {
        // Arrange: GetFileName's own namespace-qualified naming never produces this collision, but
        // the bare class name used by the old catch-block naming did.
        const string namespaced = "global::N.Foo";
        const string globalNamespace = "global::Foo";

        // Act
        var namespacedHintName = InterceptorSubjectGenerator.GetFailureHintName(namespaced);
        var globalHintName = InterceptorSubjectGenerator.GetFailureHintName(globalNamespace);

        // Assert
        Assert.NotEqual(namespacedHintName, globalHintName);
    }

    [Theory]
    [InlineData("global::Foo")]
    [InlineData("global::N.Foo")]
    [InlineData("global::N.Foo<string>")]
    [InlineData("global::N.Outer.Inner")]
    public void WhenTypeNameContainsCharactersAddSourceRejects_ThenHintNameContainsOnlyValidCharacters(string fullyQualifiedTypeName)
    {
        // Act
        var hintName = InterceptorSubjectGenerator.GetFailureHintName(fullyQualifiedTypeName);

        // Assert
        Assert.DoesNotContain(':', hintName);
        Assert.DoesNotContain('<', hintName);
        Assert.DoesNotContain('>', hintName);
        Assert.DoesNotContain(',', hintName);
        Assert.EndsWith(".g.cs", hintName);
    }
}
